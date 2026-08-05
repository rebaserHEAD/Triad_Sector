// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server._NF.Worldgen.Components.Debris;
using Content.Server.Worldgen;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Components.Debris;
using Content.Server.Worldgen.Systems;
using Content.Server.Worldgen.Systems.Debris;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Worldgen;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Profiling;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     The materialization queue: turns described debris records into real grids under a
///     per-tick time budget instead of building a whole cell's worth in the tick it loads.
///     Nothing else spawns worldgen debris while records are enabled.
/// </summary>
public sealed class DebrisMaterializeQueueSystem : BaseWorldSystem
{
    [Dependency] private readonly CellDescribeSystem _describe = default!;
    [Dependency] private readonly DebrisCaptureSystem _capture = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly TransformSystem _xformSys = default!;

    // Not readonly: engine convention for ProfManager injection, and RA0051 flags readonly
    // [Dependency] fields.
    [Dependency] private ProfManager _prof = default!;

    /// <summary>
    ///     Seconds between re-orderings of the queue. Sorting every tick charges an
    ///     O(n * loaders) key sweep to every frame, and the order barely ages in between: a loader
    ///     at 50 m/s moves about twelve metres per pass against a panic reach measured in
    ///     hundreds.
    ///     Ordering is not merely cosmetic, though. The drain loop breaks at the first record that
    ///     is over budget and outside panic range, so the panic escape only ever tests records the
    ///     loop actually reaches. The sort is what keeps urgent records at the head where that
    ///     escape can see them.
    /// </summary>
    private const float SortInterval = 0.25f;

    private bool _enabled;
    private float _budgetMs;
    private float _panicRange;

    private float _sortAccumulator;

    private readonly List<DebrisRecord> _queue = new();

    /// <summary>
    ///     Loader pose snapshot, rebuilt per ordering pass. Only <see cref="SortByArrival"/> may
    ///     read this: it is up to <see cref="SortInterval"/> stale, so no correctness path can use
    ///     it. Live checks such as <see cref="WithinPanicRange"/> re-query the world themselves.
    /// </summary>
    private readonly List<(Vector2 Pos, Vector2 Vel)> _loaders = new();

    public override void Initialize()
    {
        Subs.CVar(_cfg, TriadCCVars.WorldgenRecordsEnabled, OnEnabledChanged, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenMaterializeBudgetMs, v => _budgetMs = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenMaterializePanicRange, v => _panicRange = v, true);

        SubscribeLocalEvent<CellRecordsComponent, WorldChunkLoadedEvent>(OnCellLoaded);
        SubscribeLocalEvent<WorldChunkComponent, WorldChunkLoadedEvent>(OnChunkLoaded);
        SubscribeLocalEvent<RecordedDebrisComponent, EntityTerminatingEvent>(OnDebrisTerminating);

        // After the placer's own move handler, which re-parents OwnedDebris to the chunk the
        // grid actually sits in; this reads its answer rather than recomputing it.
        SubscribeLocalEvent<RecordedDebrisComponent, MoveEvent>(OnDebrisMoved,
            after: [typeof(DebrisFeaturePlacerSystem)]);
    }

    /// <summary>
    ///     Drops the pending queue when the tier is switched off. Update returns early while
    ///     disabled but does NOT clear the queue, so without this the records sitting in it drain on
    ///     the first tick after the switch comes back on, building the records system's roll on top of
    ///     whatever the stock placer put there in the meantime. Records are retired separately by
    ///     <see cref="CellDescribeSystem"/>; this only releases the queue's hold on them, which is
    ///     why Queued is cleared rather than left for a drain that will never come.
    /// </summary>
    private void OnEnabledChanged(bool value)
    {
        if (value == _enabled)
            return;

        _enabled = value;

        if (value)
            return;

        foreach (var record in _queue)
        {
            record.Queued = false;
        }

        _queue.Clear();
        RecordMetrics.MaterializeQueueDepth.Set(0);
    }

    /// <summary>
    ///     A cell can load before the describe sweep reaches it: a small loader outruns its own
    ///     records radius, or a projectile loads a chunk kilometres off any ship. Describe on the
    ///     spot so records stay the only thing that decides what a cell contains.
    /// </summary>
    private void OnChunkLoaded(EntityUid uid, WorldChunkComponent component, ref WorldChunkLoadedEvent args)
    {
        if (!_enabled || HasComp<CellRecordsComponent>(uid))
            return;

        if (_describe.EnsureDescribed(uid) is { } records)
            EnqueueCell(records);
    }

    private void OnCellLoaded(EntityUid uid, CellRecordsComponent component, ref WorldChunkLoadedEvent args)
    {
        if (!_enabled)
            return;

        EnqueueCell(component);
    }

    private void EnqueueCell(CellRecordsComponent cell)
    {
        foreach (var record in cell.Records.Values)
        {
            if (record.State != RecordState.Dormant || record.Queued)
                continue;

            record.BlockedAttempts = 0;
            record.Queued = true;
            _queue.Add(record);

            // Arm the next pass to re-order. New records land on the tail with no arrival key, so
            // a genuinely urgent one would otherwise sit behind the whole queue, out of reach of
            // the drain loop's panic escape, until the interval elapsed. Cell loads are rare
            // enough that this does not put the sort back on the tick.
            _sortAccumulator = SortInterval;
        }
    }

    /// <summary>
    ///     A data-space hit confirmed this rock matters right now: build it, if its cell is
    ///     loaded. A hit in an unloaded cell leaves the record dormant on purpose. The shot
    ///     stopping is the promise; the grid is a consequence for whoever is close enough to see
    ///     it, and a grid built in an unloaded cell would only be swept straight back out by the
    ///     drain loop's own loaded-cell guard anyway.
    /// </summary>
    public void ExpediteHit(DebrisRecord record)
    {
        if (record.State != RecordState.Dormant || record.Queued)
            return;

        if (!HasComp<LoadedChunkComponent>(record.Cell))
            return;

        record.BlockedAttempts = 0;
        record.Queued = true;
        _queue.Add(record);
        _sortAccumulator = SortInterval;
    }

    /// <summary>
    ///     Records whose entity died: gameplay destruction removes the record for good, while a
    ///     GC teardown on cell unload returns it to dormant so revisiting the same space finds
    ///     the same rock rather than a fresh roll. Same point, prototype and seed, so shape,
    ///     interior and deposits all rebuild identically; decoration that does not read the seed,
    ///     chiefly RoomFill, re-rolls.
    /// </summary>
    private void OnDebrisTerminating(EntityUid uid, RecordedDebrisComponent component, ref EntityTerminatingEvent args)
    {
        if (component.Record is not { } record || record.Entity != uid)
            return;

        record.Entity = null;
        record.State = RecordState.Dormant;

        // Presence of the component is not enough to say the cell is still loaded. The world
        // controller unloads with RemCompDeferred, which runs the component's shutdown immediately
        // but leaves it visible to HasComp until the end-of-tick cull. A debris grid deleted inside
        // that window (the GC queue's overflow branch does exactly this) would read as "cell still
        // loaded", take the destroyed-in-play branch, and delete a record that should have gone
        // dormant and come back. Running goes false the moment the shutdown runs, so it closes the
        // window without needing to know who did the deleting.
        if (!TryComp<LoadedChunkComponent>(record.Cell, out var loaded) || !loaded.Running || Terminating(record.Cell))
            return;

        // Still loaded, so this was destroyed in play rather than collected: it is gone,
        // and any captured blob goes with it. Destroyed rocks stay destroyed.
        if (TryComp<CellRecordsComponent>(record.Cell, out var cell))
            cell.Records.Remove(record.Point);

        _describe.Records.Remove(record.Id);
        _capture.Drop(record.Id);
    }

    /// <summary>
    ///     Keeps a record filed under the cell its grid actually occupies. A rammed or towed
    ///     rock that drifts across a chunk boundary otherwise leaves its record pointing at the
    ///     old cell forever, and every load-state decision made against that stale cell goes
    ///     wrong at once: <see cref="OnDebrisTerminating"/> misreads a destroyed-in-play kill as
    ///     a routine unload (and later resurrects the dead rock from its blob), and the old
    ///     cell's unload captures a grid the new cell still has in play.
    /// </summary>
    private void OnDebrisMoved(EntityUid uid, RecordedDebrisComponent component, ref MoveEvent args)
    {
        if (!_enabled || component.Record is not { } record || record.Entity != uid)
            return;

        if (!TryComp<OwnedDebrisComponent>(uid, out var owned))
            return;

        var newCell = owned.OwningController;
        if (newCell == record.Cell || !Exists(newCell))
            return;

        // Describe the destination if the sweep never reached it: filing a record into a bare
        // chunk would make it read as described-and-nearly-empty, hiding whatever the roll
        // would have put there.
        if (_describe.EnsureDescribed(newCell) is not { } destination)
            return;

        if (!destination.Records.TryAdd(record.Point, record))
        {
            // Two records on the same point key cannot happen between honest describe rolls;
            // leave the stale filing rather than orphan whoever is already there.
            Log.Error($"Record {record.Id} could not migrate to cell {ToPrettyString(newCell)}: point key collision.");
            return;
        }

        if (TryComp<CellRecordsComponent>(record.Cell, out var oldCell))
            oldCell.Records.Remove(record.Point);

        record.Cell = newCell;
    }

    public override void Update(float frameTime)
    {
        if (!_enabled || _queue.Count == 0)
        {
            // Arm the next pass to sort. A queue refilling from empty is in insertion order, and
            // the accumulator does not advance while we are returning early here.
            _sortAccumulator = SortInterval;
            RecordMetrics.MaterializeQueueDepth.Set(_queue.Count);
            return;
        }

        // Opened past the early return so a settled queue does not stamp an empty zone on every
        // tick of the capture; what we are hunting here is the burst, not the idle.
        using var zone = _prof.Group("worldgen.materialize", WorldgenProfiling.ZoneColor);

        var budget = TimeSpan.FromMilliseconds(_budgetMs);
        var panicSq = _panicRange * _panicRange;

        _sortAccumulator += frameTime;
        if (_sortAccumulator >= SortInterval)
        {
            _sortAccumulator -= SortInterval;

            // Its own zone because the sort is deliberately excluded from the spawn budget below.
            // If it ever grows back into the frame, that has to be visible as its own bar rather
            // than smeared across the batch it is not charged to.
            using var _sort = _prof.Group("worldgen.materialize.sort");
            SortByArrival();
        }

        // Taken after the sort, not before. The budget bounds spawn work; charging the ordering
        // pass to it is what let a deep queue eat its own budget, break before building anything,
        // and come back next tick with a longer queue and a slower sort.
        var start = _timing.RealTime;

        // Snapshot the length before walking it. Materialize re-queues a blocked record onto the
        // tail of this same list, so a live bound walks straight into the retry and burns all
        // MaxBlockedAttempts, and all their broadphase queries, inside one tick. Re-queued entries
        // sit past this bound and get their next attempt on the next pass, one per pass.
        var count = _queue.Count;

        var built = 0;
        var index = 0;
        for (; index < count; index++)
        {
            var record = _queue[index];

            // The budget is a smoothing device, not a correctness one: anything about to be
            // touched materializes now, however long the tick runs. A rock that loads late in
            // front of a ship is a rock the ship flies through.
            if (_timing.RealTime - start > budget && !WithinPanicRange(record, panicSq))
                break;

            record.Queued = false;

            if (record.State != RecordState.Dormant)
                continue;

            // The loader turned away and the cell drained; leave the record dormant.
            if (!HasComp<LoadedChunkComponent>(record.Cell))
                continue;

            Materialize(record);
            built++;
        }

        if (index > 0)
        {
            _queue.RemoveRange(0, index);
            RecordMetrics.MaterializeBatch.Observe((_timing.RealTime - start).TotalSeconds);
        }

        RecordMetrics.MaterializeQueueDepth.Set(_queue.Count);

        // Guarded on the flag rather than leaning on EmitText's null check: the interpolated
        // string would still be built every tick otherwise, Tracy running or not.
        if (_prof.IsTracyEnabled)
            zone.EmitText($"built={built} walked={index} remaining={_queue.Count}");
    }

    /// <summary>
    ///     Orders the queue by time to arrival rather than raw distance, so a ship at speed gets
    ///     the space ahead of it built first even when something closer sits off its beam. Runs on
    ///     <see cref="SortInterval"/>, not every tick.
    ///     The key is computed once per record and parked on it. A comparator that recomputed
    ///     arrival for both operands made the pass O(n log n * loaders) square roots: a 2000-deep
    ///     queue against a 30-loader fleet is over a million of them, in the very tick where the
    ///     queue is already deep enough to be in trouble.
    /// </summary>
    private void SortByArrival()
    {
        if (_queue.Count < 2)
            return;

        _loaders.Clear();
        var query = EntityQueryEnumerator<WorldLoaderComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var loader, out var xform))
        {
            if (loader.Disabled)
                continue;

            _loaders.Add((_xformSys.GetWorldPosition(xform), _physics.GetMapLinearVelocity(uid, xform: xform)));
        }

        if (_loaders.Count == 0)
            return;

        foreach (var record in _queue)
        {
            record.ArrivalKey = ArrivalTime(record, _loaders);
        }

        // Static, so the delegate is compiler-cached rather than allocated per pass; the old
        // comparator captured the loader list and minted a closure every tick.
        _queue.Sort(static (a, b) => a.ArrivalKey.CompareTo(b.ArrivalKey));
    }

    private static float ArrivalTime(DebrisRecord record, List<(Vector2 Pos, Vector2 Vel)> loaders)
    {
        var best = float.MaxValue;

        foreach (var (pos, vel) in loaders)
        {
            var offset = record.Point - pos;
            var dist = offset.Length();

            // Closing speed along the line to the record; away-facing loaders score their
            // plain distance rather than a negative (and therefore winning) time.
            var closing = dist > 0.01f ? Vector2.Dot(vel, offset / dist) : 0f;
            var time = closing > 1f ? dist / closing : dist;

            best = MathF.Min(best, time);
        }

        return best;
    }

    private bool WithinPanicRange(DebrisRecord record, float panicSq)
    {
        var query = EntityQueryEnumerator<WorldLoaderComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var loader, out var xform))
        {
            if (loader.Disabled)
                continue;

            var extent = _describe.GetLoaderExtent(uid, xform);
            var reach = MathF.Sqrt(panicSq) + extent;

            if ((_xformSys.GetWorldPosition(xform) - record.Point).LengthSquared() <= reach * reach)
                return true;
        }

        return false;
    }

    private void Materialize(DebrisRecord record)
    {
        if (!TryComp<CellRecordsComponent>(record.Cell, out var cell))
            return;

        if (!TryComp<DebrisFeaturePlacerControllerComponent>(record.Cell, out var placer))
            return;

        if (!TryComp<MapComponent>(record.Map, out var map))
            return;

        // Something moved in while this was dormant. Skip rather than spawn a rock on top of a
        // parked ship; the point stays described and retries when the cell next loads.
        if (_describe.HasCollisions(map.MapId, Box2.UnitCentered.Enlarged(placer.SafetyZoneRadius).Translated(record.Point)))
        {
            if (++record.BlockedAttempts < DebrisRecord.MaxBlockedAttempts)
            {
                record.Queued = true;
                _queue.Add(record);
            }

            return;
        }

        EntityUid ent;
        if (record.Captured && _capture.TryRestore(record, map.MapId, out var restored))
        {
            // The blob is the rock: whatever was mined, looted, built or left aboard comes back
            // exactly as captured, already map-initialized, so no spawner or fill runs again.
            ent = restored;
        }
        else
        {
            // A captured record landing here lost its blob (restore failure, store eviction
            // during a session the record outlived). Falling back to the seed roll regenerates
            // whatever was taken, which is the accepted failure valve; reverting first keeps
            // the painted shape the shape that spawns.
            if (record.Captured)
            {
                Log.Error($"Captured record {record.Id} has no usable blob; reverting to the pristine roll.");
                _capture.Drop(record.Id);
                _describe.RevertCapture(record);
            }

            var overrides = new ComponentRegistry();
            var shape = new PredeterminedShapeComponent { Seed = record.Seed };
            overrides[EntityManager.ComponentFactory.GetComponentName(typeof(PredeterminedShapeComponent))] =
                new EntityPrototype.ComponentRegistryEntry(shape);

            ent = EntityManager.SpawnAttachedTo(record.Proto, new EntityCoordinates(record.Map, record.Point), overrides);
        }

        record.Entity = ent;
        record.State = RecordState.Materialized;
        record.BlockedAttempts = 0;

        var recordedDebris = EnsureComp<RecordedDebrisComponent>(ent);
        recordedDebris.Record = record;

        // Hand the spawn to the upstream placer's bookkeeping so cell unload GCs it, moved
        // debris re-parents, and GC cancellation on reload all keep working untouched.
        placer.OwnedDebris[record.Point] = ent;
        var owned = EnsureComp<OwnedDebrisComponent>(ent);
        owned.OwningController = record.Cell;
        owned.LastKey = record.Point;

        EnsureComp<SpaceDebrisComponent>(ent);
    }
}
