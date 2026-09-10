using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._NF.Station.Components;
using Content.Server.Chemistry.Components;
using Content.Server.Database;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Gravity;
using Content.Server.Lathe.Components;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Maps;
using Content.Server.Power.EntitySystems;
using Content.Server.Power.Generator;
using Content.Server.Research.Systems;
using Content.Server.Station;
using Content.Server.Station.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Wires;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Shitmed.Autodoc.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared.Cabinet;
using Content.Shared.Chemistry;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Lathe;
using Content.Shared.Mind.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Power.Generator;
using Content.Shared.Research.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.SmartFridge;
using Content.Shared.Station.Components;
using Content.Shared.Xenoarchaeology.Equipment;
using Content.Shared.Xenoarchaeology.Equipment.Components;
using Robust.Server.Player;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The retrieve half: claim the ship's row, verify a revision, materialize it frozen on a private
/// staging map, revive the state that a normal spawn would have set up, and dock it at the
/// requesting station.
///
/// <para>The ship arrives paused and stays paused until the dock moves it. That is not a precaution
/// bolted onto the slicing, it is what makes the slicing possible: the revive epilogue below now
/// spans many ticks, and a ship that were live for those ticks would be sitting alone, airless and
/// unpiloted on a private map, simulating itself against half-restored state. Frozen, it holds
/// perfectly still until the moment it is handed over.</para>
///
/// <para>Every sweep in the epilogue reads <c>AllEntityQuery</c>, which unlike
/// <c>EntityQueryEnumerator</c> has no paused check, so all of it works verbatim on a frozen ship.
/// That single engine fact is what the whole design rests on; do not "fix" a sweep to the
/// paused-skipping enumerator.</para>
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedShipRepairSystem _shipRepair = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private NPCSystem _npc = default!;
    [Dependency] private WiresSystem _wires = default!;
    [Dependency] private DeviceNetworkSystem _deviceNetwork = default!;
    [Dependency] private ResearchSystem _research = default!;
    [Dependency] private ShuttleConsoleLockSystem _consoleLock = default!;
    [Dependency] private GeneratorSystem _generator = default!;
    [Dependency] private SharedSmartFridgeSystem _smartFridge = default!;
    [Dependency] private SharedArtifactAnalyzerSystem _artifactAnalyzer = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private OpenableSystem _openable = default!;
    // Triad: retired together with ReviveUseDelays further down. The generic offset path leaves
    // every stored delay re-based onto this round's clock with its remaining time intact, so
    // nothing on this path needs the use-delay system any more. Commented rather than deleted so
    // the dependency and the method it existed for read as one decision.
    // [Dependency] private UseDelaySystem _useDelay = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    /// <summary>
    /// Retrieves a stored ship and presents it at <paramref name="stationUid"/>.
    ///
    /// <para>The claim is a single conditional state transition, and it happens before anything is
    /// materialized. That is what makes two simultaneous retrieves safe: both ask the database to
    /// move the row from stored to checked out, exactly one wins, and the loser never touches a map.
    /// The reference implementation used a process-local dictionary for this, which does not survive
    /// a restart and does not span two server processes; a row does both.</para>
    ///
    /// <para>Every failure after the claim releases it, or the ship would be unretrievable until an
    /// administrator noticed. The release, and the berth vacate that follows a success, live in this
    /// wrapper rather than inside the pipeline, and that placement is the one thing here that must
    /// not be moved. Inside a job, a database await reached from a <c>finally</c> during a
    /// cancellation throws a second cancellation the moment it tries to resume, so the release would
    /// never run and a round restart would strand a checked-out row on every retrieve in flight. Out
    /// here a bare await is legal and nothing cancels it.</para>
    /// </summary>
    /// <param name="onProgress">
    /// Fired on the main thread whenever the whole percent moves or a phase opens. A retrieve is
    /// elastic by design, and an elastic wait is only acceptable to the player who pressed the
    /// button if it can say how far along it is.
    /// </param>
    public async Task<DrydockRetrieve> TryRetrieveShip(
        Guid shipId,
        Guid ownerUserId,
        EntityUid stationUid,
        int? roundId,
        DrydockProgressCallback? onProgress = null)
    {
        // Read-only allows retrieve on purpose: it exists to stop a suspect build writing more bad
        // revisions, not to ground the fleet.
        if (!_cfg.GetCVar(TriadCCVars.DrydockEnabled))
            return DrydockRetrieve.Refused(DrydockRetrieveResult.Disabled);

        // Resolved here only to refuse a station with nothing to dock to. The grid uid is
        // deliberately not carried forward: the pipeline re-resolves it at the dock, because a grid
        // split, a new grid, or the station itself dying can all change the answer over the many
        // ticks in between.
        if (!TryComp<StationDataComponent>(stationUid, out var stationData)
            || _station.GetLargestGrid(stationData) is null)
        {
            Log.Warning($"Drydock: retrieve of {shipId} refused, {ToPrettyString(stationUid)} is not a valid requesting station.");
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NoStation);
        }

        // The row alone for the gates. The document is read by the pipeline instead, after the
        // claim, so a refusal never pays for a blob and the read that does happen cannot race a
        // transfer or a second retrieve.
        var header = await _store.GetShipHeader(shipId);
        if (header == null)
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NotFound);

        if (header.OwnerUserId != ownerUserId)
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NotOwned);

        // The console hides a ship under investigation; this is what actually refuses it. An
        // investigation is an admin's decision and a forged retrieve request must not walk past it.
        if (header.Investigating)
        {
            Log.Info($"Drydock: retrieve of {shipId} refused, the ship is under investigation.");
            return DrydockRetrieve.Refused(DrydockRetrieveResult.Investigating);
        }

        // The row's state names the refusal before the claim is tried. The claim below still
        // decides: this read can be stale by the time the claim lands.
        switch (header.State)
        {
            case DrydockShipState.CheckedOut:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.AlreadyOut);
            case DrydockShipState.Held:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.Held);
            case DrydockShipState.InEscrow:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.InEscrow);
            case DrydockShipState.Sold:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.Sold);
        }

        // Claim before materializing. A ship that is checked out or held loses here.
        if (!await _store.TrySetState(shipId, DrydockShipState.Stored, DrydockShipState.CheckedOut,
                DrydockAuditAction.Retrieve, ownerUserId, roundId, null))
        {
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NotStored);
        }

        var ctx = new DrydockRetrieveContext
        {
            ShipId = shipId,
            OwnerUserId = ownerUserId,
            StationUid = stationUid,
            RoundId = roundId,
            ClaimHeld = true,
        };

        var budget = TickBudgetSeconds;
        var jobId = 0;
        CancellationTokenSource? cancellation = null;
        DrydockRetrieveJob? job = null;
        DrydockProgress? progress = null;
        var outcome = DrydockRetrieve.Refused(DrydockRetrieveResult.Cancelled);

        try
        {
            if (budget <= 0)
            {
                // The rollback lever. No job, no queue, no per-tick budget: the pipeline runs to
                // completion on this async path exactly as it did before slicing. It is also what
                // the integration fixtures select, so their tick pumps stay bounded.
                var sync = new DrydockSyncSlice(DrydockPhases.Retrieve, onProgress);
                progress = sync.Progress;
                outcome = (await RunRetrievePipeline(ctx, sync)).Retrieve;
            }
            else
            {
                cancellation = new CancellationTokenSource();
                job = new DrydockRetrieveJob(this, ctx, budget, SliceStride, onProgress, cancellation.Token);
                progress = job.Progress;
                jobId = RegisterJob(job, cancellation);
                EnqueueJob(job);

                var result = await job.AsTask;

                // The job's own wrapper swallows an exception into a property and faults the task
                // with it. This rethrow is what keeps a failed retrieve failing for its caller the
                // same way it did when the pipeline was called directly.
                if (job.Exception != null)
                    throw job.Exception;

                outcome = result?.Retrieve ?? DrydockRetrieve.Refused(DrydockRetrieveResult.Cancelled);
            }
        }
        catch (OperationCanceledException)
        {
            // A round restart, a shutdown, or the slice watchdog. The pipeline scrapped whatever it
            // had staged on its way out, so nothing is out and the claim below goes back.
            Log.Warning($"Drydock: retrieve of {shipId} was cancelled in flight; the ship is still stored.");
            outcome = DrydockRetrieve.Refused(DrydockRetrieveResult.Cancelled);
        }
        finally
        {
            if (jobId != 0)
                RetireJob(jobId);
            else
                cancellation?.Dispose();

            // The bar's last phase belongs to the wrapper, because the two writes it names are the
            // wrapper's own.
            progress?.BeginPhase(DrydockPhase.Release, 0);

            if (ctx.ClaimHeld)
            {
                // Wrapped where the older code left it bare: a throw out of a finally replaces
                // whatever exception was already travelling, and losing a real failure to a
                // database hiccup is how a bug becomes unreadable.
                try
                {
                    await _store.TrySetState(shipId, DrydockShipState.CheckedOut, DrydockShipState.Stored,
                        DrydockAuditAction.Release, null, roundId, "retrieve failed");
                }
                catch (Exception e)
                {
                    Log.Error($"Drydock: {shipId} could not release its claim after a failed retrieve: {e.Message}");
                }
            }
            else if (ctx.Presented)
            {
                // The berth empties only now, after the ship is docked and the claim is confirmed,
                // and never inside the claim: a failure after the claim releases the state without
                // ever having to re-seat a berth somebody else may have taken. If this write fails
                // the ship is out and still shown in its slot, which its next store heals and an
                // admin move can fix; a ship that is already docked is not scrapped over a
                // bookkeeping column.
                try
                {
                    await _store.VacateBerth(shipId);
                }
                catch (Exception e)
                {
                    Log.Error($"Drydock: {shipId} is out but its berth could not be vacated: {e.Message}");
                }
            }
        }

        ctx.Timer.Mark("release");

        if (outcome.Succeeded)
        {
            progress?.Finish();

            // The per-phase figures are wall clock now that every phase can span ticks, so the
            // worst single slice is the only number in this line that still means "stall".
            Log.Info(ctx.Timer.Format("retrieve", shipId, ctx.EntityCount, job?.WorstSliceMs ?? 0, job?.Slices ?? 0));
        }

        return outcome;
    }

    /// <summary>
    /// The retrieve itself, from the first blob read to the dock. Driven either by a job, a few
    /// milliseconds of main-thread time per tick, or by <see cref="DrydockSyncSlice"/> straight
    /// through.
    ///
    /// <para>The database claim is neither taken nor released here. The wrapper owns it, because a
    /// job that observes its cancellation can never finish another await, and the release is the one
    /// step that has to happen even then.</para>
    /// </summary>
    internal async Task<DrydockRetrieveOutcome> RunRetrievePipeline(DrydockRetrieveContext ctx, IDrydockSlice slice)
    {
        var timer = ctx.Timer;
        var jobId = JobIdOf(slice);

        try
        {
            await slice.Begin(DrydockPhase.Fetch, 0);

            var current = await slice.Await(_store.LoadCurrent(ctx.ShipId));
            if (current == null)
                return new DrydockRetrieveOutcome(DrydockRetrieve.Refused(DrydockRetrieveResult.NotFound));

            var keepBlobs = _cfg.GetCVar(TriadCCVars.DrydockKeepBlobs);
            var oldest = keepBlobs > 0 ? Math.Max(1, current.Ship.CurrentRevision - keepBlobs + 1) : 1;

            for (var revision = current.Ship.CurrentRevision; revision >= oldest; revision--)
            {
                // Re-opened per revision, so a fallback reads as a retry rather than as a stall. The
                // percentage is clamped monotonic, so re-entering a phase never walks the bar back.
                await slice.Begin(DrydockPhase.Fetch, 0);
                GuardRetrieveResume(ctx);

                var stored = revision == current.Ship.CurrentRevision
                    ? current
                    : await slice.Await(_store.LoadRevision(ctx.ShipId, revision));

                if (stored == null)
                    continue;

                byte[] yamlBytes;
                try
                {
                    yamlBytes = DecompressZstd(stored.Blob);
                }
                catch (Exception e)
                {
                    Log.Error($"Drydock: {ctx.ShipId} revision {revision} would not decompress, falling back: {e.Message}");
                    continue;
                }

                if (!SHA256.HashData(yamlBytes).AsSpan().SequenceEqual(stored.Revision.Checksum))
                {
                    Log.Error($"Drydock: {ctx.ShipId} revision {revision} failed its checksum, falling back.");
                    continue;
                }

                timer.Mark("fetch");

                // A fallback is a retrieve of an older state than the one the player last put
                // away, and the newer state is still on disk for now. It goes on the timeline so
                // an admin can see it before pruning takes the skipped document, because a
                // fallback followed by a few ordinary stores is how a latest state disappears.
                if (revision != current.Ship.CurrentRevision)
                {
                    Log.Warning($"Drydock: {ctx.ShipId} retrieved from fallback revision {revision}; revision {current.Ship.CurrentRevision} is unreadable.");
                    await slice.Await(_store.WriteAudit(new DrydockAudit
                    {
                        ShipGuid = ctx.ShipId,
                        ShipName = current.Ship.ShipName,
                        BerthId = current.Ship.BerthId,
                        Action = DrydockAuditAction.Fallback,
                        ActorUserId = ctx.OwnerUserId,
                        Revision = revision,
                        RoundId = ctx.RoundId,
                        Reason = $"revision {current.Ship.CurrentRevision} would not load; retrieved from {revision}",
                    }));
                }

                // One un-yieldable call, and it stays that way. The deserializer's stages are public
                // but its constructor wants the renamed-prototype maps that only the map loader's
                // own pre-read event produces, and the merge, the map-id assignment, the transform
                // pass and the merge epilogue are all private, so a content-side staged loader would
                // be a reimplementation of the engine rather than a slice of it. The honest per-tick
                // claim for a retrieve is the budget plus this one call.
                await slice.Begin(DrydockPhase.Load, 0);
                GuardRetrieveResume(ctx);

                if (!TryLoadOntoStagingMap(ctx, jobId, revision, yamlBytes))
                    continue;

                var grid = ctx.Grid!.Value;
                timer.Mark("load");

                // The whole tree, containers included, counted once. It pays for itself twice:
                // without a count the timings below cannot be compared between a shuttle and a
                // capital, and it is the denominator the progress percentage divides by, which is
                // what makes an unbounded wait bearable for the player who pressed the button. Note
                // this is a deeper number than the direct-child tally the retrieve used to log, so
                // timing lines from before the sliced pipeline do not compare with these.
                ctx.EntityCount = CountTree(grid);

                // A document with no shuttle component describes something that cannot dock or fly.
                // Treat it as an unusable revision and try the one before it.
                if (!HasComp<ShuttleComponent>(grid))
                {
                    ScrapRetrieveStaging(ctx);
                    Log.Error($"Drydock: {ctx.ShipId} revision {revision} has no shuttle component, falling back.");
                    continue;
                }

                try
                {
                    await ReviveSliced(grid, stored.Ship, slice, timer);

                    // The dock is one bulk call and must not be split: it sets the coordinates,
                    // snaps the translation, and welds a joint for every dock pair, all in one go.
                    // Docking a frozen ship is fine, because the dock finder walks the transform
                    // tree rather than a paused-skipping query, so the last revive slice and the
                    // dock can share a tick.
                    //
                    // The dock is also the thaw. Moving the grid onto the station's unpaused map
                    // unpauses the whole subtree inside that same engine walk, carrying the
                    // residency the ship actually spent on the staging map. Thawing first would pay
                    // for a second full-tree walk and buy a window of ticks with the ship live,
                    // alone and airless on a private map with its stored velocity restored.
                    await slice.Begin(DrydockPhase.Dock, 0);
                    GuardRetrieveResume(ctx);

                    // Re-resolved, not re-checked. The station's largest grid was read before the
                    // database work and the pipeline has since spent many ticks parked; a grid
                    // split, a new grid, or the station dying all change the answer. The claim that
                    // one re-check covers everything from here to the dock was true only while that
                    // whole span was synchronous, which is exactly what slicing took away.
                    if (ResolveDockTarget(ctx.StationUid) is not { } dockTarget)
                    {
                        ScrapRetrieveStaging(ctx);
                        Log.Warning($"Drydock: {ctx.ShipId} had its dock target die mid-retrieve; refused.");
                        return new DrydockRetrieveOutcome(DrydockRetrieve.Refused(DrydockRetrieveResult.StationLost));
                    }

                    // Re-resolved for the reason no component reference is ever held across a
                    // suspension: the dictionary behind it is mutated by anything that spawns or
                    // removes a component anywhere on the server.
                    if (!TryComp<ShuttleComponent>(grid, out var shuttle))
                        throw new DrydockAbortedException($"the loaded grid for {ctx.ShipId} lost its shuttle component");

                    // The dock a purchase of this hull would pick: the vessel's priority tag steers
                    // the choice toward the shipyard's own docks. Without it a retrieve took whatever
                    // dock was free first ("my ship spawned on the other side of Venmar").
                    string? dockTag = null;
                    if (stored.Ship.VesselProto is { } vesselId && _protoMan.TryIndex<VesselPrototype>(vesselId, out var vesselProto))
                        dockTag = vesselProto.PriorityDockTag;

                    if (!_shuttle.TryFTLDock(grid, shuttle, dockTarget, priorityTag: dockTag))
                        Log.Warning($"Drydock: {ctx.ShipId} found no docking config at {ToPrettyString(ctx.StationUid)}; presented by proximity.");

                    timer.Mark("dock");

                    // TryFTLDock returns false both when proximity placed the ship and when its
                    // first guard moved nothing (ShuttleSystem.FasterThanLight.cs:1185-1203), so the
                    // bool cannot say where the ship is. ScrapRetrieveStaging deletes what is still
                    // on the staging map, so trusting it scraps the hull and reports Success.
                    // Refusing is safe here: the grid is a copy and the revision is untouched.
                    if (ctx.StagingMap is { } stillStaged
                        && Exists(stillStaged)
                        && Transform(grid).MapUid == stillStaged)
                    {
                        Log.Error($"Drydock: {ctx.ShipId} would not leave the staging map; "
                                  + $"{ToPrettyString(dockTarget)} has no map to dock against. Refused rather than scrapped.");

                        ScrapRetrieveStaging(ctx);
                        return new DrydockRetrieveOutcome(DrydockRetrieve.Refused(DrydockRetrieveResult.StationLost));
                    }

                    ctx.ClaimHeld = false; // The claim is now correct: the ship really is out.
                    ctx.Presented = true;
                }
                catch
                {
                    // A throw here would otherwise leave a live grid stranded on a private map while
                    // the claim is released, which is the duplicate this whole gate exists to
                    // prevent. Scrap it, then let the failure travel. Synchronous on purpose: this
                    // runs on the cancellation path too, where an await could never complete.
                    ScrapRetrieveStaging(ctx);
                    throw;
                }

                // Nothing may await between the dock above and the return below. The ship is docked
                // and the claim has been handed over, so a cancellation observed here would report a
                // failed retrieve about a ship that is visibly parked at the station.
                ScrapRetrieveStaging(ctx);

                return new DrydockRetrieveOutcome(new DrydockRetrieve(DrydockRetrieveResult.Success, grid));
            }

            Log.Error($"Drydock: {ctx.ShipId} has no revision that verifies; retrieve refused.");
            return new DrydockRetrieveOutcome(DrydockRetrieve.Refused(DrydockRetrieveResult.NoReadableRevision));
        }
        catch (DrydockAbortedException e)
        {
            // The world moved under a parked pipeline. That is a normal outcome, not a failure:
            // letting it reach the job's process wrapper would log it at Error and fail every pooled
            // integration pair that exercised a cancellation.
            Log.Warning($"Drydock: retrieve of {ctx.ShipId} aborted, {e.Reason}.");
            ScrapRetrieveStaging(ctx);
            return new DrydockRetrieveOutcome(DrydockRetrieve.Refused(DrydockRetrieveResult.Cancelled));
        }
        catch (OperationCanceledException)
        {
            ScrapRetrieveStaging(ctx);
            throw;
        }
    }

    /// <summary>
    /// Loads one revision onto a private, paused map of its own.
    ///
    /// <para>The map-creating overload is what does the work: it creates the map map-initialised and
    /// pauses it while it is still empty, so the engine's recursive pause walks one entity instead
    /// of a whole hull, and the loader's merge epilogue then applies the target map's pause state to
    /// everything it loaded. The mechanism is the target map's state and not the document's own
    /// per-entity paused flags. Documents written by the older readers carry no such flags at all,
    /// deriving pause from a file-level one instead, and it makes no difference here.</para>
    ///
    /// <para>A map per retrieve also retires the spacing scheme that came before it. The shared
    /// shipyard map is unpaused and holds purchases and dead drops, so every retrieve used to be
    /// dropped a thousand tiles further along it to keep two hulls from overlapping for the part of
    /// a tick they shared. Residency is measured in seconds now, which is precisely the case that
    /// spacing could not have covered, and a private map has nothing to overlap with.</para>
    /// </summary>
    private bool TryLoadOntoStagingMap(DrydockRetrieveContext ctx, int jobId, int revision, byte[] yamlBytes)
    {
        var options = new DeserializationOptions
        {
            InitializeMaps = true,
            PauseMaps = true,
        };

        using var reader = new StreamReader(new MemoryStream(yamlBytes), Encoding.UTF8);

        if (!_mapLoader.TryLoadGrid(reader, $"drydock/{ctx.ShipId}", out var map, out var loaded, options))
        {
            Log.Error($"Drydock: {ctx.ShipId} revision {revision} passed its checksum but would not load.");
            return false;
        }

        ctx.StagingMap = map.Value.Owner;
        ctx.Grid = loaded.Value.Owner;
        ctx.RevisionLoaded = revision;

        // Tagged after the fact, because the loader made the map rather than us. The tag is what
        // lets the orphan sweep tell a map whose pipeline is still running from one whose pipeline
        // died holding it.
        TagStagingMap(ctx.StagingMap.Value, jobId, DrydockStagingKind.Retrieve, ctx.ShipId);
        return true;
    }

    /// <summary>
    /// Clears away a retrieve's staging map, and the half-built ship on it if the ship is still
    /// there.
    ///
    /// <para>Both halves are deliberate. Discarding a grid whose revision turned out to be unusable
    /// is what the retrieve has always done, and the map scrap refuses outright while a grid is
    /// still parented to it, so the order is load-bearing. After a successful dock the grid has
    /// moved to the station's map and only the empty staging map is taken, which is why the success
    /// path calls this too.</para>
    /// </summary>
    private void ScrapRetrieveStaging(DrydockRetrieveContext ctx)
    {
        if (ctx.StagingMap is not { } map)
            return;

        if (!Exists(map))
        {
            ctx.StagingMap = null;
            return;
        }

        if (ctx.Grid is { } grid && Exists(grid) && Transform(grid).MapUid == map)
            Del(grid);

        ScrapStagingMap(map);
        ctx.StagingMap = null;

        // A dead uid left here makes the next revision's resume guard throw, killing the fallback.
        ctx.Grid = null;
    }

    /// <summary>
    /// Throws when the world moved under a pipeline that was parked. Called after every suspension:
    /// the grid and its map are both deletable by an admin, a round restart or a crash cleanup at
    /// any point between two slices, and every step past this one reads one or both.
    /// </summary>
    private void GuardRetrieveResume(DrydockRetrieveContext ctx)
    {
        if (ctx.StagingMap is { } map && !Exists(map))
            throw new DrydockAbortedException($"the staging map for {ctx.ShipId} was deleted");

        if (ctx.Grid is { } grid && TerminatingOrDeleted(grid))
            throw new DrydockAbortedException($"the loaded grid for {ctx.ShipId} was deleted");
    }

    /// <summary>
    /// Step and guard, the pair every sliced sweep in the revive epilogue calls.
    ///
    /// <para>Scoped to the grid rather than to the whole retrieve context, because the sweeps are
    /// handed the grid and nothing else. That is enough: everything downstream of a sweep reads the
    /// grid, the staging map cannot outlive the grid on it, and the pipeline's own guard covers the
    /// map at the two boundaries where it matters. What this catches is the case worth catching, an
    /// admin or a round restart deleting the ship while a hundred ticks of epilogue are still queued
    /// up against it.</para>
    /// </summary>
    private async Task SweepStep(EntityUid grid, IDrydockSlice slice, int index)
    {
        await slice.Step(index);

        if (TerminatingOrDeleted(grid))
            throw new DrydockAbortedException($"{ToPrettyString(grid)} was deleted mid-revive");
    }

    /// <summary>
    /// Begin and guard, the pair every phase of the revive epilogue opens with.
    ///
    /// <para><see cref="SweepStep"/> covers a grid that dies mid-sweep but cannot cover one that is
    /// already gone when the sweep starts: <c>SnapshotOnGrid</c> matches nothing on a deleted grid,
    /// so the loop body never runs and neither does the guard inside it. The epilogue would then
    /// walk every remaining sweep over a corpse and reach the Station block, where
    /// <c>EnsureComp</c> throws a plain <see cref="ArgumentException"/> that the pipeline does not
    /// catch. Every Begin here force-suspends, so every one of them is a window.</para>
    /// </summary>
    private async Task ReviveBegin(EntityUid grid, IDrydockSlice slice, DrydockPhase phase, int items)
    {
        await slice.Begin(phase, items);

        if (TerminatingOrDeleted(grid))
            throw new DrydockAbortedException($"{ToPrettyString(grid)} was deleted mid-revive");
    }

    /// <summary>
    /// The station's largest grid, resolved fresh. Never a remembered uid: the whole reason this
    /// exists is that the answer changes while a pipeline is parked.
    /// </summary>
    private EntityUid? ResolveDockTarget(EntityUid stationUid)
    {
        if (!Exists(stationUid) || !TryComp<StationDataComponent>(stationUid, out var stationData))
            return null;

        return _station.GetLargestGrid(stationData) is { } target && Exists(target)
            ? target
            : null;
    }

    /// <summary>
    /// Puts back everything a normal spawn would have set up but a restored entity never gets, a
    /// few milliseconds of main-thread time at a time.
    ///
    /// <para>This is the map-init boundary made concrete. A restored entity comes back already
    /// marked initialized, so <c>MapInitEvent</c> never fires for it again. That is deliberate and
    /// necessary, since re-firing it would re-run every one-shot spawner aboard. The cost is that
    /// anything a system only ever does on map init has to be done again here, by name.</para>
    ///
    /// <para>Each step below is a system whose runtime registration lives entirely behind that
    /// event or the purchase path. Without them a retrieved ship comes back with dead machines that
    /// look perfectly fine, or a helm its own captain cannot unlock.</para>
    ///
    /// <para>The order is load-bearing and each step carries its own reason for standing where it
    /// does. Slicing may reorder entities inside a step; it must never reorder the steps.</para>
    /// </summary>
    /// <param name="timer">
    /// Split into its own phases when given, because "revive" as one number cannot say whether the
    /// cost is the fidelity restore or the per-system sweeps below it, and those have opposite fixes.
    /// Under slicing every one of these phases is wall clock rather than main-thread time, which is
    /// why the timing line carries a worst-slice figure beside them.
    /// </param>
    private async Task ReviveSliced(EntityUid grid, DrydockShip record, IDrydockSlice slice, DrydockPhaseTimer? timer = null)
    {
        await ReviveBegin(grid, slice, DrydockPhase.Fidelity, 0);

        // The general fidelity net first: everything captured into a sidecar goes back before
        // anything else reads component state.
        var restore = await _fidelity.RestoreCapturedSliced(grid, slice);
        if (restore.Skipped.Count > 0)
            Log.Warning($"Drydock: {record.ShipGuid} restored with {restore.Skipped.Count} captured field(s) skipped.");

        // Appearance next, and before every step below it. Those steps correct specific machines
        // against the state the ship actually came back with, and a machine's captured appearance
        // can disagree with that: a lathe stored mid-production comes back without the marker that
        // made the animation true. Restoring appearance after them would reinstate exactly the
        // frozen animations this is here to end.
        var appearance = await _fidelity.RestoreAppearanceSliced(grid, slice);
        if (appearance.Skipped.Count > 0)
            Log.Warning($"Drydock: {record.ShipGuid} restored with {appearance.Skipped.Count} appearance key(s) skipped.");

        // Scrubs for documents written before the store learned to strip these. A stale FTL
        // component leaves the ship stuck mid-jump with the shuttle system erroring every tick. A
        // stale in-progress marker is the store's re-entrancy sentinel, so a ship whose document
        // carries one has every later store of it refused as already in progress, permanently. No
        // code change heals a document that is already filed, which is why both scrubs stay.
        if (HasComp<FTLComponent>(grid))
            RemComp<FTLComponent>(grid);

        if (HasComp<DrydockInProgressComponent>(grid))
            RemComp<DrydockInProgressComponent>(grid);

        timer?.Mark("fidelity");

        await ReviveGravitySliced(grid, slice);
        await ReviveNpcsSliced(grid, slice);
        await ReviveWiresSliced(grid, slice);
        await ReviveDeviceNetworkSliced(grid, slice);
        await ReviveResearchClientsSliced(grid, slice);
        await ReviveConsoleLocksSliced(grid, slice);
        await ReviveGeneratorsSliced(grid, slice);
        await ReviveSmartFridgesSliced(grid, slice);
        await ReviveArtifactAnalyzersSliced(grid, slice);
        await ReviveFilledHandsSliced(grid, slice);
        await ReviveDispenserSlotsSliced(grid, slice);
        await ReviveCabinetLocksSliced(grid, slice);
        await ScrubStaleLatheProductionSliced(grid, slice);

        timer?.Mark("sweeps");

        await RehydrateDamageSliced(grid, slice);

        // The repair baseline is derived state, stripped at store. Retrieve fires neither map init
        // nor the purchase event, and the repair system subscribes only to the latter.
        _shipRepair.GenerateRepairData(grid);

        timer?.Mark("damage");

        await ReviveBegin(grid, slice, DrydockPhase.Station, 0);

        // The row is authoritative for the name too: a rename made while the ship was stored is a
        // row update, and this is where the hull and its deed learn it. Before the station, which
        // takes its name from the grid.
        _shipyard.StampStoredName(grid, record.ShipName);
        RefreshShipOwnership(grid, record);
        RecreateStation(grid, record);

        timer?.Mark("station");
    }

    /// <summary>
    /// Every entity on <paramref name="grid"/> carrying <typeparamref name="T"/>, materialised into
    /// a list the sweeps below can walk across ticks.
    ///
    /// <para>A query enumerator wraps a live component dictionary and cannot be parked, so every
    /// sweep snapshots first and re-resolves each uid as it consumes it. The snapshot pass itself
    /// walks every instance of the component on the server and cannot yield part-way, which is the
    /// sweeps' own un-yieldable floor; it is the same walk the epilogue has always done, once per
    /// sweep.</para>
    ///
    /// <para>Deliberately <c>AllEntityQuery</c> rather than the paused-skipping enumerator. The ship
    /// is frozen while all of this runs, and the paused-skipping one would return nothing at
    /// all.</para>
    /// </summary>
    private List<EntityUid> SnapshotOnGrid<T>(EntityUid grid) where T : IComponent
    {
        var found = new List<EntityUid>();
        var query = AllEntityQuery<T, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == grid)
                found.Add(uid);
        }

        return found;
    }

    /// <summary>
    /// A gravity generator pushes gravity onto its grid only on the edge where its charge
    /// activates. On load the charge comes back already full, so the loop sees no edge and never
    /// pushes, while the generator's own active flag is not serialized and reads false. The result
    /// is a live generator and no gravity. Re-raising the activation lets its own handler do the
    /// work, which matters because the component is access-locked to that system.
    /// </summary>
    private async Task ReviveGravitySliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<GravityGeneratorComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (Exists(uid))
            {
                var activated = new ChargedMachineActivatedEvent();
                RaiseLocalEvent(uid, ref activated);
            }

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// Autopilot is an HTN behaviour on the shuttle console, and turrets and drones are HTN too.
    /// The active marker and the blackboard's owner are installed only on map init, so without this
    /// a restored ship's autopilot never plans and never steers.
    ///
    /// <para>The stored autopilot destination is dropped deliberately: a ship that has been sitting
    /// in a drydock has no business resuming a course to a point that may no longer mean
    /// anything.</para>
    /// </summary>
    private async Task ReviveNpcsSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<HTNComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];

            // A minded HTN should not have survived the organics gate, but the NPC system refuses to
            // wake one anyway, so match that rather than fight it.
            if (TryComp<HTNComponent>(uid, out var htn)
                && (!TryComp<MindContainerComponent>(uid, out var mind) || !mind.HasMind))
            {
                htn.Blackboard.SetValue(NPCBlackboard.Owner, uid);

                if (TryComp<ShuttleConsoleComponent>(uid, out var console))
                    htn.Blackboard.Remove<EntityCoordinates>(console.AutopilotTargetKey);

                _npc.WakeNPC(uid, htn);
            }

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// A wired machine's actual wire list is not a data field, so it does not persist, and the only
    /// thing that ever built it was map init. Without this every panel on a restored ship opens
    /// empty: nothing to cut, nothing to pulse, on every airlock and every APC aboard.
    /// </summary>
    private async Task ReviveWiresSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<WiresComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<WiresComponent>(uid, out var wires) && !string.IsNullOrEmpty(wires.LayoutId))
                _wires.SetOrCreateWireLayout(uid, wires);

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// Device network membership is runtime registration held by the network system, not state on
    /// the device, and joining happens on map init. Without this a restored ship's air alarms,
    /// sensors and consoles are all present, all powered, and all deaf.
    /// </summary>
    private async Task ReviveDeviceNetworkSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<DeviceNetworkComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<DeviceNetworkComponent>(uid, out var device))
                _deviceNetwork.ConnectDevice(uid, device);

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// A research client's server link is a plain property rather than a data field, and the
    /// registration that sets it runs on map init by scanning the client's own grid for servers.
    /// This repeats that scan, which is the same shape and therefore the same result.
    /// </summary>
    private async Task ReviveResearchClientsSliced(EntityUid grid, IDrydockSlice slice)
    {
        var servers = SnapshotOnGrid<ResearchServerComponent>(grid);

        // No servers aboard means nothing to register against, and the client walk is a whole
        // component sweep of the server that would find nothing to do with its answer.
        var clients = servers.Count == 0
            ? new List<EntityUid>()
            : SnapshotOnGrid<ResearchClientComponent>(grid);

        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, clients.Count);

        for (var i = 0; i < clients.Count; i++)
        {
            var uid = clients[i];
            if (TryComp<ResearchClientComponent>(uid, out var client))
            {
                foreach (var serverUid in servers)
                {
                    if (TryComp<ResearchServerComponent>(serverUid, out var server))
                        _research.RegisterClient(uid, serverUid, client, server);
                }
            }

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// A console lock and the grid lock beside it hold the ship's uid as a STRING, the one uid on
    /// the ship the loader cannot remap, so every locked console comes back keyed to a dead grid.
    /// The deed names the new grid and the unlock compares the two as strings, so the captain's own
    /// deed will not open the helm. Stamps every console with the live uid, as purchase and ship
    /// load both do.
    /// </summary>
    private async Task ReviveConsoleLocksSliced(EntityUid grid, IDrydockSlice slice)
    {
        var shuttleId = grid.ToString();
        var targets = SnapshotOnGrid<ShuttleConsoleLockComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<ShuttleConsoleLockComponent>(uid, out var lockComp))
                _consoleLock.SetShuttleId(uid, shuttleId, lockComp);

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// A fuel generator's on flag is written into the save, but it did not survive
    /// the load: the transform system raises AnchorStateChangedEvent on every entity that starts up
    /// anchored, and the generator's handler switched off on any anchor change rather than only on
    /// coming unanchored. So the flag arrived true and was false by the time this ran, this step
    /// skipped every generator, and the fleet sweep showed On: True -> False on 73 of them. The
    /// handler is fixed at the source (GeneratorSystem.OnAnchorStateChanged); this step stays for
    /// what the flag drives and the save does not carry: the running sprite, the ambient hum, the
    /// radiation source and its glow. Re-applying the flag through the generator system re-derives
    /// all of it.
    /// </summary>
    private async Task ReviveGeneratorsSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<FuelGeneratorComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<FuelGeneratorComponent>(uid, out var generator) && generator.On)
                _generator.SetFuelGeneratorOn(uid, true, generator);

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// A smart fridge's stock listing is an index over its container, rebuilt on map init because
    /// its key type cannot be a YAML mapping key. No map init here, so rebuild it by hand, or a
    /// stocked fridge reports itself empty and its contents are unreachable.
    /// </summary>
    private async Task ReviveSmartFridgesSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<SmartFridgeComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<SmartFridgeComponent>(uid, out var fridge))
                _smartFridge.RebuildEntries((uid, fridge));

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// An analysis console holds its analyzer as a NetEntity, which no loader remaps, and the only
    /// thing that re-resolves it from the device-link wire is the analyzer's map init. Without this
    /// the pair comes back linked on the wire and dead on the console.
    /// </summary>
    private async Task ReviveArtifactAnalyzersSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<ArtifactAnalyzerComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<ArtifactAnalyzerComponent>(uid, out var analyzer))
                _artifactAnalyzer.RelinkConsole((uid, analyzer));

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// A machine's hands are not data fields; the hand-fill component declares them and map init
    /// creates them, so a retrieved robotic arm has no hand to hold its tool in. Re-creates every
    /// declared hand that is missing. Fill items are NOT spawned again: what was in the hand
    /// persisted as a container child and is picked back up, and an empty hand was emptied on
    /// purpose.
    /// </summary>
    private async Task ReviveFilledHandsSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<HandsFillComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<HandsFillComponent>(uid, out var fill) && TryComp<HandsComponent>(uid, out var hands))
            {
                foreach (var name in fill.Hands.Keys)
                {
                    if (!_hands.TryGetHand(uid, name, out _, hands))
                        _hands.AddHand(uid, name, HandLocation.Middle, hands);
                }
            }

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// The item-slot registry is a read-only data field, so a slot added at runtime is never saved
    /// and only the prototype's own slots come back. A reagent dispenser registers its beaker slot on
    /// map init and its storage slots from its parts, so a retrieved one has jugs in containers no
    /// slot knows about and nowhere to put a new one. The slot definitions persist on the dispenser;
    /// re-registering them finds the jugs already in their containers.
    /// </summary>
    private async Task ReviveDispenserSlotsSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<ReagentDispenserComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<ReagentDispenserComponent>(uid, out var dispenser)
                && TryComp<ItemSlotsComponent>(uid, out var itemSlots))
            {
                if (!_itemSlots.TryGetSlot(uid, SharedReagentDispenser.OutputSlotName, out _, itemSlots))
                    _itemSlots.AddItemSlot(uid, SharedReagentDispenser.OutputSlotName, dispenser.BeakerSlot, itemSlots);

                var count = Math.Min(dispenser.StorageSlotIds.Count, dispenser.StorageSlots.Count);
                for (var slot = 0; slot < count; slot++)
                {
                    if (!_itemSlots.TryGetSlot(uid, dispenser.StorageSlotIds[slot], out _, itemSlots))
                        _itemSlots.AddItemSlot(uid, dispenser.StorageSlotIds[slot], dispenser.StorageSlots[slot], itemSlots);
                }
            }

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// Same read-only registry: a slot's lock state is not saved either, and a cabinet locks its
    /// slot to its door on map init. A retrieved closed cabinet therefore handed out its contents
    /// through the closed door ("cabinets that require them to be opened no longer do").
    /// </summary>
    private async Task ReviveCabinetLocksSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<ItemCabinetComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<ItemCabinetComponent>(uid, out var cabinet) && TryComp<ItemSlotsComponent>(uid, out var itemSlots))
                _itemSlots.SetLock(uid, cabinet.Slot, !_openable.IsOpen(uid), itemSlots);

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// Two things for every lathe aboard. First, a scrub for documents written before the
    /// producing marker opted out of saving: a lathe stored mid-print reloaded carrying the marker
    /// with no recipe behind it, the lathe loop skips a producing lathe with no recipe and the
    /// reboot pass skips any lathe that is producing, so it neither finished nor restarted, forever.
    /// Dropping the marker lets the reboot pass resume the queue, which is the state that actually
    /// persisted. Second, the appearance keys the lathe's map init sets ("appearance requires
    /// initialization or the layers break", in its own words), set here against the marker the ship
    /// came back with rather than the one it was stored with.
    ///
    /// <para>The appearance carrier restores both keys before this runs, and that is not enough on
    /// its own: what it restores is what the lathe looked like at store, and a lathe stored mid-print
    /// looked like it was running. The marker behind that animation does not ride the save, so this
    /// is the step that settles the two against each other.</para>
    /// </summary>
    private async Task ScrubStaleLatheProductionSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<LatheComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Sweeps, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<LatheComponent>(uid, out var lathe))
            {
                var producing = HasComp<LatheProducingComponent>(uid);
                if (producing && lathe.CurrentRecipe == null)
                {
                    RemCompDeferred<LatheProducingComponent>(uid);
                    producing = false;
                }

                _appearance.SetData(uid, LatheVisuals.IsInserting, false);
                _appearance.SetData(uid, LatheVisuals.IsRunning, producing);
            }

            await SweepStep(grid, slice, i);
        }
    }

    // Triad: ReviveUseDelays retired in favour of the generic offset path, which is strictly more
    // faithful rather than merely cheaper.
    //
    // The problem it was written for is real: a use delay's end is an absolute game time and the
    // clock starts over every round, so a half-second on a bag written in one round would read as
    // hours in the next and nothing aboard would open on a press. What solves it is not this method.
    // UseDelayInfo's times carry the engine's time-offset serializer, so a loaded delay arrives
    // already re-based onto this round's clock with its REMAINING time intact, before anything here
    // could look at it. Re-arming on top of that throws the remaining time away and hands every item
    // aboard a fresh full-length delay. Preserving what is left is the faithful answer and re-arming
    // is not, so the correct step is no step.
    //
    // Kept as a comment because the reasoning is the whole value. The old doc justified re-arming as
    // "the same pass the ship-load path runs", and that path re-arms because it has no offset it can
    // trust, not because re-arming is right.
    //
    // private void ReviveUseDelays(EntityUid grid)
    // {
    //     var query = AllEntityQuery<UseDelayComponent, TransformComponent>();
    //     while (query.MoveNext(out var uid, out var delay, out var xform))
    //     {
    //         if (xform.GridUid != grid)
    //             continue;
    //
    //         _useDelay.ResetAllDelays((uid, delay));
    //     }
    // }

    /// <summary>
    /// Applies each damage sidecar back onto its holder and removes it.
    ///
    /// <para>Run from this explicit pass rather than a startup hook on purpose. Applying damage at
    /// component startup fires the damage-changed event into the destructible system while the grid
    /// is still settling, which risks tripping a destruction threshold against state that is not
    /// final yet. Running after the load has returned lets thresholds see a finished ship, and it is
    /// why this keeps its place at the end of the epilogue: a slice boundary that moved it earlier
    /// would reintroduce exactly that.</para>
    ///
    /// <para>Snapshot-then-apply, and not only for the reason every sweep here is. This one removes
    /// the very component it selects on, so consuming a live query would be reading a dictionary it
    /// is mutating as it goes.</para>
    /// </summary>
    private async Task RehydrateDamageSliced(EntityUid grid, IDrydockSlice slice)
    {
        var targets = SnapshotOnGrid<DrydockDamageSidecarComponent>(grid);
        await ReviveBegin(grid, slice, DrydockPhase.Damage, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            var uid = targets[i];
            if (TryComp<DrydockDamageSidecarComponent>(uid, out var sidecar)
                && TryComp<DamageableComponent>(uid, out var damageable))
            {
                var damage = new DamageSpecifier { DamageDict = new Dictionary<string, FixedPoint2>(sidecar.DamageDict) };
                _damageable.SetDamage(uid, damageable, damage);
                RemComp<DrydockDamageSidecarComponent>(uid);
            }

            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// The row is authoritative for ownership, and this is where the grid learns it. The
    /// ownership component rides the document, so without this a transferred ship would come back
    /// stamped with its previous owner, the console would refuse the new owner's store as "not
    /// yours", and a store by the old owner would file the row back under them. Its last-status
    /// timestamp is round-scoped absolute time too, and a previous round's clock would feed the
    /// offline-deletion timer nonsense, so that is re-derived here as well.
    /// </summary>
    private void RefreshShipOwnership(EntityUid grid, DrydockShip record)
    {
        var ownership = EnsureComp<ShipOwnershipComponent>(grid);

        ownership.OwnerUserId = new NetUserId(record.OwnerUserId);
        ownership.IsOwnerOnline = _player.TryGetSessionById(ownership.OwnerUserId, out _);
        ownership.LastStatusChangeTime = _timing.CurTime;
        Dirty(grid, ownership);
    }

    /// <summary>
    /// The station a hull with no vessel prototype gets. Built here rather than as a gameMap
    /// prototype because gameMap requires a mapPath, and a fallback has no map to point at; a
    /// prototype naming a file it never loads would be a lie in the data.
    ///
    /// <para><c>StandardFrontierVessel</c> is what every vessel's own station config names, so this
    /// differs from a purchased ship's station only in carrying no per-vessel job list, name
    /// template or vessel id. It brings the station records, expedition data and job spawning that
    /// being stationless takes away.</para>
    /// </summary>
    private static readonly StationConfig GenericVesselStation = new()
    {
        StationPrototype = "StandardFrontierVessel",
        StationComponentOverrides = new ComponentRegistry(),
    };

    /// <summary>
    /// The station a ship comes back as. It is recreated rather than restored because a station is
    /// round-scoped, and the ship's own name is passed through so a player's rename survives rather
    /// than being replaced by the prototype's name generator.
    ///
    /// <para>A row whose vessel prototype is missing still gets a station, just a plain one. Coming
    /// back stationless is not a safe answer: without <c>StationMemberComponent</c> a ship draws
    /// yellow on radar instead of white and is invisible to station records, expeditions and
    /// everything else keyed on stations. Legacy imports are the population that hits this, because
    /// the vessel id lived on the station entity and a save only ever carried the grid, so there is
    /// nothing in the document to recover it from and no amount of re-saving will conjure one.</para>
    /// </summary>
    private void RecreateStation(EntityUid grid, DrydockShip record)
    {
        // The roundstart variation passes run on every new station and read this marker off the
        // station, which is recreated below, so a retrieved ship was re-varied on every retrieve:
        // fresh trash and spills each time, some of it inside the hull. The marker on the grid is
        // what the rule now honours, and it rides the document, so a ship is varied once at most.
        // Stamped before the vessel check: a stationless retrieve must not be varied later either.
        EnsureComp<StationVariationHasRunComponent>(grid);

        StationConfig stationConfig;
        bool known;

        if (!string.IsNullOrEmpty(record.VesselProto)
            && _protoMan.TryIndex<GameMapPrototype>(record.VesselProto, out var stationProto)
            && stationProto.Stations.TryGetValue(record.VesselProto, out var vesselConfig))
        {
            stationConfig = vesselConfig;
            known = true;
        }
        else
        {
            Log.Info($"Drydock: {record.ShipGuid} has no vessel prototype ('{record.VesselProto}'); giving it a plain station.");
            stationConfig = GenericVesselStation;
            known = false;
        }

        var station = _station.InitializeNewStation(stationConfig, new[] { grid }, Name(grid));

        // Only a ship that came from a vessel can claim to be one. A generic station keeps the
        // component the prototype gives it, with no vessel named, rather than being labelled as a
        // hull it is not.
        if (known)
            EnsureComp<ExtraShuttleInformationComponent>(station).Vessel = record.VesselProto;
    }

    private static byte[] DecompressZstd(byte[] input)
    {
        using var decompress = new Robust.Shared.Utility.ZStdDecompressStream(new MemoryStream(input));
        using var output = new MemoryStream();
        decompress.CopyTo(output);
        return output.ToArray();
    }
}
