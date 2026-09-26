using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._NF.Station.Components;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Chemistry.Components;
using Content.Server.Database;
using Content.Server.Gravity;
using Content.Server.Lathe.Components;
using Content.Server.Maps;
using Content.Server.Power.EntitySystems;
using Content.Server.Research.Systems;
using Content.Server.Station;
using Content.Server.Station.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Triad.CCVar;
using Content.Shared.Chemistry;
using Content.Shared.FixedPoint;
using Content.Shared.Lathe;
using Content.Shared.Mind.Components;
using Content.Shared.Research.Components;
using Content.Shared.Shuttles.Components;
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
/// <para>Every sweep in the epilogue finds its targets by walking the ship's transform tree
/// (<see cref="SnapshotOnGrid{T}"/>), which has no paused check, so all of it works verbatim on a
/// frozen ship. That is what the whole design rests on; do not "fix" a sweep to
/// <c>EntityQueryEnumerator</c>, which skips paused entities and would find nothing.</para>
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private ResearchSystem _research = default!;
    [Dependency] private ShuttleConsoleLockSystem _consoleLock = default!;
    [Dependency] private SharedArtifactAnalyzerSystem _artifactAnalyzer = default!;

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

        // The row alone for the gates. The image is read by the pipeline instead, after the
        // claim, so a refusal never pays for reading one and the read that does happen cannot race
        // a transfer or a second retrieve.
        var header = await _store.GetShipHeader(shipId);
        if (header == null)
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NotFound);

        if (header.OwnerUserId != ownerUserId)
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NotOwned);

        // The row's state names the refusal before the claim is tried. The claim below still
        // decides: this read can be stale by the time the claim lands.
        switch (header.State)
        {
            case DrydockShipState.CheckedOut:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.AlreadyOut);
            case DrydockShipState.Impounded:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.Impounded);
            case DrydockShipState.InEscrow:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.InEscrow);
            case DrydockShipState.Sold:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.Sold);
            case DrydockShipState.Destroyed:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.Destroyed);
            case DrydockShipState.Abandoned:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.Abandoned);
        }

        // Claim before materializing. A ship that is checked out or impounded loses here.
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
        DrydockPipelineJob<DrydockRetrieveContext, DrydockRetrieveOutcome>? job = null;
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
                job = StartPipelineJob<DrydockRetrieveContext, DrydockRetrieveOutcome>(
                    ctx, budget, onProgress, DrydockPhases.Retrieve, RunRetrievePipeline);
                progress = job.Progress;

                // The job's own wrapper records a pipeline's exception and faults the task with it,
                // so a failed retrieve throws out of this await for its caller the same way it did
                // when the pipeline was called directly.
                var result = await job.AsTask;

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
            EndPipelineJob(job);

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
                        DrydockAuditAction.ClaimReleased, null, roundId, "retrieve failed");
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
    /// The retrieve itself, from the first image read to the dock. Driven either by a job, a few
    /// milliseconds of main-thread time per tick, or by <see cref="DrydockSyncSlice"/> straight
    /// through.
    ///
    /// <para>The inbound leg has a harder floor than the outbound one. The store writes one entity
    /// per step, but the load's four phases run inside one tick (<see cref="LoadOntoStagingMap"/>),
    /// and the slicing starts at the revive epilogue after it.</para>
    ///
    /// <para>The database claim is neither taken nor released here. The wrapper owns it, because a
    /// job that observes its cancellation can never finish another await, and the release is the one
    /// step that has to happen even then.</para>
    /// </summary>
    internal async Task<DrydockRetrieveOutcome> RunRetrievePipeline(DrydockRetrieveContext ctx, IDrydockSlice slice)
    {
        var timer = ctx.Timer;

        try
        {
            await slice.Begin(DrydockPhase.Fetch, 0);

            var current = await slice.Await(_store.LoadCurrentImage(ctx.ShipId));
            if (current == null)
                return new DrydockRetrieveOutcome(DrydockRetrieve.Refused(DrydockRetrieveResult.NotFound));

            // The ladder walks the images that exist, newest first, so a pinned image below the keep-N
            // window stays reachable and a pruned revision is never asked for.
            var revisions = await slice.Await(_store.ListRetrievableRevisions(ctx.ShipId));
            GuardRetrieveResume(ctx);

            // The newest revision the drift gate refused, for the refusal when the ladder runs out.
            (int Revision, DrydockDriftVerdict Verdict)? driftRefused = null;

            foreach (var revision in revisions)
            {
                var isCurrent = revision == current.Ship.CurrentRevision;

                // Re-opened per revision, so a fallback reads as a retry rather than as a stall. The
                // percentage is clamped monotonic, so re-entering a phase never walks the bar back.
                await slice.Begin(DrydockPhase.Fetch, 0);
                GuardRetrieveResume(ctx);

                var stored = isCurrent
                    ? current
                    : await slice.Await(_store.LoadRevisionImage(ctx.ShipId, revision));

                if (stored == null)
                    continue;

                timer.Mark("fetch");

                // The drift gate, over the store's pre-flight of the image and before the load: every
                // prototype id and component name the image carries has to resolve now, because the
                // engine allocates every entity before any row is read and a load cannot start on part
                // of an image (IDrydockImageStore.Preflight). A refusal names every one that does not.
                var preflight = await slice.Await(_store.PreflightImage(ctx.ShipId, revision));
                GuardRetrieveResume(ctx);
                if (preflight == null)
                    continue;

                var verdict = DetectDrift(preflight, ImageEngineFormat, stored.Revision.DrydockFormatVer);
                timer.Mark("drift");

                if (verdict.IsRefusal)
                {
                    var driftReason = DescribeDriftRefusal(verdict);
                    Log.Warning($"Drydock: {ctx.ShipId} revision {revision} references content that no longer resolves: {driftReason}");

                    // The current image is refused outright. An older one is older state, and it
                    // almost certainly references the same content, so falling back would trade a
                    // clear refusal for a ship that is both stale and likely just as broken.
                    if (isCurrent)
                        return await RefuseForDrift(ctx, slice, current.Ship, revision, driftReason);

                    // An older image reached only because newer ones would not load. Stepped past
                    // like one that would not load, and pinned the same way: it is a newer state than
                    // whatever this ladder ends up handing out.
                    driftRefused ??= (revision, verdict);
                    await PinSteppedPast(ctx, slice, revision, "references content that no longer exists");
                    continue;
                }

                // The honest per-tick claim for a retrieve is the budget plus this one load.
                await slice.Begin(DrydockPhase.Load, 0);
                GuardRetrieveResume(ctx);

                if (LoadOntoStagingMap(ctx, slice, revision, stored.Image) is not { } loaded)
                {
                    await PinSteppedPast(ctx, slice, revision, "its image would not load");
                    continue;
                }

                var grid = ctx.Grid!.Value;
                timer.Mark("load");

                // The whole tree, containers included, counted once. It pays for itself twice:
                // without a count the timings below cannot be compared between a shuttle and a
                // capital, and it is the denominator the progress percentage divides by, which is
                // what makes an unbounded wait bearable for the player who pressed the button. Note
                // this is a deeper number than the direct-child tally the retrieve used to log, so
                // timing lines from before the sliced pipeline do not compare with these.
                ctx.EntityCount = CountTree(grid);

                // An image with no shuttle component describes something that cannot dock or fly.
                // Treat it as an unusable revision and try the one before it.
                if (!HasComp<ShuttleComponent>(grid))
                {
                    ScrapRetrieveStaging(ctx);
                    Log.Error($"Drydock: {ctx.ShipId} revision {revision} has no shuttle component, falling back.");
                    await PinSteppedPast(ctx, slice, revision, "it loaded with no shuttle component");
                    continue;
                }

                try
                {
                    // A fallback is a retrieve of an older state than the one the player last put
                    // away, and the newer state is still on disk for now. It goes on the timeline so
                    // an admin can see it before pruning takes the skipped image, because a
                    // fallback followed by a few ordinary stores is how a latest state disappears.
                    // Written once the older image has actually loaded, so the row never
                    // names a revision the ladder then stepped past as well, and inside this try so a
                    // failed write scraps the loaded grid on its way out like any other throw.
                    if (!isCurrent)
                    {
                        Log.Warning($"Drydock: {ctx.ShipId} retrieved from fallback revision {revision}; revision {current.Ship.CurrentRevision} could not be used.");
                        DrydockMetrics.RetrieveFallbacks.Inc();
                        await slice.Await(_store.WriteAudit(new DrydockAudit
                        {
                            ShipGuid = ctx.ShipId,
                            ShipName = current.Ship.ShipName,
                            BerthId = current.Ship.BerthId,
                            Action = DrydockAuditAction.Fallback,
                            ActorUserId = ctx.OwnerUserId,
                            Revision = revision,
                            RoundId = ctx.RoundId,
                            Reason = $"revision {current.Ship.CurrentRevision} could not be used; retrieved from {revision}",
                        }));
                        GuardRetrieveResume(ctx);
                    }

                    await ReviveSliced(grid, stored.Ship, slice, timer);

                    // The dock is two atomic calls, not one: the config search, then the move.
                    // Each gets its own timer mark below, so a hitch here says which half it belongs
                    // to instead of blaming "dock" as a whole. Docking a frozen ship is fine either
                    // way, because the dock finder walks the transform tree rather than a
                    // paused-skipping query, so the last revive slice and the dock can share a tick.
                    //
                    // The move is also the thaw. Moving the grid onto the station's unpaused map
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
                    if (!HasComp<ShuttleComponent>(grid))
                        throw new DrydockAbortedException($"the loaded grid for {ctx.ShipId} lost its shuttle component");

                    // The dock a purchase of this hull would pick: the vessel's priority tag steers
                    // the choice toward the shipyard's own docks. Without it a retrieve took whatever
                    // dock was free first ("my ship spawned on the other side of Venmar"). Resolved
                    // the same way the station is, so an imported hull docks where its class does.
                    var dockTag = PriorityDockTagFor(ResolveVesselProto(grid, stored.Ship));

                    // The thaw, with every time the load restored as a sentinel held through it
                    // (DrydockImageSystem.PreserveSentinels).
                    _image.PreserveSentinels(loaded, () =>
                    {
                        // TryFTLDock's own first guard, kept: a target with no valid map goes straight
                        // to proximity, since the search reads the target's grid and transform bare.
                        var config = Transform(dockTarget).MapUid is { } targetMap && targetMap.IsValid()
                            ? _docking.GetDockingConfig(grid, dockTarget, dockTag, DockType.Airlock)
                            : null;
                        timer.Mark("dock_config");

                        if (config != null)
                        {
                            _shuttle.FTLDock((grid, Transform(grid)), config);
                        }
                        else
                        {
                            _shuttle.TryFTLProximity(grid, dockTarget);
                            Log.Warning($"Drydock: {ctx.ShipId} found no docking config at {ToPrettyString(ctx.StationUid)}; presented by proximity.");
                        }
                    });

                    timer.Mark("dock");

                    // Neither call above says where the ship ended up: FTLDock is void, and
                    // TryFTLProximity's bool only reports whether it moved anything, not where.
                    // ScrapRetrieveStaging deletes what is still on the staging map, so trusting
                    // either return scraps the hull and reports Success. Refusing is safe here: the
                    // grid is a copy and the revision is untouched.
                    if (ctx.StagingMap is { } stillStaged
                        && Exists(stillStaged)
                        && Transform(grid).MapUid == stillStaged)
                    {
                        Log.Error($"Drydock: {ctx.ShipId} would not leave the staging map; "
                                  + $"{ToPrettyString(dockTarget)} has no map to dock against. Refused rather than scrapped.");

                        ScrapRetrieveStaging(ctx);
                        return new DrydockRetrieveOutcome(DrydockRetrieve.Refused(DrydockRetrieveResult.StationLost));
                    }

                    // The ship's own station, now that the move has thawed it. Synchronous, so it fits
                    // the no-await span from the dock to the return. See ReviveSliced for why it
                    // cannot run on the frozen ship.
                    RecreateStation(grid, stored.Ship);
                    timer.Mark("station_init");

                    ctx.ClaimHeld = false; // The claim is now correct: the ship really is out.
                    ctx.Presented = true;
                }
                catch
                {
                    // A throw here would otherwise leave a live grid stranded on a private map while
                    // the claim is released, which is the duplicate this whole gate exists to
                    // prevent. Scrap it, then let the failure travel. Synchronous on purpose: this
                    // runs on the cancellation path too, where an await could never complete.
                    //
                    // A throw after the dock moved the grid but before it was presented (the station
                    // recreation above, or the dock itself) leaves the copy on the station's map, where
                    // the staging scrap cannot see it, with the claim about to be released. The copy
                    // is deleted for the same reason: the revision is untouched and the row goes back.
                    if (!ctx.Presented
                        && ctx.Grid is { } loose
                        && Exists(loose)
                        && Transform(loose).MapUid != ctx.StagingMap)
                    {
                        Del(loose);
                    }

                    ScrapRetrieveStaging(ctx);
                    throw;
                }

                // Nothing may await between the dock above and the return below. The ship is docked
                // and the claim has been handed over, so a cancellation observed here would report a
                // failed retrieve about a ship that is visibly parked at the station. It is also why
                // the job's own finally has to sample this tail explicitly: from Begin(Dock) - holding
                // TryFTLDock, one of the four calls content cannot interrupt - to here there is no
                // Await, Begin or Step, so nothing else would ever price it and every retrieve timing
                // line would under-report its own worst span without that last sample.
                ScrapRetrieveStaging(ctx);

                return new DrydockRetrieveOutcome(new DrydockRetrieve(DrydockRetrieveResult.Success, grid));
            }

            // A ladder that ran out having refused an image for drift says so, since content that no
            // longer exists is a different remedy from an image that will not load.
            if (driftRefused is { } refused)
            {
                return await RefuseForDrift(ctx, slice, current.Ship, refused.Revision,
                    $"no newer revision was readable; {DescribeDriftRefusal(refused.Verdict)}");
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
    /// Loads one revision's image onto a private, paused map of its own, in one tick: the loader's four
    /// phases, with the strip rule and the research reset between its start and its completion, so the
    /// restore events it raises last see the population that stays. Null when the image would not
    /// load, with whatever the load made scrapped.
    ///
    /// <para>The map is created paused while it is still empty, so the engine's recursive pause walks
    /// one entity instead of a whole hull (<see cref="CreateStagingMap"/>).</para>
    ///
    /// <para>A map per retrieve also retires the spacing scheme that came before it. The shared
    /// shipyard map is unpaused and holds purchases and dead drops, so every retrieve used to be
    /// dropped a thousand tiles further along it to keep two hulls from overlapping for the part of
    /// a tick they shared. Residency is measured in seconds now, which is precisely the case that
    /// spacing could not have covered, and a private map has nothing to overlap with.</para>
    /// </summary>
    private DrydockLoadResult? LoadOntoStagingMap(DrydockRetrieveContext ctx, IDrydockSlice slice, int revision, DrydockImage image)
    {
        ctx.StagingMap = CreateStagingMap(JobIdOf(slice), DrydockStagingKind.Retrieve, ctx.ShipId, mapInit: true);
        var session = _image.BeginLoad(image, ctx.StagingMap.Value, new DrydockLoadOptions { Migrations = MigrationTable });
        try
        {
            session.CreateEntities();
            ctx.Grid = session.Grid;
            session.ApplyRows();
            session.Start();

            // A sync slice never suspends, so both sweeps finish inside this call.
            var sync = new DrydockSyncSlice(DrydockPhases.Retrieve);
            StripSliced(session.Grid, sync).GetAwaiter().GetResult();
            ResetResearchSliced(session.Grid, sync).GetAwaiter().GetResult();

            var result = session.Complete();
            Log.Info($"Drydock: {ctx.ShipId} revision {revision} loaded {result.Ids.Count} entities; "
                     + $"severed {result.Severed.Count}, manifest missing {result.Manifest.Missing.Values.Sum()} and refused {result.Manifest.Refused.Values.Sum()}, "
                     + $"appearance refused {result.AppearanceRefused.Values.Sum()}, unresolved prototypes {result.UnresolvedPrototypes.Count}, "
                     + $"dropped batches {result.DroppedBatches.Count}, "
                     + $"dropped roots [{string.Join(", ", result.DroppedRoots.Select(r => $"{r.Prototype} ({r.Subtree})"))}].");
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"Drydock: {ctx.ShipId} revision {revision} would not load: {e}");

            // Everything the load allocated, not the grid alone: an entity the rows had not reached is
            // not under it, and before the grid is parented onto the map the staging scrap below does
            // not look for either.
            session.Abandon();

            ScrapRetrieveStaging(ctx);
            return null;
        }
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
    /// Refuses the whole retrieve for content drift: one <see cref="DrydockAuditAction.DriftRefused"/>
    /// row naming the revision and what would not resolve, and the counter. The claim is released by
    /// the wrapper, as for every other refusal.
    /// </summary>
    private async Task<DrydockRetrieveOutcome> RefuseForDrift(
        DrydockRetrieveContext ctx, IDrydockSlice slice, DrydockShip ship, int revision, string reason)
    {
        DrydockMetrics.DriftRefusals.Inc();
        await slice.Await(_store.WriteAudit(new DrydockAudit
        {
            ShipGuid = ctx.ShipId,
            ShipName = ship.ShipName,
            BerthId = ship.BerthId,
            Action = DrydockAuditAction.DriftRefused,
            ActorUserId = ctx.OwnerUserId,
            Revision = revision,
            RoundId = ctx.RoundId,
            Reason = reason,
        }));

        return new DrydockRetrieveOutcome(DrydockRetrieve.Refused(DrydockRetrieveResult.ContentDrift));
    }

    /// <summary>
    /// Every prototype id that no longer resolves and every component name nothing registers, then
    /// whichever format sits outside its reader's window. Renames and deletions are left out: the load applies
    /// both (<see cref="DrydockDriftVerdict.IsRefusal"/>).
    /// </summary>
    internal static string DescribeDriftRefusal(DrydockDriftVerdict verdict)
    {
        var parts = new List<string>();

        if (verdict.Unresolved.Count > 0)
            parts.Add("unresolved prototypes " + string.Join(", ", verdict.Unresolved));

        if (verdict.MissingComponents.Count > 0)
            parts.Add("unregistered components " + string.Join(", ", verdict.MissingComponents));

        if (verdict.EngineFormatOutOfWindow)
            parts.Add($"engine format {verdict.EngineFormatVer} outside {verdict.EngineWindow.Minimum}-{verdict.EngineWindow.Maximum}");

        if (verdict.DrydockFormatOutOfWindow)
            parts.Add($"drydock format {verdict.DrydockFormatVer} outside {verdict.DrydockWindow.Minimum}-{verdict.DrydockWindow.Maximum}");

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Pins a checksum-valid revision the ladder stepped past, so ordinary stores after this retrieve
    /// cannot prune a newer state than the one it hands out. A pin that does not land is logged and
    /// never refuses the retrieve; a cancellation still travels.
    /// </summary>
    private async Task PinSteppedPast(DrydockRetrieveContext ctx, IDrydockSlice slice, int revision, string why)
    {
        try
        {
            var pinned = await slice.Await(_store.TryPinRevision(ctx.ShipId, revision, null, ctx.RoundId,
                $"a retrieve stepped past it: {why}"));

            if (pinned is not (DrydockPinResult.Success or DrydockPinResult.AlreadyInState))
                Log.Error($"Drydock: {ctx.ShipId} revision {revision} was stepped past and could not be pinned: {pinned}.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"Drydock: {ctx.ShipId} revision {revision} was stepped past and could not be pinned: {e.Message}");
        }

        GuardRetrieveResume(ctx);
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
    /// A legacy import's first map init, run on the staged hull before it is filed. The import
    /// stamps every entity map-initialized so the loader does not re-run authoring over a live
    /// ship, which also means nothing fills what the old writer never saved: an airlock's door
    /// electronics, without which the door refuses everyone. This raises map init under
    /// <see cref="DrydockMapInitMode.Import"/>, keeping those spawns so the store files them.
    ///
    /// <para>On one tick, not sliced: the hull is live and docked here, not frozen, so a system
    /// update between the snapshot and the reconcile would be reverted as if map init had done it.
    /// Honours <see cref="TriadCCVars.DrydockMapInitRefire"/>: off raises nothing, report only
    /// reports.</para>
    /// </summary>
    public DrydockMapInitReport InitializeImportedShip(EntityUid grid)
    {
        var mode = DrydockFidelitySystem.ParseMapInitMode(_cfg.GetCVar(TriadCCVars.DrydockMapInitRefire));
        if (mode == DrydockMapInitMode.Revert)
            mode = DrydockMapInitMode.Import;

        // A sync slice never suspends, so the task has already completed when it returns.
        return _fidelity.RefireMapInitSliced(grid, new DrydockSyncSlice(DrydockPhases.Retrieve), mode).GetAwaiter().GetResult();
    }

    /// <summary>
    /// What the console's retrieve does to a loaded ship after the load and before the dock, a few
    /// milliseconds of main-thread time at a time.
    ///
    /// <para>Three sweeps are stopgaps for restore handlers the image does not have yet, and run on
    /// the console's retrieve only: the research clients (H10), the console locks' grid id and the
    /// artifact analyzers' back-link (H06). Each goes with its handler. The strip rule and the
    /// research reset are not here: they run inside the load, between its start and its completion
    /// (<see cref="LoadOntoStagingMap"/>).</para>
    ///
    /// <para>The research clients register after the reset, so a lathe syncing from its server copies
    /// the empty database.</para>
    /// </summary>
    private async Task ReviveSliced(EntityUid grid, DrydockShip record, IDrydockSlice slice, DrydockPhaseTimer timer)
    {
        await ReviveResearchClientsSliced(grid, slice);
        await ReviveConsoleLocksSliced(grid, slice);
        await ReviveArtifactAnalyzersSliced(grid, slice);

        timer.Mark("sweeps");

        await ReviveBegin(grid, slice, DrydockPhase.Station, 0);

        // The row is authoritative for the name too: a rename made while the ship was stored is a
        // row update, and this is where the hull and its deed learn it. Before the station, which
        // takes its name from the grid.
        _shipyard.StampStoredName(grid, record.ShipName);
        RefreshShipOwnership(grid, record);

        // The vessel's own component grant, which the purchase applies and no load ever has. Mostly
        // a no-op on an image that came from a purchased hull, since what the shipyard granted then
        // rode the store; it is the hulls imported from a ship file that arrive without it.
        _shipyard.GrantVesselComponents(grid, ResolveVesselProto(grid, record));

        // The station itself is NOT recreated here: that waits for the dock's thaw, in the pipeline's
        // tail. Joining a station raises StationGridAddedEvent and StationPostInitEvent, whose
        // subscribers register the ship with the world: on the frozen ship they would read paused
        // entities and register the staging map (the FTL system takes it as a destination).

        timer.Mark("station");
    }

    /// <summary>
    /// Every entity on <paramref name="grid"/> carrying <typeparamref name="T"/>, materialised into
    /// a list the sweeps below can walk across ticks.
    ///
    /// <para>Snapshotted first and re-resolved per uid as the sweep consumes it, because the sweep
    /// spans ticks. The snapshot itself cannot yield part-way and is the sweeps' own un-yieldable
    /// floor, so it walks the ship's transform tree rather than every instance of the component on
    /// the server: the floor is then the size of the hull, not the size of the sector.</para>
    ///
    /// <para>The walk has no paused check, which is what the frozen ship needs; the paused-skipping
    /// query enumerator would return nothing at all here.</para>
    /// </summary>
    private List<EntityUid> SnapshotOnGrid<T>(EntityUid grid) where T : IComponent
    {
        var found = new List<EntityUid>();
        foreach (var uid in _fidelity.GridTreeList(grid))
        {
            if (HasComp<T>(uid))
                found.Add(uid);
        }

        return found;
    }

    /// <summary>
    /// The skeleton every sliced revive sweep shares: open the phase against a pre-built target
    /// list, then run <paramref name="body"/> per item ahead of <see cref="SweepStep"/>.
    ///
    /// <para>Takes a raw <see cref="EntityUid"/> rather than re-resolving a component, so a sweep
    /// whose own existence check is not a plain <see cref="TryComp{T}"/> - gravity's is a bare
    /// <see cref="Exists"/>, since it only needs the entity to still be there to raise an event -
    /// can still share this skeleton instead of hand-rolling its own loop.</para>
    /// </summary>
    private async Task SweepRaw(EntityUid grid, IDrydockSlice slice, DrydockPhase phase, List<EntityUid> targets, Action<EntityUid> body)
    {
        await ReviveBegin(grid, slice, phase, targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            body(targets[i]);
            await SweepStep(grid, slice, i);
        }
    }

    /// <summary>
    /// <see cref="SweepRaw"/> for the common case: snapshot every <typeparamref name="T"/> on the
    /// grid, then run <paramref name="body"/> on every one that still carries it, re-checked fresh
    /// per item since the sweep spans ticks.
    /// </summary>
    private Task SweepOnGrid<T>(EntityUid grid, IDrydockSlice slice, DrydockPhase phase, Action<EntityUid, T> body) where T : IComponent
    {
        return SweepRaw(grid, slice, phase, SnapshotOnGrid<T>(grid), uid =>
        {
            if (TryComp<T>(uid, out var comp))
                body(uid, comp);
        });
    }

    /// <summary>
    /// Research points and unlocked technology stay with the round, not the ship: the legacy ship
    /// save stripped them from the file, and live still does. The drydock stores the full image,
    /// so the reset happens here instead, which also covers every ship already filed with research in
    /// it. Every database aboard is reset, lathes and consoles included, since a lathe with no server
    /// keeps whatever recipes it last synced.
    /// </summary>
    private Task ResetResearchSliced(EntityUid grid, IDrydockSlice slice)
    {
        // Two independent checks, not one gating the other: a database and a server are separate
        // components and either can be present without the other.
        return SweepRaw(grid, slice, DrydockPhase.Sweeps, SnapshotOnGrid<TechnologyDatabaseComponent>(grid), uid =>
        {
            if (TryComp<TechnologyDatabaseComponent>(uid, out var database))
                _research.ResetDatabase((uid, database));

            if (TryComp<ResearchServerComponent>(uid, out var server))
                _research.ModifyServerPoints(uid, -server.Points, server);
        });
    }

    /// <summary>
    /// A research client's server link is a plain property rather than a data field, and the
    /// registration that sets it runs on map init by scanning the client's own grid for servers.
    /// This repeats that scan, which is the same shape and therefore the same result.
    /// </summary>
    private Task ReviveResearchClientsSliced(EntityUid grid, IDrydockSlice slice)
    {
        var servers = SnapshotOnGrid<ResearchServerComponent>(grid);

        // No servers aboard means nothing to register against, so the second walk of the hull's
        // tree for clients would find nothing to do with its answer.
        var clients = servers.Count == 0
            ? new List<EntityUid>()
            : SnapshotOnGrid<ResearchClientComponent>(grid);

        return SweepRaw(grid, slice, DrydockPhase.Sweeps, clients, uid =>
        {
            if (!TryComp<ResearchClientComponent>(uid, out var client))
                return;

            foreach (var serverUid in servers)
            {
                if (TryComp<ResearchServerComponent>(serverUid, out var server))
                    _research.RegisterClient(uid, serverUid, client, server);
            }
        });
    }

    /// <summary>
    /// A console lock and the grid lock beside it hold the ship's uid as a STRING, the one uid on
    /// the ship the loader cannot remap, so every locked console comes back keyed to a dead grid.
    /// The deed names the new grid and the unlock compares the two as strings, so the captain's own
    /// deed will not open the helm. Stamps every console with the live uid, as purchase and ship
    /// load both do.
    /// </summary>
    private Task ReviveConsoleLocksSliced(EntityUid grid, IDrydockSlice slice)
    {
        var shuttleId = grid.ToString();
        return SweepOnGrid<ShuttleConsoleLockComponent>(grid, slice, DrydockPhase.Sweeps,
            (uid, lockComp) => _consoleLock.SetShuttleId(uid, shuttleId, lockComp));
    }

    /// <summary>
    /// An analysis console holds its analyzer as a NetEntity, which no loader remaps, and the only
    /// thing that re-resolves it from the device-link wire is the analyzer's map init. Without this
    /// the pair comes back linked on the wire and dead on the console.
    /// </summary>
    private Task ReviveArtifactAnalyzersSliced(EntityUid grid, IDrydockSlice slice)
    {
        return SweepOnGrid<ArtifactAnalyzerComponent>(grid, slice, DrydockPhase.Sweeps,
            (uid, analyzer) => _artifactAnalyzer.RelinkConsole((uid, analyzer)));
    }

    /// <summary>
    /// The row is authoritative for ownership, and this is where the grid learns it. The
    /// ownership component rides the image, so without this a transferred ship would come back
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
    /// The vessel this hull came from, row first and grid second. The row is filled in by the store
    /// from the ship's own station, which a legacy import never had one of; the grid's
    /// <c>VesselComponent</c> is written by the purchase, is not on the ship-save exporter's strip
    /// list and is not stripped at store either, so it rides both a ship file and an image. A hull that was
    /// never bought - a mapped one, or a save old enough to predate the component - carries neither,
    /// and that is what the plain station is for.
    ///
    /// <para>The row is not healed here. The next store of this hull reads the recreated station's
    /// vessel information, or failing that this same component, and files it.</para>
    /// </summary>
    private string? ResolveVesselProto(EntityUid grid, DrydockShip record)
    {
        if (!string.IsNullOrEmpty(record.VesselProto))
            return record.VesselProto;

        return VesselIdOnGrid(grid);
    }

    /// <summary>
    /// The vessel id the purchase wrote onto the grid's own <c>VesselComponent</c>, or null when it
    /// carries none. The fallback source for both the store and the retrieve; each asks its own
    /// primary source first.
    /// </summary>
    private string? VesselIdOnGrid(EntityUid grid)
    {
        return TryComp<VesselComponent>(grid, out var vessel) && !string.IsNullOrEmpty(vessel.VesselId.Id)
            ? vessel.VesselId.Id
            : null;
    }

    /// <summary>
    /// The priority dock tag of a vessel, which steers a dock search toward the berths a purchase of
    /// it would pick. Null when there is no vessel id or it names no vessel prototype.
    /// </summary>
    private string? PriorityDockTagFor(string? vesselId)
    {
        return vesselId != null && _protoMan.TryIndex<VesselPrototype>(vesselId, out var vessel)
            ? vessel.PriorityDockTag
            : null;
    }

    /// <summary>
    /// The station a ship comes back as. It is recreated rather than restored because a station is
    /// round-scoped, and the ship's own name is passed through so a player's rename survives rather
    /// than being replaced by the prototype's name generator.
    ///
    /// <para>A hull with no recoverable vessel still gets a station, just a plain one. Coming back
    /// stationless is not a safe answer: without <c>StationMemberComponent</c> a ship is invisible
    /// to station records, expeditions, late-join spawning and everything else keyed on stations.
    /// Legacy imports are the population that lands here, because the store reads the vessel id off
    /// the ship's own station and an import is staged into the console's;
    /// <see cref="ResolveVesselProto"/> is what recovers it from the grid instead.</para>
    /// </summary>
    private void RecreateStation(EntityUid grid, DrydockShip record)
    {
        // The roundstart variation passes run on every new station and read this marker off the
        // station, which is recreated below, so a retrieved ship was re-varied on every retrieve:
        // fresh trash and spills each time, some of it inside the hull. The marker on the grid is
        // what the rule now honours, and it rides the image, so a ship is varied once at most.
        // Stamped before the vessel check: a stationless retrieve must not be varied later either.
        EnsureComp<StationVariationHasRunComponent>(grid);

        var vesselProto = ResolveVesselProto(grid, record);

        StationConfig stationConfig;
        bool known;

        if (!string.IsNullOrEmpty(vesselProto)
            && _protoMan.TryIndex<GameMapPrototype>(vesselProto, out var stationProto)
            && stationProto.Stations.TryGetValue(vesselProto, out var vesselConfig))
        {
            stationConfig = vesselConfig;
            known = true;
        }
        else
        {
            Log.Info($"Drydock: {record.ShipGuid} has no vessel prototype ('{vesselProto}'); giving it a plain station.");
            stationConfig = GenericVesselStation;
            known = false;
        }

        var station = _station.InitializeNewStation(stationConfig, new[] { grid }, Name(grid));

        // Only a ship that came from a vessel can claim to be one. A generic station keeps the
        // component the prototype gives it, with no vessel named, rather than being labelled as a
        // hull it is not. Naming it is also what lets the next store file the id back onto the row.
        if (known)
            EnsureComp<ExtraShuttleInformationComponent>(station).Vessel = vesselProto;
    }
}
