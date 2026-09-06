using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Stores a deeded grid as an engine-serialized document in the database and takes it off the map.
/// The pipeline is entirely in memory: serialize, validate, checksum, compress, file, despawn. No
/// file ever touches disk, which is the whole point of replacing the ship save system.
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
    };

    /// <summary>
    /// Stores <paramref name="gridUid"/> for <paramref name="ownerUserId"/>. The order is gate,
    /// prepare, serialize, validate, commit, despawn, and the grid is only removed once the
    /// document is filed.
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
    public async Task<(DrydockStoreResult Result, Guid? ShipId)> TryStoreShip(EntityUid gridUid, Guid ownerUserId, int? roundId, int? berthId = null)
    {
        if (!_cfg.GetCVar(TriadCCVars.DrydockEnabled) || _cfg.GetCVar(TriadCCVars.DrydockReadOnly))
            return (DrydockStoreResult.Disabled, null);

        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        // The sentinel, before anything yields. A second store request for the same grid while
        // this one is awaiting the database would otherwise run the whole preparation again on a
        // grid mid-store and file a second revision of it. The marker doubles as the container
        // insertion block for the length of the store, and the finally below always removes it.
        if (HasComp<DrydockInProgressComponent>(gridUid))
            return (DrydockStoreResult.InProgress, null);

        EnsureComp<DrydockInProgressComponent>(gridUid);

        var injectedGas = new List<EntityUid>();
        var injectedDamage = new List<EntityUid>();
        var stripped = new List<IComponent>();
        DrydockFidelityCapture? fidelity = null;
        EntityUid? deedHolder = null;
        var storeMaps = new List<(EntityUid Store, EntityUid? Map)>();
        var committed = false;

        // The try opens HERE, before the first await and before the first mutation that has to be
        // undone, rather than after the whole preparation as the reference implementation had it.
        // Everything below either yields or clears live fields or removes live components, and a
        // throw anywhere in it would otherwise escape with the ship left blanked or the sentinel
        // left on: sidecars stuck on, stripped components gone, captured fields emptied, every
        // container aboard refusing insertion forever.
        try
        {
            // Capacity first, because it is the one gate that needs the database. The await sits
            // before any other gate has been passed and before any mutation, so nothing has to be
            // re-checked after it. A full garage refuses cheaply here; the filing transaction
            // checks again, and the unique index on the berth column makes that answer the final
            // one. Both reads are of the live grid: the cached class text on the row is never
            // load-bearing.
            var shipId = ResolveOrMintShipId(gridUid);
            var sizeClass = _shipSize.GetSizeClass((gridUid, Comp<MapGridComponent>(gridUid))).ToString();

            var capacity = await _store.CheckBerthForStore(shipId, ownerUserId, sizeClass, berthId);
            if (capacity != DrydockBerthResult.Success)
                return (BerthRefusal(capacity), null);

            if (TerminatingOrDeleted(gridUid))
                return (DrydockStoreResult.SerializeFailed, null);

            // Hazards next, because the check mutates nothing and refusing here means nobody has
            // been moved for a store that was never going to happen. Runtime countdowns are
            // ordinary data fields that would resume on thaw, so an armed ship must be refused
            // rather than frozen.
            if (HasHazardAboard(gridUid))
                return (DrydockStoreResult.HazardAboard, null);

            // A mind must never be serialized, and a living mob does not round-trip cleanly. The
            // implementation this is ported from relocates loose occupants onto the docked station
            // instead of refusing; that is not ported yet, so this gate is stricter than it will
            // finally be. Refusing is the safe direction to be stricter in.
            if (_shipyard.FoundOrganics(gridUid, mobQuery, xformQuery) is not null)
                return (DrydockStoreResult.OrganicsAboard, null);

            EnsureComp<DrydockIdentityComponent>(gridUid).ShipId = shipId;

            var shipName = Comp<MetaDataComponent>(gridUid).EntityName;

            // The saving-contraband purge, by the same component rule the ship-save path applies:
            // anything marked as saving contraband goes unless it carries a contraband permit. It
            // sits after every refusal above, so a refused store deletes nothing, and before the
            // appraisal, so the quote is for what is actually filed. Not undoable, which is also
            // true of the reference path; the first play test found ID cards and modular grenades
            // riding a store that kept everything (2026-09-06).
            var purged = PurgeSavingContraband(gridUid);
            if (purged > 0)
                Log.Info($"Drydock: {shipId} store purged {purged} saving-contraband entities without a permit.");

            // The sale quote, taken while the hull is whole and before any sidecar or strip
            // touches it, so what a scrap pays is what the shipyard would have paid at this moment.
            var appraisal = _shipyard.AppraiseHull(gridUid);

            // A pipe net's air lives on the node-group graph, which the serializer cannot reach.
            // Distribute each net's gas across its members by volume. The live net is left alone,
            // since the ship stays flyable until it despawns.
            injectedGas = InjectPipeGasSidecars(gridUid);

            // Damage is read-only to the serializer, so a damaged ship would come back pristine.
            injectedDamage = InjectDamageSidecars(gridUid);

            // The grid does not know its own vessel prototype; its station's latejoin information
            // does. Read it before the strip below cuts station membership off the grid.
            string? vesselProto = null;
            if (TryComp<StationMemberComponent>(gridUid, out var stationMember)
                && TryComp<ExtraShuttleInformationComponent>(stationMember.Station, out var vesselInfo)
                && vesselInfo.Vessel is { } vessel)
            {
                vesselProto = vessel.Id;
            }

            stripped = StripListedComponents(gridUid);

            // The grid's own deed names the card holding it, which is outside the document. Written
            // as-is it reloads as an invalid reference and the deserializer logs an error on every
            // scratch load and every retrieve; retrieve sets the holder afresh anyway.
            deedHolder = _shipyard.DetachGridDeedHolder(gridUid);

            // A PDA's store remembers the map it was set up on, which is likewise outside the
            // document and reloads as an invalid reference that logs on every load. Purchased
            // ships already leave that map behind when they dock, so a retrieved store is no
            // worse off for coming back with the field blank.
            storeMaps = DetachStoreMaps(gridUid);

            // The general net, after the two specific sidecars and the strip list so it sees the
            // final live component set. For every unserializable populated field it either captures
            // the value or strips it, and clears the live field either way.
            fidelity = _fidelity.CaptureAndStrip(gridUid);

            // The rest of preparation is deliberately not undoable, and runs last for that reason.
            // An empty AI core is the intended end state, an undocked ship is where a stored ship
            // has to start from, and a ship at rest has no business carrying FTL state.
            SanitizeStationAiCores(gridUid);

            // A stored ship must be fully detached: its docking partner is not in the document, so a
            // serialized dock reloads as an invalid reference and crashes the docking system's
            // startup on every load, the validation scratch load included.
            _docking.UndockDocks(gridUid);

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

            string yaml;
            using (var writer = new StringWriter())
            {
                if (!_mapLoader.TrySaveGrid(gridUid, writer, saveOptions))
                    return (DrydockStoreResult.SerializeFailed, null);

                yaml = writer.ToString();
            }

            if (DetectRoundTripMismatch(gridUid, yaml))
                return (DrydockStoreResult.ValidationFailed, null);

            var yamlBytes = Encoding.UTF8.GetBytes(yaml);

            // Checksum the uncompressed document, so stored hashes survive a future change of
            // compression.
            var checksum = SHA256.HashData(yamlBytes);
            var (fingerprint, engineFormat) = ReadDriftMetadata(yaml);

            var request = new DrydockRevisionRequest
            {
                ShipGuid = shipId,
                OwnerUserId = ownerUserId,
                ShipName = shipName,
                VesselProto = vesselProto,
                SizeClass = sizeClass,
                BerthId = berthId,
                Kind = DrydockRevisionKind.PlayerStore,
                ActorUserId = ownerUserId,
                CreatedRoundId = roundId,
                EngineFormatVer = engineFormat,
                ProtoFingerprint = fingerprint,
                CapturedKeyHash = fidelity.ComputeCapturedKeyHash(),
                Checksum = checksum,
                SizeBytes = yamlBytes.Length,
                AppraisedValue = appraisal,
                Manifest = BuildManifest(gridUid, fidelity).Serialize(),
            };

            var filed = await _store.FileRevision(request, CompressZstd(yamlBytes), _cfg.GetCVar(TriadCCVars.DrydockKeepBlobs));

            // The garage filled up between the capacity check and the commit, or this store lost
            // the last berth to another committing in the same instant. Nothing was filed; the
            // unwind below hands the ship back exactly as it was.
            if (filed.Outcome != DrydockBerthResult.Success)
                return (BerthRefusal(filed.Outcome), null);

            // The write above yielded, and the in-progress marker blocks insertion, not walking
            // aboard. Refuse rather than despawning somebody with the ship. The revision already
            // filed is a truthful snapshot of a real past state and cannot be retrieved into a
            // duplicate, because the row is not marked stored until the grid is gone, below. That
            // two-step is deliberate: the first draft had the filing write mark the row stored,
            // and this refusal then left a retrievable row behind a ship still flying.
            if (_shipyard.FoundOrganics(gridUid, mobQuery, xformQuery) is not null)
                return (DrydockStoreResult.OrganicsAboard, null);

            QueueDel(gridUid);
            committed = true;

            // The grid is queued for deletion and nothing below can bring it back, so the row may
            // now say stored. If this write fails the revision is filed and the ship is gone from
            // the world with its row still checked out, which is exactly the "wait for a human"
            // state an admin restore exists for, and not a duplicate.
            try
            {
                if (!await _store.MarkStored(shipId))
                    Log.Warning($"Drydock: {shipId} filed revision {filed.Revision} but its row did not move to stored; it may be held.");
            }
            catch (Exception e)
            {
                Log.Error($"Drydock: {shipId} filed revision {filed.Revision} but marking it stored failed: {e.Message}. An admin restore recovers it.");
            }

            return (DrydockStoreResult.Success, shipId);
        }
        finally
        {
            // On success the grid is already queued for deletion, so this is a no-op. On any refusal
            // it re-opens insertion on a ship that is still flying.
            RemCompDeferred<DrydockInProgressComponent>(gridUid);

            if (!committed)
            {
                foreach (var uid in injectedGas)
                    RemComp<DrydockPipeGasComponent>(uid);

                foreach (var uid in injectedDamage)
                    RemComp<DrydockDamageSidecarComponent>(uid);

                RestoreStrippedComponents(gridUid, stripped);
                _shipyard.ReattachGridDeedHolder(gridUid, deedHolder);
                ReattachStoreMaps(storeMaps);

                // Stripping station membership fired the station system's shutdown handler, which
                // removed this grid from its station's set. Restoring the component brings the
                // reference back but not the set entry, and that set is access-locked to the station
                // system, so the re-add has to go through it. Only after a strip actually happened:
                // the gates above the strip refuse through this same finally, and re-booking a grid
                // that never left its station is not a no-op for the station's listeners.
                if (stripped.Count > 0
                    && TryComp<StationMemberComponent>(gridUid, out var restoredMember)
                    && HasComp<StationDataComponent>(restoredMember.Station))
                {
                    _station.AddGridToStation(restoredMember.Station, gridUid);
                }

                if (fidelity != null)
                    _fidelity.RestoreSnapshot(fidelity);
            }
        }
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
    /// touch the live simulation, and it is deleted on every path out.
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
    /// vessel that hit depended on the instant the store ran. The 2026-08-26 roster sweep caught it
    /// refusing different vessels on identical back-to-back runs.</para>
    /// </summary>
    /// <returns>True on mismatch, meaning the store must abort.</returns>
    private bool DetectRoundTripMismatch(EntityUid gridUid, string yaml)
    {
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

        try
        {
            var live = CountChildPrototypes(gridUid);
            var scratch = CountChildPrototypes(scratchGrid!.Value.Owner);

            var liveCount = live.Values.Sum();
            var scratchCount = scratch.Values.Sum();
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
    /// The forensic record of what was aboard, built from what the store already has in hand.
    /// Parents are recorded as indices into the entry list rather than as entity references, since
    /// entity ids do not survive a round trip and a manifest has to still mean something a year
    /// later.
    /// </summary>
    private DrydockManifest BuildManifest(EntityUid gridUid, DrydockFidelityCapture capture)
    {
        var manifest = new DrydockManifest();
        var index = new Dictionary<EntityUid, int>();
        var capturedByEntity = capture.Snapshot
            .GroupBy(s => s.Uid)
            .ToDictionary(g => g.Key, g => g.Select(s => $"{s.Comp.GetType().Name}|{s.Member.Name}").ToList());

        var stack = new Stack<(EntityUid Uid, int? Parent)>();
        stack.Push((gridUid, null));

        while (stack.Count > 0)
        {
            var (uid, parent) = stack.Pop();

            var entry = new DrydockManifestEntry
            {
                Proto = MetaData(uid).EntityPrototype?.ID ?? string.Empty,
                Parent = parent,
            };

            if (TryComp<DamageableComponent>(uid, out var damageable))
                entry.Damage = (float) damageable.TotalDamage;

            if (TryComp<Content.Shared.Stacks.StackComponent>(uid, out var stack1))
                entry.Stack = stack1.Count;

            if (capturedByEntity.TryGetValue(uid, out var keys))
                entry.CapturedKeys = keys;

            manifest.Entries.Add(entry);
            var myIndex = manifest.Entries.Count - 1;
            index[uid] = myIndex;

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push((child, myIndex));
        }

        return manifest;
    }

    private List<EntityUid> InjectPipeGasSidecars(EntityUid gridUid)
    {
        var injected = new List<EntityUid>();

        // Nets are de-duplicated by node-group identity: a net has many member pipes and its gas
        // must be distributed exactly once. Members are (entity, node name, node), because a
        // two-port device sits in two nets and each node's share has to be filed under its own
        // name, or the last net written wins and the restore leaks it into the other.
        var nets = new Dictionary<object, List<(EntityUid Owner, string Name, PipeNode Pipe)>>();

        var query = AllEntityQuery<NodeContainerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var nodeContainer, out var xform))
        {
            if (xform.GridUid != gridUid)
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

                var sidecar = EnsureComp<DrydockPipeGasComponent>(owner);
                sidecar.Shares[name] = share;
                if (sidecar.Shares.Count == 1)
                    injected.Add(owner);
            }
        }

        return injected;
    }

    private List<EntityUid> InjectDamageSidecars(EntityUid gridUid)
    {
        var injected = new List<EntityUid>();

        var query = AllEntityQuery<DamageableComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var damageable, out var xform))
        {
            if (xform.GridUid != gridUid || damageable.TotalDamage <= FixedPoint2.Zero)
                continue;

            EnsureComp<DrydockDamageSidecarComponent>(uid).DamageDict =
                new Dictionary<string, FixedPoint2>(damageable.Damage.DamageDict);

            injected.Add(uid);
        }

        return injected;
    }

    /// <summary>
    /// Removes each listed component, keeping a deep copy rather than the live instance so an
    /// aborted store can put the field data back. A bare re-add of a fresh instance would come back
    /// empty.
    /// </summary>
    private List<IComponent> StripListedComponents(EntityUid gridUid)
    {
        var stripped = new List<IComponent>();

        foreach (var type in StoreStripList)
        {
            if (!TryComp(gridUid, type, out var comp))
                continue;

            stripped.Add(_serialization.CreateCopy(comp, notNullableOverride: true));
            RemComp(gridUid, comp);
        }

        return stripped;
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
    /// Deletes every entity aboard marked as saving contraband that does not carry a contraband
    /// permit, containers and their contents included, and returns how many went. This is the
    /// ship-save path's rule (<c>IsInvalidEntity</c>), applied by component rather than by a list,
    /// which is the user's call from the play test: the component is what the content marks.
    /// Immediate deletes, because the serializer walks the tree later in this same tick.
    /// </summary>
    private int PurgeSavingContraband(EntityUid gridUid)
    {
        var doomed = new List<EntityUid>();
        var query = AllEntityQuery<SavingContrabandComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid != gridUid || HasComp<ContrabandPermitItemComponent>(uid))
                continue;

            doomed.Add(uid);
        }

        var count = 0;
        foreach (var uid in doomed)
        {
            // A container purged earlier in the list takes its contents with it.
            if (TerminatingOrDeleted(uid))
                continue;

            Del(uid);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Blanks the starting-map reference on every store aboard (a PDA's uplink store, mostly) and
    /// returns what was there, so an aborted store can put it back.
    /// </summary>
    private List<(EntityUid Store, EntityUid? Map)> DetachStoreMaps(EntityUid gridUid)
    {
        var detached = new List<(EntityUid, EntityUid?)>();
        var query = AllEntityQuery<StoreComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var store, out var xform))
        {
            if (xform.GridUid != gridUid || store.StartingMap == null)
                continue;

            detached.Add((uid, store.StartingMap));
            store.StartingMap = null;
        }

        return detached;
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
    private static (byte[] Fingerprint, int FormatVersion) ReadDriftMetadata(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode) stream.Documents[0].RootNode;

        var formatVer = 0;
        if (root.Children.TryGetValue(new YamlScalarNode("meta"), out var metaNode)
            && metaNode is YamlMappingNode meta
            && meta.Children.TryGetValue(new YamlScalarNode("format"), out var format))
        {
            int.TryParse(((YamlScalarNode) format).Value, out formatVer);
        }

        var protos = new SortedSet<string>(StringComparer.Ordinal);
        if (root.Children.TryGetValue(new YamlScalarNode("entities"), out var entitiesNode)
            && entitiesNode is YamlSequenceNode entities)
        {
            foreach (var entry in entities.Children.OfType<YamlMappingNode>())
            {
                if (entry.Children.TryGetValue(new YamlScalarNode("proto"), out var proto)
                    && ((YamlScalarNode) proto).Value is { Length: > 0 } protoId)
                {
                    protos.Add(protoId);
                }
            }
        }

        return (SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', protos))), formatVer);
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
