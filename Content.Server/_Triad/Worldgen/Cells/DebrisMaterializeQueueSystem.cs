// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server._NF.Worldgen.Components.Debris;
using Content.Server.Worldgen;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Components.Debris;
using Content.Server.Worldgen.Systems;
using Content.Shared._Triad.CCVar;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     The materialization queue: turns described debris records into real grids under a
///     per-tick time budget instead of building a whole cell's worth in the tick it loads.
///     Nothing else spawns worldgen debris while the sensed tier is enabled.
/// </summary>
public sealed class DebrisMaterializeQueueSystem : BaseWorldSystem
{
    [Dependency] private readonly CellDescribeSystem _describe = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly TransformSystem _xformSys = default!;

    /// <summary>
    ///     A record blocked this many times in a row stays dormant until its cell reloads.
    ///     Something durable is parked on the point; retrying it every tick is wasted work.
    /// </summary>
    private const byte MaxBlockedAttempts = 3;

    private bool _enabled;
    private float _budgetMs;
    private float _panicRange;

    private readonly List<DebrisRecord> _queue = new();
    private List<Entity<MapGridComponent>> _gridsIntersecting = new();

    public override void Initialize()
    {
        Subs.CVar(_cfg, TriadCCVars.WorldgenSensedEnabled, v => _enabled = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenMaterializeBudgetMs, v => _budgetMs = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenMaterializePanicRange, v => _panicRange = v, true);

        SubscribeLocalEvent<SensedCellComponent, WorldChunkLoadedEvent>(OnCellLoaded);
        SubscribeLocalEvent<WorldChunkComponent, WorldChunkLoadedEvent>(OnChunkLoaded);
        SubscribeLocalEvent<SensedDebrisComponent, EntityTerminatingEvent>(OnDebrisTerminating);
    }

    /// <summary>
    ///     A cell can load before the describe sweep reaches it: a small loader outruns its own
    ///     sensed radius, or a projectile loads a chunk kilometres off any ship. Describe on the
    ///     spot so records stay the only thing that decides what a cell contains.
    /// </summary>
    private void OnChunkLoaded(EntityUid uid, WorldChunkComponent component, ref WorldChunkLoadedEvent args)
    {
        if (!_enabled || HasComp<SensedCellComponent>(uid))
            return;

        if (_describe.EnsureDescribed(uid) is { } sensed)
            EnqueueCell(sensed);
    }

    private void OnCellLoaded(EntityUid uid, SensedCellComponent component, ref WorldChunkLoadedEvent args)
    {
        if (!_enabled)
            return;

        EnqueueCell(component);
    }

    private void EnqueueCell(SensedCellComponent cell)
    {
        foreach (var record in cell.Records.Values)
        {
            if (record.State != SensedState.Dormant || record.Queued)
                continue;

            record.BlockedAttempts = 0;
            record.Queued = true;
            _queue.Add(record);
        }
    }

    /// <summary>
    ///     Records whose entity died: gameplay destruction removes the record for good, while a
    ///     GC teardown on cell unload returns it to dormant so revisiting the same space finds
    ///     the same rock rather than a fresh roll.
    /// </summary>
    private void OnDebrisTerminating(EntityUid uid, SensedDebrisComponent component, ref EntityTerminatingEvent args)
    {
        if (component.Record is not { } record || record.Entity != uid)
            return;

        record.Entity = null;
        record.State = SensedState.Dormant;

        if (!HasComp<LoadedChunkComponent>(record.Cell) || Terminating(record.Cell))
            return;

        // Still loaded, so this was destroyed in play rather than collected: it is gone.
        if (TryComp<SensedCellComponent>(record.Cell, out var cell))
            cell.Records.Remove(record.Point);

        _describe.Records.Remove(record.Id);
    }

    public override void Update(float frameTime)
    {
        if (!_enabled || _queue.Count == 0)
        {
            SensedMetrics.MaterializeQueueDepth.Set(_queue.Count);
            return;
        }

        var budget = TimeSpan.FromMilliseconds(_budgetMs);
        var start = _timing.RealTime;
        var panicSq = _panicRange * _panicRange;

        SortByArrival();

        var index = 0;
        for (; index < _queue.Count; index++)
        {
            var record = _queue[index];

            // The budget is a smoothing device, not a correctness one: anything about to be
            // touched materializes now, however long the tick runs. A rock that loads late in
            // front of a ship is a rock the ship flies through.
            if (_timing.RealTime - start > budget && !WithinPanicRange(record, panicSq))
                break;

            record.Queued = false;

            if (record.State != SensedState.Dormant)
                continue;

            // The loader turned away and the cell drained; leave the record dormant.
            if (!HasComp<LoadedChunkComponent>(record.Cell))
                continue;

            Materialize(record);
        }

        if (index > 0)
        {
            _queue.RemoveRange(0, index);
            SensedMetrics.MaterializeBatch.Observe((_timing.RealTime - start).TotalSeconds);
        }

        SensedMetrics.MaterializeQueueDepth.Set(_queue.Count);
    }

    /// <summary>
    ///     Orders the queue by time to arrival rather than raw distance, so a ship at speed gets
    ///     the space ahead of it built first even when something closer sits off its beam.
    /// </summary>
    private void SortByArrival()
    {
        var loaders = new List<(Vector2 Pos, Vector2 Vel)>();
        var query = EntityQueryEnumerator<WorldLoaderComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var loader, out var xform))
        {
            if (loader.Disabled)
                continue;

            loaders.Add((_xformSys.GetWorldPosition(xform), _physics.GetMapLinearVelocity(uid, xform: xform)));
        }

        if (loaders.Count == 0)
            return;

        _queue.Sort((a, b) => ArrivalTime(a, loaders).CompareTo(ArrivalTime(b, loaders)));
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
        if (!TryComp<SensedCellComponent>(record.Cell, out var cell))
            return;

        if (!TryComp<DebrisFeaturePlacerControllerComponent>(record.Cell, out var placer))
            return;

        if (!TryComp<MapComponent>(record.Map, out var map))
            return;

        // Something moved in while this was dormant. Skip rather than spawn a rock on top of a
        // parked ship; the point stays described and retries when the cell next loads.
        if (HasCollisions(map.MapId, Box2.UnitCentered.Enlarged(placer.SafetyZoneRadius).Translated(record.Point)))
        {
            if (++record.BlockedAttempts < MaxBlockedAttempts)
            {
                record.Queued = true;
                _queue.Add(record);
            }

            return;
        }

        var overrides = new ComponentRegistry();
        var shape = new PredeterminedShapeComponent { Seed = record.Seed };
        overrides[EntityManager.ComponentFactory.GetComponentName(typeof(PredeterminedShapeComponent))] =
            new EntityPrototype.ComponentRegistryEntry(shape, new());

        var ent = EntityManager.SpawnAttachedTo(record.Proto, new EntityCoordinates(record.Map, record.Point), overrides);

        record.Entity = ent;
        record.State = SensedState.Materialized;
        record.BlockedAttempts = 0;

        var sensedDebris = EnsureComp<SensedDebrisComponent>(ent);
        sensedDebris.Record = record;

        // Hand the spawn to the upstream placer's bookkeeping so cell unload GCs it, moved
        // debris re-parents, and GC cancellation on reload all keep working untouched.
        placer.OwnedDebris[record.Point] = ent;
        var owned = EnsureComp<OwnedDebrisComponent>(ent);
        owned.OwningController = record.Cell;
        owned.LastKey = record.Point;

        EnsureComp<SpaceDebrisComponent>(ent);
    }

    private bool HasCollisions(MapId mapId, Box2 bounds)
    {
        _gridsIntersecting.Clear();
        _mapManager.FindGridsIntersecting(mapId, bounds, ref _gridsIntersecting);
        return _gridsIntersecting.Count > 0;
    }
}
