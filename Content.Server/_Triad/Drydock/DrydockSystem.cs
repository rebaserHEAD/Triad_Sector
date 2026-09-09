using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Mono.Shuttles.Components;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._NF.Station.Components;
using Content.Server.NodeContainer;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Nuke;
using Content.Server.Database;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared._Mono.Shipyard; // Triad
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ContrabandPermit;
using Content.Shared._Triad.Shipyard.Save.Contraband;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Damage;
using Content.Shared.Explosion.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Components;
using Content.Shared.NodeContainer;
using Content.Shared.Nuke;
using Content.Shared.Shuttles.Components;
using Content.Shared.Singularity.Components;
using Content.Shared.Station.Components;
using Content.Shared.Store.Components;
using Robust.Shared.Configuration;
using Robust.Shared.CPUJob.JobQueues;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Utility;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Stores a deeded grid as an engine-serialized document in the database and takes it off the map.
/// The pipeline is entirely in memory: serialize, validate, checksum, compress, file, despawn. No
/// file ever touches disk, which is the whole point of replacing the ship save system.
///
/// <para>None of those phases happens in one tick any more. Everything from the freeze onwards runs
/// a few milliseconds at a time, on a private map that was paused before the ship was moved onto it:
/// it is fine to make the one captain who pressed the button wait as long as it takes, and it is not
/// fine to stall sixty other players for a reason they cannot perceive. The ship is frozen and
/// invisible for that whole span, which is what makes an elastic duration safe rather than merely
/// slower - a sliced walk over a ship that was still flying would tear its own snapshot.</para>
/// </summary>
public sealed partial class DrydockSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private DrydockStore _store = default!;
    [Dependency] private DrydockFidelitySystem _fidelity = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private ShipSizeSystem _shipSize = default!;
    [Dependency] private ShipyardSystem _shipyard = default!;
    [Dependency] private DockingSystem _docking = default!;
    [Dependency] private StationSystem _station = default!;

    /// <summary>
    /// Components cut from the live grid before it is written, because they are derived state or
    /// hold references that rot across a reload.
    ///
    /// <para>The repair data is session-scoped: entity references and raw tile ids, regenerated
    /// against the loaded grid. Station membership must not ride the document at all, because the
    /// station is round-scoped and rebuilt on retrieve, so a serialized reference reloads as invalid
    /// and the deserializer logs an error on every single load, the validation scratch load
    /// included.</para>
    /// </summary>
    private static readonly Type[] StoreStripList =
    {
        typeof(ShipRepairDataComponent),
        typeof(StationMemberComponent),
        // A powered-down helm parks its job slots here with the station they belonged to, and the
        // station is round-scoped like the membership above: serialized, it reloads as an invalid
        // reference and logs on every load. The recreated station gets its vessel's slots anyway.
        typeof(ShuttleConsoleJobSlotsComponent),
        // Guest access granted at the helm, as raw uids of cards the guests carry away with them:
        // off-grid references that reload invalid, and a permission that should not outlast the
        // voyage. Retrieve starts with none.
        typeof(ShipGuestAccessComponent),
    };

    /// <summary>
    /// Stores <paramref name="gridUid"/> for <paramref name="ownerUserId"/>. The order is gate,
    /// depart, freeze, prepare, serialize, validate, commit, despawn, and the grid is only removed
    /// once the document is filed.
    ///
    /// <para>This method is only the wrapper. It owns three things: the two refusals that must bypass
    /// every undo, the re-entrancy sentinel, and the choice between a sliced job and running the
    /// pipeline inline. <see cref="RunStorePipeline"/> is the store.</para>
    ///
    /// <para>Identity is resolved before it is minted. A grid that already carries a
    /// <see cref="DrydockIdentityComponent"/>, whether from an earlier retrieve this round or from a
    /// previous round entirely, files a new revision against the same hull. Only a grid that has
    /// never been stored mints a fresh id. Getting this backwards forks a new independently
    /// retrievable row on every store while the old one stays retrievable too, which is unbounded
    /// duplication on the happy path.</para>
    /// </summary>
    /// <param name="berthId">
    /// The berth to land in, or null to let the store pick: the ship's own last berth if free and
    /// fitting, else the smallest free berth that fits. A named berth still has to be the owner's,
    /// free, and large enough.
    /// </param>
    /// <param name="stationUid">
    /// The station the console resolved, and where the unwind puts the ship back if anything refuses
    /// after the freeze. Null falls back to the grid's own owning station, which is the right answer
    /// for every caller that is not a console; by the time the unwind runs the grid is on a private
    /// map and has no owning station left to ask.
    /// </param>
    /// <param name="onProgress">
    /// Fired on the main thread whenever the whole integer percent changes or a phase opens. A store
    /// is elastic on purpose, so any caller with a player waiting on it is expected to pass one: the
    /// percentage is what makes an unbounded wait acceptable rather than alarming.
    /// </param>
    public async Task<(DrydockStoreResult Result, Guid? ShipId)> TryStoreShip(
        EntityUid gridUid,
        Guid ownerUserId,
        int? roundId,
        int? berthId = null,
        EntityUid? stationUid = null,
        DrydockProgressCallback? onProgress = null)
    {
        if (!_cfg.GetCVar(TriadCCVars.DrydockEnabled) || _cfg.GetCVar(TriadCCVars.DrydockReadOnly))
            return (DrydockStoreResult.Disabled, null);

        // The sentinel, before anything yields. A second store request for the same grid while this
        // one is awaiting the database would otherwise run the whole preparation again on a grid
        // mid-store and file a second revision of it. Both this refusal and the disabled one above
        // deliberately sit outside the try below: the finally removes the marker unconditionally, so
        // a second request that fell through it would strip the first store's sentinel.
        //
        // Re-entrancy is all the marker does now. It used to double as a container insertion block,
        // through what was the solution's only unfiltered subscription to the insertion attempt, so
        // every insertion anywhere on the server paid two component lookups for the length of a
        // store; a ship frozen on a private map with nobody aboard has nothing that can insert into
        // it. The window that leaves open is the one between this stamp and the freeze, which is a
        // single awaited capacity check: for that tick plus a database round trip the ship is live,
        // docked and unguarded. That is the exposure the pre-slicing code already had after its own
        // await, and it is accepted.
        if (HasComp<DrydockInProgressComponent>(gridUid))
            return (DrydockStoreResult.InProgress, null);

        EnsureComp<DrydockInProgressComponent>(gridUid);

        var ctx = new DrydockStoreContext
        {
            GridUid = gridUid,
            OwnerUserId = ownerUserId,
            RoundId = roundId,
            BerthId = berthId,
            StationUid = stationUid ?? _station.GetOwningStation(gridUid) ?? EntityUid.Invalid,
        };

        var jobId = 0;
        CancellationTokenSource? cancellation = null;

        try
        {
            // A budget of zero or less is the rollback lever on a pipeline whose deploy has no other
            // one: no job, no queue, no queue latency, and the whole store on this caller's own async
            // path, in the order it ran before slicing. It is also what the integration fixtures set,
            // because a sliced store outruns their tick pumps.
            if (TickBudgetSeconds <= 0)
            {
                var direct = await RunStorePipeline(ctx, new DrydockSyncSlice(DrydockPhases.Store, onProgress));
                return (direct.Result, direct.ShipId);
            }

            cancellation = new CancellationTokenSource();
            var job = new DrydockStoreJob(this, ctx, TickBudgetSeconds, SliceStride, onProgress, cancellation.Token);
            jobId = RegisterJob(job, cancellation);
            EnqueueJob(job);

            DrydockStoreOutcome? outcome;
            try
            {
                outcome = await job.AsTask;
            }
            catch (OperationCanceledException)
            {
                // A round restart, a shutdown, or the slice watchdog. The pipeline's own finally has
                // already run the unwind, so the ship is back where it was and nothing was filed. The
                // job wrapper cancels its task without recording an exception, so a cancelled job is
                // only ever visible here and never through its exception property.
                return (DrydockStoreResult.Cancelled, null);
            }

            // Awaiting a faulted job already rethrows, so this is the belt to that braces: the
            // recorded exception is the job's own failure signal, and a store that threw must never
            // come back as a silent refusal if those two ever stop being the same thing.
            if (job.Exception != null)
                throw job.Exception;

            return outcome is null
                ? (DrydockStoreResult.SerializeFailed, null)
                : (outcome.Result, outcome.ShipId);
        }
        finally
        {
            if (jobId != 0)
                RetireJob(jobId);
            else
                cancellation?.Dispose();

            // One frame up from where this used to sit, at the bottom of the pipeline's own finally,
            // so it now also covers a job cancelled before its body ever ran. On success the grid is
            // already queued for deletion and this is a no-op; on any refusal it re-opens the ship to
            // a second store attempt.
            if (!TerminatingOrDeleted(gridUid))
                RemCompDeferred<DrydockInProgressComponent>(gridUid);
        }
    }

    /// <summary>
    /// The store itself, from the identity stamp through the despawn, written against a tick budget.
    /// Driven either by <see cref="DrydockStoreJob"/> or, when the budget cvar is off, by
    /// <see cref="DrydockSyncSlice"/> on the caller's own async path.
    /// </summary>
    /// <remarks>
    /// <para>The only legal awaits in here are on the slice. A bare await leaves the job with no
    /// resume handle: it asserts in debug and hangs forever in release, and a hung store is a frozen
    /// ship parked on a private map for the rest of the round.</para>
    /// <para>Every suspension is a hole in the world's continuity, so every one of them is followed
    /// by <see cref="GuardStoreResume"/>. The two post-await re-checks the old shape needed are still
    /// here; they simply have siblings now.</para>
    /// </remarks>
    internal async Task<DrydockStoreOutcome> RunStorePipeline(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        var gridUid = ctx.GridUid;
        var timer = ctx.Timer;
        var jobId = JobIdOf(slice);

        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        void MarkPhase(DrydockPhase phase) => timer.Mark(DrydockPhases.Mark(phase));

        try
        {
            // Opening a phase suspends, so even the very first line of the store is already a tick
            // away from the caller that asked for it.
            await slice.Begin(DrydockPhase.Gate, 0);
            GuardStoreResume(ctx);

            // Capacity first, because it is the one gate that needs the database. The await sits
            // before any other gate has been passed and before any mutation, so nothing has to be
            // re-checked after it. A full garage refuses cheaply here; the filing transaction
            // checks again, and the unique index on the berth column makes that answer the final
            // one. Both reads are of the live grid: the cached class text on the row is never
            // load-bearing.
            var shipId = ResolveOrMintShipId(gridUid);
            ctx.ShipId = shipId;
            var sizeClass = _shipSize.GetSizeClass((gridUid, Comp<MapGridComponent>(gridUid))).ToString();

            var capacity = await slice.Await(
                _store.CheckBerthForStore(shipId, ctx.OwnerUserId, sizeClass, ctx.BerthId));

            if (capacity != DrydockBerthResult.Success)
                return new DrydockStoreOutcome(BerthRefusal(capacity), null);

            if (TerminatingOrDeleted(gridUid))
                return new DrydockStoreOutcome(DrydockStoreResult.SerializeFailed, null);

            // Hazards next, because the check mutates nothing and refusing here means nobody has
            // been moved for a store that was never going to happen. Runtime countdowns are
            // ordinary data fields that would resume on thaw, so an armed ship must be refused
            // rather than frozen.
            if (HasHazardAboard(gridUid))
                return new DrydockStoreOutcome(DrydockStoreResult.HazardAboard, null);

            // A mind must never be serialized, and a living mob does not round-trip cleanly.
            // Relocating loose occupants onto the docked station is the eventual behaviour; until
            // that exists this refuses, which is the safe direction to be stricter in.
            if (_shipyard.FoundOrganics(gridUid, mobQuery, xformQuery) is not null)
                return new DrydockStoreOutcome(DrydockStoreResult.OrganicsAboard, null);

            EnsureComp<DrydockIdentityComponent>(gridUid).ShipId = shipId;
            MarkPhase(DrydockPhase.Gate);

            var shipName = Comp<MetaDataComponent>(gridUid).EntityName;

            // The grid does not know its own vessel prototype; its station's latejoin information
            // does. Read at the top of the freeze block for two reasons: the strip further down cuts
            // station membership off the grid, and the unwind needs the vessel's priority dock tag to
            // hand a refused ship back at the same kind of berth a purchase of it would have picked.
            // Reparenting does not touch station membership - the station system subscribes to no
            // parent change - so this reads the same either side of the freeze.
            string? vesselProto = null;
            if (TryComp<StationMemberComponent>(gridUid, out var stationMember)
                && TryComp<ExtraShuttleInformationComponent>(stationMember.Station, out var vesselInfo)
                && vesselInfo.Vessel is { } vessel)
            {
                vesselProto = vessel.Id;

                if (_protoMan.TryIndex<VesselPrototype>(vessel.Id, out var vesselPrototype))
                    ctx.ReturnDockTag = vesselPrototype.PriorityDockTag;
            }

            // The departure. An observer sees what a real jump shows them, because a real jump's
            // vanish is itself a map reparent: the startup sound, then gone. Deliberately not the
            // thruster half of a real jump setup, which latches the thrusters north and would be
            // serialized that way, and deliberately not real FTL at all - that is a multi-tick state
            // machine whose updates run on the paused-skipping enumerator, so freezing mid-jump
            // stalls it forever and leaves behind exactly the component the strip below removes.
            PlayDepartureEffect(gridUid);

            // Read while the docks still exist, which is only true for one more statement. The
            // unwind's first choice of where to put the ship back is whatever it was docked to.
            ctx.ReturnTarget = FindDockedPartnerGrid(gridUid);

            // The last free refusal. Everything past this point either moves the ship or takes
            // something off it, and right here the ship is still whole and still docked, so a
            // straggler who walked aboard while the capacity check was in the database costs nothing
            // to refuse for. The old post-database counterpart of this check is gone with it: nobody
            // can walk aboard a ship on a private map.
            if (_shipyard.FoundOrganics(gridUid, mobQuery, xformQuery) is not null)
                return new DrydockStoreOutcome(DrydockStoreResult.OrganicsAboard, null);

            // Hoisted above the reparent, which is where upstream's own jump setup puts it. A stored
            // ship has to be fully detached regardless - its docking partner is not in the document,
            // so a serialized dock reloads as an invalid reference and crashes the docking system's
            // startup on every load, the validation scratch load included - but the reason it has to
            // happen HERE is that a grid still weld-jointed to a station cannot be reparented
            // cleanly.
            ctx.Undocked = true;
            _docking.UndockDocks(gridUid);

            // From here the ship is private, frozen and invisible, and every phase below runs on the
            // tick budget. The map is paused while it is still empty, so the engine's own recursive
            // pause walks one entity instead of nine hundred; the reparent onto it is then what
            // actually freezes the tree, and the walk inside the freeze counts and back-stops.
            ctx.StagingMap = CreateStagingMap(jobId, DrydockStagingKind.Store, shipId, mapInit: true);
            ctx.EntityCount = await FreezeOntoStagingMap(gridUid, ctx.StagingMap.Value, slice);
            ctx.Frozen = true;
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Freeze);

            // The saving-contraband purge, by the same component rule the ship-save path applies:
            // marked entities go unless they carry a permit. After every refusal above, so a refused
            // store deletes nothing, and before the appraisal, so the quote is for what is filed.
            // Not undoable.
            await PurgeSavingContrabandSliced(ctx, slice);
            MarkPhase(DrydockPhase.Purge);

            // The sale quote, taken while the hull is whole and before any sidecar or strip
            // touches it, so what a scrap pays is what the shipyard would have paid at this moment.
            await slice.Begin(DrydockPhase.Appraise, 0);
            GuardStoreResume(ctx);
            var appraisal = _shipyard.AppraiseHull(gridUid);
            MarkPhase(DrydockPhase.Appraise);

            // Three walks under one phase name, each opening it with its own item count. The
            // percentage is clamped monotonic, so re-opening a phase reads as a stall inside its band
            // rather than as a bar running backwards.
            //
            // A pipe net's air lives on the node-group graph, which the serializer cannot reach.
            // Distribute each net's gas across its members by volume. The live net is left alone -
            // pointlessly now, since the ship is frozen on a private map and nothing will ever read
            // it again, but writing to it would be a mutation with no undo entry for no gain.
            await InjectPipeGasSidecarsSliced(ctx, slice);

            // Damage is read-only to the serializer, so a damaged ship would come back pristine.
            await InjectDamageSidecarsSliced(ctx, slice);

            // Appearance data is not a data field at all, so no save has ever carried it and the
            // probe below cannot see it either. Without this a retrieved ship's visuals come back
            // at prototype defaults wherever the owning system does not re-derive them on startup.
            await _fidelity.CaptureAppearanceSliced(gridUid, ctx.InjectedAppearance, slice);
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Sidecars);

            await StripListedComponentsSliced(ctx, slice);

            // The grid's own deed names the card holding it, which is outside the document. Written
            // as-is it reloads as an invalid reference and the deserializer logs an error on every
            // scratch load and every retrieve; retrieve sets the holder afresh anyway. The flag goes
            // up before the call, not after: an abort between the two restores a null holder, which
            // is what the reattach would have been handed anyway.
            ctx.DeedDetached = true;
            ctx.DeedHolder = _shipyard.DetachGridDeedHolder(gridUid);

            // A PDA's store remembers the map it was set up on, which is likewise outside the
            // document and reloads as an invalid reference that logs on every load. Purchased
            // ships already leave that map behind when they dock, so a retrieved store is no
            // worse off for coming back with the field blank.
            await DetachStoreMapsSliced(ctx, slice);
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Strip);

            // The general net, after the two specific sidecars and the strip list so it sees the
            // final live component set. For every unserializable populated field it either captures
            // the value or strips it, and clears the live field either way. The ledger is created and
            // handed to the context BEFORE the walk starts: it is the only record of what was blanked
            // and on which entity, and a walk that can now be abandoned half-way through must not be
            // the thing that owns it.
            ctx.Fidelity = new DrydockFidelityCapture();
            await _fidelity.CaptureAndStripSliced(gridUid, ctx.Fidelity, slice);
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Capture);

            await slice.Begin(DrydockPhase.Prepare, 0);
            GuardStoreResume(ctx);

            // The rest of preparation is deliberately not undoable, and runs last for that reason.
            // An empty AI core is the intended end state, and a ship at rest has no business carrying
            // FTL state. The undock used to be the third member of this block and has moved up into
            // the freeze, where the reparent forces it.
            SanitizeStationAiCores(gridUid);

            // A ship stored during its FTL cooldown still carries the component the jump added. A
            // reborn ship carrying it comes back mid-jump and the shuttle system errors on it every
            // tick, which leaves it stuck.
            if (HasComp<FTLComponent>(gridUid))
                RemComp<FTLComponent>(gridUid);

            // A ship document is self-contained. The engine default drags any referenced null-space
            // entity into the save, and a ship that is its own station references that station,
            // which pulls the whole station in along with state the serializer cannot write. Ignore
            // turns those references into invalid ones, which retrieve rebinds. Transform parenting
            // is exempt, so grid children are unaffected.
            var saveOptions = new SerializationOptions { MissingEntityBehaviour = MissingEntityBehaviour.Ignore };
            MarkPhase(DrydockPhase.Prepare);

            // One atomic call, and deliberately not sliced. The engine's per-entity serialize surface
            // is public, but the wrapper around it is not reproducible from content: the truncate
            // flag has a private setter and the tile-map initialiser is private, so a content-side
            // replica logs an orphan error per store, throws on the first save:false child, and
            // re-encodes every chunk's tile ids. This is therefore one of the calls that set the real
            // per-tick ceiling - the honest claim is "the budget plus the longest bulk call", not
            // "the budget" - and the whole phase lands inside one tick.
            await slice.Begin(DrydockPhase.Serialize, 0);
            GuardStoreResume(ctx);

            string yaml;
            using (var writer = new StringWriter())
            {
                if (!_mapLoader.TrySaveGrid(gridUid, writer, saveOptions))
                    return new DrydockStoreOutcome(DrydockStoreResult.SerializeFailed, null);

                yaml = writer.ToString();
            }

            MarkPhase(DrydockPhase.Serialize);

            // The other bulk call, for the same reason: the deserializer's stages are public but
            // every per-entity loop inside them is private, so the scratch load cannot be sliced
            // below stage granularity and is not worth splitting at stage granularity either.
            await slice.Begin(DrydockPhase.Validate, 0);
            GuardStoreResume(ctx);

            if (DetectRoundTripMismatch(gridUid, yaml, jobId, shipId, out var liveEntities))
                return new DrydockStoreOutcome(DrydockStoreResult.ValidationFailed, null);

            MarkPhase(DrydockPhase.Validate);

            // Everything from here to the commit is pure byte work: no entity, no component, no map,
            // so it is the one part of a store that can leave the main thread at all. Encoding and
            // checksumming a multi-megabyte document, and compressing it, are real milliseconds that
            // the server no longer has to spend. Each hop back costs a tick of latency, which an
            // elastic store does not care about.
            //
            // Checksum the uncompressed document, so stored hashes survive a future change of
            // compression.
            await slice.Begin(DrydockPhase.Hash, 0);
            var hashed = await slice.Await(Task.Run(() =>
            {
                var bytes = Encoding.UTF8.GetBytes(yaml);
                return (Bytes: bytes, Checksum: SHA256.HashData(bytes));
            }));

            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Hash);

            await slice.Begin(DrydockPhase.Drift, 0);
            var (fingerprint, engineFormat) = await slice.Await(Task.Run(() => ReadDriftMetadata(yaml)));
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Drift);

            // The last walk of the live tree, and it has to finish before the despawn below.
            var manifest = new DrydockManifest();
            await BuildManifestSliced(ctx, slice, manifest);
            GuardStoreResume(ctx);

            var request = new DrydockRevisionRequest
            {
                ShipGuid = shipId,
                OwnerUserId = ctx.OwnerUserId,
                ShipName = shipName,
                VesselProto = vesselProto,
                SizeClass = sizeClass,
                BerthId = ctx.BerthId,
                Kind = DrydockRevisionKind.PlayerStore,
                ActorUserId = ctx.OwnerUserId,
                CreatedRoundId = ctx.RoundId,
                EngineFormatVer = engineFormat,
                ProtoFingerprint = fingerprint,
                CapturedKeyHash = ctx.Fidelity.ComputeCapturedKeyHash(),
                Checksum = hashed.Checksum,
                SizeBytes = hashed.Bytes.Length,
                AppraisedValue = appraisal,
                Manifest = manifest.Serialize(),
            };

            MarkPhase(DrydockPhase.Manifest);

            await slice.Begin(DrydockPhase.Compress, 0);
            var payload = await slice.Await(Task.Run(() => CompressZstd(hashed.Bytes)));
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Compress);

            await slice.Begin(DrydockPhase.Commit, 0);
            var filed = await slice.Await(
                _store.FileRevision(request, payload, _cfg.GetCVar(TriadCCVars.DrydockKeepBlobs)));

            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Commit);

            // The garage filled up between the capacity check and the commit, or this store lost
            // the last berth to another committing in the same instant. Nothing was filed; the
            // unwind below thaws the ship and hands it back to the station.
            if (filed.Outcome != DrydockBerthResult.Success)
                return new DrydockStoreOutcome(BerthRefusal(filed.Outcome), null);

            // The organics re-check that used to sit here is gone with the private map. It existed
            // because the write above yields and the in-progress marker blocked insertion rather than
            // boarding; there is no boarding a ship that has been on a paused map of its own since
            // long before the write started. The late re-check in the freeze block is what covers the
            // one window that is still real.
            await slice.Begin(DrydockPhase.Despawn, 0);
            GuardStoreResume(ctx);

            QueueDel(gridUid);
            ctx.Committed = true;

            // The staging map goes with the ship. Not through ScrapStagingMap, which refuses a map
            // that still carries a grid: that refusal is right everywhere except here, where the grid
            // on it is the ship we just filed and are deliberately despawning. Both are queued in the
            // same tick, so the order they run in does not matter.
            if (ctx.StagingMap is { } stagingMap && Exists(stagingMap))
                QueueDel(stagingMap);

            // The grid is queued for deletion and nothing below can bring it back, so the row may
            // now say stored. If this write fails the revision is filed and the ship is gone from
            // the world with its row still checked out, which is exactly the "wait for a human"
            // state an admin restore exists for, and not a duplicate.
            try
            {
                if (!await slice.Await(_store.MarkStored(shipId)))
                    Log.Warning($"Drydock: {shipId} filed revision {filed.Revision} but its row did not move to stored; it may be held.");
            }
            catch (OperationCanceledException)
            {
                // Caught apart from the general failure below, and deliberately not rethrown. This is
                // the one await that happens after the commit, so a cancellation landing on it is not
                // a cancelled store: the revision is filed and the ship is already gone. Letting it
                // travel would report a store that really happened as cancelled, and logging it at
                // Error would fail every pooled integration pair that cancels a pipeline.
                Log.Warning($"Drydock: {shipId} filed revision {filed.Revision} and despawned, but the pipeline "
                            + "was cancelled before its row moved to stored. An admin restore recovers it.");
            }
            catch (Exception e)
            {
                Log.Error($"Drydock: {shipId} filed revision {filed.Revision} but marking it stored failed: {e.Message}. An admin restore recovers it.");
            }

            MarkPhase(DrydockPhase.Despawn);

            // The per-phase figures are wall clock and always were, but under slicing that stops
            // being a footnote: a phase spanning fifty ticks reads as fifty ticks. The worst slice
            // beside them is the number that says whether anyone else felt it.
            var (worstSliceMs, slices) = slice is DrydockStoreJob job
                ? (job.WorstSliceMs, job.Slices)
                : (0d, 0);

            Log.Info(timer.Format("store", shipId, liveEntities, worstSliceMs, slices));

            return new DrydockStoreOutcome(DrydockStoreResult.Success, shipId);
        }
        catch (DrydockAbortedException e)
        {
            // The world moved under a parked pipeline. Turned into an ordinary outcome here and
            // never allowed to escape: the job's process wrapper logs any exception that reaches it
            // at Error, and a pooled integration pair fails its return on an unexpected error log.
            Log.Warning($"Drydock: store of {ctx.ShipId} aborted: {e.Reason}.");

            return new DrydockStoreOutcome(
                e.Reason == AbortGridGone ? DrydockStoreResult.SerializeFailed : DrydockStoreResult.Cancelled,
                null);
        }
        finally
        {
            if (!ctx.Committed)
                UnwindStore(ctx);

            // Last, so the bar only reads finished once the ship is really back. Fired on every exit,
            // refusals included, because a client indicator that is never told the story ended sits
            // at whatever percent the refusal happened at.
            slice.Progress.Finish();
        }
    }

    /// <summary>Reason text for the two aborts, so the outcome mapping is not a string guess.</summary>
    private const string AbortGridGone = "the grid was deleted while the pipeline was parked";

    /// <inheritdoc cref="AbortGridGone"/>
    private const string AbortMapGone = "the staging map was deleted while the pipeline was parked";

    /// <summary>
    /// The staleness check after every suspension. Slicing replaced three await-driven re-checks with
    /// a hole at every yield point: an administrator can delete the grid or its private map between
    /// any two slices, and a walk that carried on past that would blank a ship that no longer exists
    /// or file a document of half a hull.
    /// </summary>
    /// <exception cref="DrydockAbortedException">
    /// Always the way this fails. The pipeline catches it and turns it into an outcome.
    /// </exception>
    private void GuardStoreResume(DrydockStoreContext ctx)
    {
        if (TerminatingOrDeleted(ctx.GridUid))
            throw new DrydockAbortedException(AbortGridGone);

        if (ctx.StagingMap is { } map && TerminatingOrDeleted(map))
            throw new DrydockAbortedException(AbortMapGone);
    }

    /// <summary>Step and guard, the pair every sliced store loop calls in place of a bare step.</summary>
    private async Task StoreStep(DrydockStoreContext ctx, IDrydockSlice slice, int index)
    {
        await slice.Step(index);
        GuardStoreResume(ctx);
    }

    /// <summary>
    /// Which registered job is driving this slice, or zero for the synchronous path. The id is
    /// stamped on every private map the pipeline creates, so a map left behind names an owner the
    /// sweep can ask whether it is still alive.
    ///
    /// <para>Zero makes a synchronous store's staging maps look ownerless to that sweep. That is the
    /// honest answer - there is no job to ask - and it costs nothing in practice, because the sweep
    /// only runs at a round boundary and a store still in flight across a round boundary was already
    /// the pre-slicing code's problem.</para>
    /// </summary>
    private int JobIdOf(IDrydockSlice slice)
    {
        if (slice is not IJob job)
            return 0;

        foreach (var (id, entry) in _liveJobs)
        {
            if (ReferenceEquals(entry.Job, job))
                return id;
        }

        return 0;
    }

    /// <summary>
    /// Puts back everything a refused store took off the ship, then puts the ship itself back at the
    /// station. Runs from the pipeline's finally on every path that did not commit, cancellation
    /// included.
    ///
    /// <para>Fully synchronous and cancellation-blind, and it has to be both. An async unwind inside a
    /// job would need to suspend, which is the one thing a cancelled job can no longer do; an unwind
    /// that honoured cancellation could never finish. It is also why the whole thing is written to
    /// tolerate anything already being gone rather than to assume a coherent world.</para>
    ///
    /// <para>The restores run in PREPARATION order rather than in reverse, which works because each
    /// one is independent of the others. The return leg is appended at the end deliberately: by the
    /// time the ship reappears at the station it is already whole.</para>
    /// </summary>
    private void UnwindStore(DrydockStoreContext ctx)
    {
        var gridUid = ctx.GridUid;

        if (!TerminatingOrDeleted(gridUid))
        {
            foreach (var uid in ctx.InjectedGas)
            {
                if (!TerminatingOrDeleted(uid))
                    RemComp<DrydockPipeGasComponent>(uid);
            }

            foreach (var uid in ctx.InjectedDamage)
            {
                if (!TerminatingOrDeleted(uid))
                    RemComp<DrydockDamageSidecarComponent>(uid);
            }

            foreach (var uid in ctx.InjectedAppearance)
            {
                if (!TerminatingOrDeleted(uid))
                    RemComp<DrydockAppearanceComponent>(uid);
            }

            RestoreStrippedComponents(gridUid, ctx.Stripped);

            if (ctx.DeedDetached)
                _shipyard.ReattachGridDeedHolder(gridUid, ctx.DeedHolder);

            ReattachStoreMaps(ctx.StoreMaps);

            // Stripping station membership fired the station system's shutdown handler, which
            // removed this grid from its station's set. Restoring the component brings the
            // reference back but not the set entry, and that set is access-locked to the station
            // system, so the re-add has to go through it. Only after a strip actually happened:
            // the gates above the strip refuse through this same path, and re-booking a grid
            // that never left its station is not a no-op for the station's listeners.
            if (ctx.Stripped.Count > 0
                && TryComp<StationMemberComponent>(gridUid, out var restoredMember)
                && HasComp<StationDataComponent>(restoredMember.Station))
            {
                _station.AddGridToStation(restoredMember.Station, gridUid);
            }

            if (ctx.Fidelity != null)
                _fidelity.RestoreSnapshot(ctx.Fidelity);
        }

        if (ctx.StagingMap is not { } staging)
        {
            // Never frozen, so the ship is still where it was. It may still have been undocked, in
            // the one statement between the undock and the map: re-dock it rather than leaving a
            // ship the player thought was moored drifting alongside the station.
            if (ctx.Undocked && !TerminatingOrDeleted(gridUid))
                ReturnGridToStation(gridUid, ctx.ReturnTarget, ctx.StationUid, ctx.ReturnDockTag);

            return;
        }

        if (TerminatingOrDeleted(gridUid))
        {
            ScrapStagingMap(staging);
            return;
        }

        // The return leg. The dock places the ship on the station's own unpaused map, and that map
        // change thaws the whole subtree inside the engine's own recursive walk, so a successful
        // return needs no per-entity thaw of ours.
        if (ReturnGridToStation(gridUid, ctx.ReturnTarget, ctx.StationUid, ctx.ReturnDockTag))
        {
            ScrapStagingMap(staging);
            return;
        }

        // Nowhere to put it: the remembered docking partner and the station are both gone. The ship
        // stays frozen on its private map, re-kinded so the sweep leaves it alone and an admin can
        // still hand it back. Deleting it is the one thing this whole path exists to prevent, and a
        // budgeted thaw is not available to a synchronous unwind.
        if (TryComp<DrydockStagingMapComponent>(staging, out var stagingMap))
            stagingMap.Kind = DrydockStagingKind.Stranded;

        Log.Error($"Drydock: the unwind for ship {ctx.ShipId} found nowhere to return {ToPrettyString(gridUid)} to. "
                  + $"It is frozen on staging map {ToPrettyString(staging)}, restored and intact, awaiting an admin.");
    }

    /// <summary>
    /// Resolves before minting. The grid-side identity component survives both a store and retrieve
    /// cycle and a round boundary, so a second store lands on the same hull.
    /// </summary>
    private Guid ResolveOrMintShipId(EntityUid gridUid)
    {
        if (TryComp<DrydockIdentityComponent>(gridUid, out var identity) && identity.ShipId != Guid.Empty)
            return identity.ShipId;

        return Guid.NewGuid();
    }

    /// <summary>
    /// What the console tells the player when the drydock has nowhere to put the hull. Only the
    /// too-small case gets its own message, because its fix is different; every other berth
    /// outcome a store can produce means "no free berth", including losing a race for the last one.
    /// </summary>
    private static DrydockStoreResult BerthRefusal(DrydockBerthResult outcome)
    {
        return outcome switch
        {
            DrydockBerthResult.BerthTooSmall => DrydockStoreResult.BerthTooSmall,
            DrydockBerthResult.BerthOccupied => DrydockStoreResult.BerthOccupied,
            _ => DrydockStoreResult.NoBerth,
        };
    }

    /// <summary>
    /// Deserializes the document that was just written onto an inert scratch map and compares it
    /// with the live grid, in two tiers: a whole-grid entity count, then a per-prototype tally of
    /// each side's direct grid children, so a bug that swaps one kind of entity for another while
    /// preserving the count is caught too. The scratch map never initializes or ticks, so it cannot
    /// touch the live simulation, and it is deleted on every path out of this call.
    ///
    /// <para>That last clause used to be a promise about the whole store and is now only a promise
    /// about this method: the store around it spans ticks. Nothing inside here yields, so the map
    /// still lives and dies within one call and the tag it is given is not load-bearing today. It is
    /// tagged anyway, because it is now a private map created from inside a pipeline that can be
    /// cancelled, and an untagged private map is invisible to the sweep the moment anything ever does
    /// leave one behind. Deliberately loaded pre-init: map-initialising the scratch copy would fire
    /// map init across it and make the composition tally disagree on every store.</para>
    ///
    /// <para>A byte-for-byte double-serialize comparison would be the deeper check and was tried
    /// first in the implementation this comes from. It is not usable as a production gate on real
    /// ship content: a reload rebuilds fixtures and the broadphase, which legitimately resets
    /// physics state that is itself a data field, so the comparison differs with no content drift
    /// at all. That is one instance of an open-ended class rather than a single normalizable noise
    /// source.</para>
    ///
    /// <para>Both tiers count only what the serializer will actually write. The engine's
    /// <c>EntitySerializer.IsSerializable</c> skips any entity whose prototype declares
    /// <c>save: false</c>, a class of ninety-odd prototypes that includes every live sound effect:
    /// a sound played at grid coordinates is a real grid child until its despawn timer fires.
    /// Before this filter, a ship that happened to have a sound in the air at the moment of the
    /// store counted it on the live side, never saw it on the scratch side, and was refused - which
    /// vessel that hit depended on the instant the store ran. The roster sweep caught it
    /// refusing different vessels on identical back-to-back runs.</para>
    /// </summary>
    /// <returns>True on mismatch, meaning the store must abort.</returns>
    /// <param name="liveEntities">
    /// How many entities the live grid holds, counted here because this already walks the tree for
    /// the comparison. Zero when the document would not reload at all.
    /// </param>
    private bool DetectRoundTripMismatch(EntityUid gridUid, string yaml, int jobId, Guid shipId, out int liveEntities)
    {
        liveEntities = 0;
        using var reader = new StringReader(yaml);
        var options = new DeserializationOptions
        {
            InitializeMaps = false,
            PauseMaps = true,
        };

        if (!_mapLoader.TryLoadGrid(reader, "drydock/validation", out var scratchMap, out var scratchGrid, options))
        {
            Log.Warning($"Drydock store validation failed for {ToPrettyString(gridUid)}: the document just written would not reload.");
            return true;
        }

        TagStagingMap(scratchMap!.Value.Owner, jobId, DrydockStagingKind.Validation, shipId);

        try
        {
            var live = CountChildPrototypes(gridUid);
            var scratch = CountChildPrototypes(scratchGrid!.Value.Owner);

            var liveCount = live.Values.Sum();
            var scratchCount = scratch.Values.Sum();
            liveEntities = liveCount;
            if (liveCount != scratchCount)
            {
                Log.Warning($"Drydock store validation failed for {ToPrettyString(gridUid)}: entity count mismatch (live={liveCount}, scratch={scratchCount}).");
                return true;
            }

            if (!PrototypeCountsMatch(live, scratch, out var detail))
            {
                Log.Warning($"Drydock store validation failed for {ToPrettyString(gridUid)}: composition mismatch ({detail}).");
                return true;
            }

            return false;
        }
        finally
        {
            Del(scratchMap!.Value.Owner);
        }
    }

    private Dictionary<string, int> CountChildPrototypes(EntityUid gridUid)
    {
        var counts = new Dictionary<string, int>();
        var enumerator = Transform(gridUid).ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            var meta = MetaData(child);

            // The serializer's own gate, mirrored: EntitySerializer.IsSerializable refuses any
            // entity whose prototype declares save: false, so such a child is live but will never
            // be in the document. Counting it refuses the store for content the store was never
            // going to write. A prototype-less entity is serializable and stays counted.
            if (meta.EntityPrototype?.MapSavable == false)
                continue;

            var protoId = meta.EntityPrototype?.ID ?? "<no-prototype>";
            counts.TryGetValue(protoId, out var count);
            counts[protoId] = count + 1;
        }

        return counts;
    }

    private static bool PrototypeCountsMatch(Dictionary<string, int> live, Dictionary<string, int> scratch, out string detail)
    {
        foreach (var (proto, liveCount) in live)
        {
            if (!scratch.TryGetValue(proto, out var scratchCount) || scratchCount != liveCount)
            {
                detail = $"prototype '{proto}': live={liveCount}, scratch={(scratch.TryGetValue(proto, out var sc) ? sc : 0)}";
                return false;
            }
        }

        foreach (var (proto, scratchCount) in scratch)
        {
            if (!live.ContainsKey(proto))
            {
                detail = $"prototype '{proto}': live=0, scratch={scratchCount}";
                return false;
            }
        }

        detail = string.Empty;
        return true;
    }

    /// <summary>
    /// The forensic record of what was aboard, built from what the store already has in hand and
    /// filled in place into <paramref name="manifest"/>. Parents are recorded as indices into the
    /// entry list rather than as entity references, since entity ids do not survive a round trip and
    /// a manifest has to still mean something a year later.
    /// </summary>
    /// <remarks>
    /// The walk itself is materialised up front, in one un-yielded pass, and only the per-entity
    /// component reads are sliced. That is what keeps the parent indices honest: they are positions
    /// in a list that was fixed before the first suspension, so an entity that vanished mid-walk
    /// still occupies its slot and every child below it still points at the right parent. The order
    /// is the depth-first one the unsliced version produced, kept so two manifests of the same hull
    /// remain comparable across the change.
    /// </remarks>
    private async Task BuildManifestSliced(DrydockStoreContext ctx, IDrydockSlice slice, DrydockManifest manifest)
    {
        var capturedByEntity = ctx.Fidelity == null
            ? new Dictionary<EntityUid, List<string>>()
            : ctx.Fidelity.Snapshot
                .GroupBy(s => s.Uid)
                .ToDictionary(g => g.Key, g => g.Select(s => $"{s.Comp.GetType().Name}|{s.Member.Name}").ToList());

        var order = new List<(EntityUid Uid, int? Parent)>();
        var stack = new Stack<(EntityUid Uid, int? Parent)>();
        stack.Push((ctx.GridUid, null));

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            var myIndex = order.Count - 1;

            var children = Transform(node.Uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push((child, myIndex));
        }

        await slice.Begin(DrydockPhase.Manifest, order.Count);

        for (var i = 0; i < order.Count; i++)
        {
            var (uid, parent) = order[i];
            var entry = new DrydockManifestEntry { Parent = parent };

            // An entry is added for every position in the walk whether or not the entity survived to
            // be read, because the indices above are what the parent links mean.
            if (!TerminatingOrDeleted(uid))
            {
                entry.Proto = MetaData(uid).EntityPrototype?.ID ?? string.Empty;

                if (TryComp<DamageableComponent>(uid, out var damageable))
                    entry.Damage = (float) damageable.TotalDamage;

                if (TryComp<Content.Shared.Stacks.StackComponent>(uid, out var stackComp))
                    entry.Stack = stackComp.Count;

                if (capturedByEntity.TryGetValue(uid, out var keys))
                    entry.CapturedKeys = keys;
            }

            manifest.Entries.Add(entry);
            await StoreStep(ctx, slice, i);
        }
    }

    /// <summary>
    /// Files each pipe net's gas onto its member pipes as a per-node share, so a document that cannot
    /// carry the node-group graph still carries the air.
    /// </summary>
    /// <remarks>
    /// The read half is one un-yielded pass and cannot be anything else. The dictionary is keyed by
    /// live node-group object identity and the second pass reads a member's air off that same live
    /// group; node group updates are not gated by pause, so a group rebuilt between two slices would
    /// leave the second pass reading a dead one. Snapshotting every share first and slicing only the
    /// write-out keeps the whole graph read inside one tick, which is where it has to be.
    /// </remarks>
    private async Task InjectPipeGasSidecarsSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        // Nets are de-duplicated by node-group identity: a net has many member pipes and its gas
        // must be distributed exactly once. Members are (entity, node name, node), because a
        // two-port device sits in two nets and each node's share has to be filed under its own
        // name, or the last net written wins and the restore leaks it into the other.
        var nets = new Dictionary<object, List<(EntityUid Owner, string Name, PipeNode Pipe)>>();

        var query = AllEntityQuery<NodeContainerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var nodeContainer, out var xform))
        {
            if (xform.GridUid != ctx.GridUid)
                continue;

            foreach (var (name, node) in nodeContainer.Nodes)
            {
                if (node is not PipeNode { NodeGroup: { } group } pipe)
                    continue;

                if (!nets.TryGetValue(group, out var members))
                    nets[group] = members = new List<(EntityUid, string, PipeNode)>();

                members.Add((uid, name, pipe));
            }
        }

        var shares = new List<(EntityUid Owner, string Name, Content.Shared.Atmos.GasMixture Share)>();

        foreach (var members in nets.Values)
        {
            var totalVolume = 0f;
            foreach (var (_, _, pipe) in members)
                totalVolume += pipe.Volume;

            if (totalVolume <= 0f)
                continue;

            var netAir = members[0].Pipe.Air;

            foreach (var (owner, name, pipe) in members)
            {
                var share = new Content.Shared.Atmos.GasMixture(netAir) { Volume = pipe.Volume };
                share.Multiply(pipe.Volume / totalVolume);
                shares.Add((owner, name, share));
            }
        }

        await slice.Begin(DrydockPhase.Sidecars, shares.Count);

        for (var i = 0; i < shares.Count; i++)
        {
            var (owner, name, share) = shares[i];

            if (!TerminatingOrDeleted(owner))
            {
                // One ledger entry per owner, and it goes in before the component does. Asking
                // whether the sidecar was already there beats counting the shares afterwards: an
                // owner with two nodes gets two shares and must still be removed exactly once, and an
                // abort between the two writes must still find it on the ledger.
                if (!HasComp<DrydockPipeGasComponent>(owner))
                    ctx.InjectedGas.Add(owner);

                EnsureComp<DrydockPipeGasComponent>(owner).Shares[name] = share;
            }

            await StoreStep(ctx, slice, i);
        }
    }

    private async Task InjectDamageSidecarsSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        var damaged = new List<(EntityUid Uid, Dictionary<string, FixedPoint2> Damage)>();

        var query = AllEntityQuery<DamageableComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var damageable, out var xform))
        {
            if (xform.GridUid != ctx.GridUid || damageable.TotalDamage <= FixedPoint2.Zero)
                continue;

            damaged.Add((uid, new Dictionary<string, FixedPoint2>(damageable.Damage.DamageDict)));
        }

        await slice.Begin(DrydockPhase.Sidecars, damaged.Count);

        for (var i = 0; i < damaged.Count; i++)
        {
            var (uid, damage) = damaged[i];

            if (!TerminatingOrDeleted(uid))
            {
                if (!HasComp<DrydockDamageSidecarComponent>(uid))
                    ctx.InjectedDamage.Add(uid);

                EnsureComp<DrydockDamageSidecarComponent>(uid).DamageDict = damage;
            }

            await StoreStep(ctx, slice, i);
        }
    }

    /// <summary>
    /// Removes each listed component, keeping a deep copy rather than the live instance so an
    /// aborted store can put the field data back. A bare re-add of a fresh instance would come back
    /// empty.
    /// </summary>
    private async Task StripListedComponentsSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        await slice.Begin(DrydockPhase.Strip, StoreStripList.Length);

        for (var i = 0; i < StoreStripList.Length; i++)
        {
            var type = StoreStripList[i];

            if (!TerminatingOrDeleted(ctx.GridUid) && TryComp(ctx.GridUid, type, out var comp))
            {
                // The copy lands on the ledger before the live component is taken off, so an abort
                // between the two finds the component still on the grid and re-adds a duplicate of
                // it, which is a no-op, rather than finding it gone with no copy to put back.
                ctx.Stripped.Add(_serialization.CreateCopy(comp, notNullableOverride: true));
                RemComp(ctx.GridUid, comp);
            }

            await StoreStep(ctx, slice, i);
        }
    }

    private void RestoreStrippedComponents(EntityUid gridUid, List<IComponent> stripped)
    {
        foreach (var comp in stripped)
        {
#pragma warning disable CS0618 // Owner is obsolete for external callers; this is the component-restore seam.
            comp.Owner = gridUid;
#pragma warning restore CS0618
            AddComp(gridUid, comp, true);
        }
    }

    /// <summary>
    /// Deletes every entity aboard marked as saving contraband without a permit, containers and
    /// contents included, and returns how many went. The ship-save path's rule
    /// (<c>IsInvalidEntity</c>), applied by component rather than by a list: the component is what
    /// the content marks. Immediate deletes, not queued: the serializer walks the tree many ticks
    /// later now, and a queued deletion would be honoured well before then, but a merely queued
    /// entity is still a real grid child in the meantime and every walk between here and the save -
    /// the sidecars, the capture, the manifest - would count and touch it.
    ///
    /// <para>Deliberately absolute, with no exemption for anchored entities. Anchoring is a state a
    /// player can create with a wrench, so exempting it would let anyone bolt restricted kit to a
    /// deck and carry it between rounds. A hull fixture that must survive a store is one that should
    /// not have carried the contraband marker in the first place, which is where that gets fixed.</para>
    /// </summary>
    private async Task PurgeSavingContrabandSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        var doomed = new List<EntityUid>();
        var query = AllEntityQuery<SavingContrabandComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid != ctx.GridUid || HasComp<ContrabandPermitItemComponent>(uid))
                continue;

            doomed.Add(uid);
        }

        await slice.Begin(DrydockPhase.Purge, doomed.Count);

        var count = 0;
        for (var i = 0; i < doomed.Count; i++)
        {
            // A container purged earlier in the list takes its contents with it.
            if (!TerminatingOrDeleted(doomed[i]))
            {
                Del(doomed[i]);
                count++;
            }

            await StoreStep(ctx, slice, i);
        }

        if (count > 0)
            Log.Info($"Drydock: {ctx.ShipId} store purged {count} saving-contraband entities without a permit.");
    }

    /// <summary>
    /// Blanks the starting-map reference on every store aboard (a PDA's uplink store, mostly) and
    /// returns what was there, so an aborted store can put it back.
    /// </summary>
    private async Task DetachStoreMapsSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        var found = new List<(EntityUid Store, EntityUid? Map)>();
        var query = AllEntityQuery<StoreComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var store, out var xform))
        {
            if (xform.GridUid != ctx.GridUid || store.StartingMap == null)
                continue;

            found.Add((uid, store.StartingMap));
        }

        await slice.Begin(DrydockPhase.Strip, found.Count);

        for (var i = 0; i < found.Count; i++)
        {
            var (uid, map) = found[i];

            // Ledger first, blank second, so an abort between them re-writes a value that is still
            // there rather than losing one that is already gone.
            if (TryComp<StoreComponent>(uid, out var store) && store.StartingMap != null)
            {
                ctx.StoreMaps.Add((uid, map));
                store.StartingMap = null;
            }

            await StoreStep(ctx, slice, i);
        }
    }

    private void ReattachStoreMaps(List<(EntityUid Store, EntityUid? Map)> detached)
    {
        foreach (var (uid, map) in detached)
        {
            if (TryComp<StoreComponent>(uid, out var store))
                store.StartingMap = map;
        }
    }

    /// <summary>
    /// Whether an armed nuke, an active countdown, or a singularity is aboard. Each is a world query
    /// filtered by grid rather than a child walk, because hazards are rare and the transform's grid
    /// resolves through container nesting: a nuke stashed in a crate still reports the ship.
    /// </summary>
    private bool HasHazardAboard(EntityUid gridUid)
    {
        var nukes = AllEntityQuery<NukeComponent, TransformComponent>();
        while (nukes.MoveNext(out _, out var nuke, out var xform))
        {
            if (xform.GridUid == gridUid && nuke.Status == NukeStatus.ARMED)
                return true;
        }

        var timers = AllEntityQuery<ActiveTimerTriggerComponent, TransformComponent>();
        while (timers.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                return true;
        }

        var singularities = AllEntityQuery<SingularityComponent, TransformComponent>();
        while (singularities.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The engine's document format version, and a hash over the sorted set of prototype ids the
    /// document references. That id set is the drift key: a change to it is what the re-bake ladder
    /// reacts to.
    /// </summary>
    /// <summary>
    /// The document's format version and the fingerprint of the prototypes it names.
    /// </summary>
    /// <remarks>
    /// <para>Streams the document rather than loading it into a node tree. The two facts wanted here
    /// live in the top-level <c>meta</c> mapping and in the <c>proto</c> key of each entry of the
    /// top-level <c>entities</c> sequence, and entities are grouped by prototype, so those keys number
    /// in the hundreds while the tree under them holds every instance, component and field on the
    /// ship. Building that tree to read the headers cost 237 ms of a 2.1 s store on a large hull,
    /// measured across 112 stores 2026-09-09, which was more than a tenth of the whole pipeline.</para>
    /// <para>The output is contractually identical to the node-tree read it replaces, because the
    /// fingerprint is persisted on every revision and compared across stores: a different value here
    /// would read as content drift on ships that had not changed.
    /// <c>DrydockDriftMetadataTest</c> holds that equality down against a tree-reading oracle.</para>
    /// </remarks>
    internal static (byte[] Fingerprint, int FormatVersion) ReadDriftMetadata(string yaml)
    {
        var formatVer = 0;
        var protos = new SortedSet<string>(StringComparer.Ordinal);

        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();

        if (!parser.TryConsume<MappingStart>(out _))
            return (Fingerprint(protos), formatVer);

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;

            switch (key)
            {
                case "meta":
                    formatVer = ReadFormat(parser);
                    break;
                case "entities":
                    ReadProtoGroups(parser, protos);
                    break;
                default:
                    parser.SkipThisAndNestedEvents();
                    break;
            }
        }

        return (Fingerprint(protos), formatVer);

        static byte[] Fingerprint(SortedSet<string> ids) =>
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ids)));
    }

    /// <summary>Reads <c>format</c> out of the meta mapping, skipping everything else in it.</summary>
    private static int ReadFormat(IParser parser)
    {
        if (!parser.TryConsume<MappingStart>(out _))
        {
            // Not a mapping, so there is no format to find. The value still has to be consumed or
            // the caller reads this node's contents as its own keys.
            parser.SkipThisAndNestedEvents();
            return 0;
        }

        var formatVer = 0;

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            if (parser.Consume<Scalar>().Value == "format" && parser.TryConsume<Scalar>(out var value))
                int.TryParse(value.Value, out formatVer);
            else
                parser.SkipThisAndNestedEvents();
        }

        return formatVer;
    }

    /// <summary>
    /// Collects the <c>proto</c> of each prototype group, skipping the instance list under it, which
    /// is where the document's bulk lives.
    /// </summary>
    private static void ReadProtoGroups(IParser parser, SortedSet<string> protos)
    {
        if (!parser.TryConsume<SequenceStart>(out _))
        {
            parser.SkipThisAndNestedEvents();
            return;
        }

        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            // A non-mapping entry is skipped rather than refused, matching the node-tree read, which
            // filtered the sequence to mappings.
            if (!parser.TryConsume<MappingStart>(out _))
            {
                parser.SkipThisAndNestedEvents();
                continue;
            }

            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (parser.Consume<Scalar>().Value == "proto"
                    && parser.TryConsume<Scalar>(out var proto))
                {
                    if (proto.Value.Length > 0)
                        protos.Add(proto.Value);
                }
                else
                {
                    parser.SkipThisAndNestedEvents();
                }
            }
        }
    }

    private static byte[] CompressZstd(byte[] input)
    {
        using var output = new MemoryStream();
        using (var compress = new ZStdCompressStream(output, ownStream: false))
        {
            compress.Write(input);
        }

        return output.ToArray();
    }
}
