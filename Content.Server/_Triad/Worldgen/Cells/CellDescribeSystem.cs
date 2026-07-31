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
using Content.Server.Worldgen.Tools;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Worldgen;
using Content.Shared.Ghost;
using Content.Shared.Mind.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     The describe service: decides what debris exists in a cell as data, ahead of and
///     independently of building any entity. Radar contacts and the materialization queue
///     both read the records this produces, which is what keeps the shape painted at range
///     the shape of the rock that eventually loads in.
///
///     Scope: this models the shape roll. Build-time decoration that lays its own tiles is
///     not modelled, so a decorated grid can end up a few tiles larger than what was painted.
///     See <see cref="DebrisRecord"/> for why that is accepted rather than chased.
/// </summary>
public sealed class CellDescribeSystem : BaseWorldSystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly NoiseIndexSystem _noiseIndex = default!;
    [Dependency] private readonly PoissonDiskSampler _sampler = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly TransformSystem _xformSys = default!;

    private const float UpdateInterval = 1f;

    private bool _enabled;
    private float _describeRange;
    private float _describeBudgetMs;
    private float _describeLead;

    private float _accumulator;

    /// <summary>Tick the shared per-tick describe pool is being counted against, and what it has spent.</summary>
    private GameTick _inlineTick;
    private TimeSpan _inlineSpent;
    private int _nextRecordId = 1;

    /// <summary>
    ///     Cells wanted by sources but not yet described, nearest-first. Rebuilt by each 1 Hz
    ///     collect pass and drained a budget slice per tick from <see cref="_pendingHead"/> on.
    /// </summary>
    private readonly List<(float DistSq, Vector2i Coords, EntityUid Map)> _pending = new();
    private int _pendingHead;
    private readonly HashSet<(EntityUid Map, Vector2i Coords)> _pendingSet = new();

    /// <summary>Everything making space real this pass, as (map, centre, radius).</summary>
    private readonly List<(EntityUid Map, Vector2 Center, float Range)> _sources = new();
    private List<Entity<MapGridComponent>> _gridsIntersecting = new();

    /// <summary>Flat id index over every live record, for the contact channel's diffing.</summary>
    public readonly Dictionary<int, DebrisRecord> Records = new();

    /// <summary>
    ///     Rolled tile sets per record id, for data-space collision. Filled the first time a
    ///     query actually reaches a record's fine test, dropped when the record's cell retires,
    ///     so it only ever holds rocks something interacted with.
    /// </summary>
    private readonly Dictionary<int, HashSet<Vector2i>?> _tileCache = new();

    public override void Initialize()
    {
        Subs.CVar(_cfg, TriadCCVars.WorldgenSensedEnabled, OnEnabledChanged, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenDescribeRange, v => _describeRange = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenDescribeBudgetMs, v => _describeBudgetMs = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenDescribeLeadS, v => _describeLead = v, true);

        SubscribeLocalEvent<SensedCellComponent, ComponentShutdown>(OnCellShutdown);
    }

    /// <summary>
    ///     Retires every described cell when the tier is switched off, so the switch is an actual
    ///     rollback rather than a pause.
    ///
    ///     Without this the records survive the switch while the stock burst-spawn placer populates
    ///     the same cells, and switching back on re-materializes the old roll alongside the placer's
    ///     debris: not on top of it, since the spawn-site collision check holds, but roughly double
    ///     the population in every cell that was visited in between, charged to PVS and broadphase
    ///     for the rest of the round with nothing to clear it.
    ///
    ///     Dropping <see cref="SensedCellComponent"/> is the whole of it. <see cref="OnCellShutdown"/>
    ///     already drains that cell's entries out of the flat index, so there is no second
    ///     bookkeeping path to keep in step, and a cell with no component is one Describe will roll
    ///     again from scratch if the tier comes back.
    /// </summary>
    private void OnEnabledChanged(bool value)
    {
        if (value == _enabled)
            return;

        _enabled = value;

        if (value)
            return;

        var cells = new List<EntityUid>();
        var query = EntityQueryEnumerator<SensedCellComponent>();

        while (query.MoveNext(out var uid, out _))
        {
            cells.Add(uid);
        }

        // Collected first: RemComp fires OnCellShutdown, which mutates Records, and the query is
        // live over the same component set.
        foreach (var uid in cells)
        {
            RemComp<SensedCellComponent>(uid);
        }

        SensedMetrics.Records.Set(Records.Count);
    }

    private void OnCellShutdown(EntityUid uid, SensedCellComponent component, ComponentShutdown args)
    {
        foreach (var record in component.Records.Values)
        {
            Records.Remove(record.Id);
            _tileCache.Remove(record.Id);
        }
    }

    public override void Update(float frameTime)
    {
        _accumulator += frameTime;
        if (_accumulator >= UpdateInterval)
        {
            _accumulator -= UpdateInterval;

            // Counted above the enabled gate, deliberately. This gauge exists to compare the sensed
            // tier against the stock burst-spawn placer, so it has to keep reporting when the tier is
            // switched off; a metric that goes dark in one arm of the comparison cannot make it.
            SensedMetrics.ResidentDebris.Set(CountResidentDebris());

            if (_enabled)
                CollectPending();
        }

        if (!_enabled)
            return;

        // Every tick, not just on the 1 Hz collect: the drain is budget-capped per tick either
        // way, so ticking it thirty times a second is what sets the fill rate, not the hitch.
        DrainPending();

        SensedMetrics.Records.Set(Records.Count);
    }

    /// <summary>
    ///     Live debris grids from either spawn path. Both paths carry
    ///     <see cref="SpaceDebrisComponent"/>: the stock placer from the prototype, and the
    ///     materialize queue by explicit EnsureComp, precisely so the two are comparable.
    /// </summary>
    private int CountResidentDebris()
    {
        var n = 0;
        var query = EntityQueryEnumerator<SpaceDebrisComponent>();
        while (query.MoveNext(out _))
            n++;

        return n;
    }

    /// <summary>
    ///     Gathers undescribed cells around everything present in space, ordered nearest-first
    ///     so the picture fills outward from people rather than in map order.
    /// </summary>
    private void CollectPending()
    {
        _pending.Clear();
        _pendingHead = 0;
        _pendingSet.Clear();
        _sources.Clear();

        CollectSources();

        var controllerQuery = GetEntityQuery<WorldControllerComponent>();
        var sensedQuery = GetEntityQuery<SensedCellComponent>();

        // Budgeted for the same reason the drain is. The scan is O(sources * (range/ChunkSize)^2),
        // which at shipping defaults is roughly 2600 candidate coords per source with no cap on
        // source count, and it lands as a single lump on whichever tick the 1 Hz accumulator fires.
        // The drain being hard-capped while the scan that feeds it was not left the cvar unable to
        // bound the pass it names.
        var budget = TimeSpan.FromMilliseconds(_describeBudgetMs);
        var start = _timing.RealTime;
        var truncated = false;

        foreach (var (map, center, range) in _sources)
        {
            if (truncated)
                break;

            if (!controllerQuery.TryComp(map, out var controller))
                continue;

            var radius = (int) MathF.Ceiling(range / WorldGen.ChunkSize) + 1;
            var chunks = new GridPointsNearEnumerator(WorldGen.WorldToChunkCoords(center).Floored(), radius);

            while (chunks.MoveNext(out var chunk))
            {
                // Truncating biases what got collected toward the sources scanned first, so the
                // nearest-first sort below is only nearest-first among what was reached. That is
                // the right trade for a valve that should almost never trip: the cells missed are
                // still undescribed next second and get collected then.
                if (_timing.RealTime - start > budget)
                {
                    truncated = true;
                    break;
                }

                var coords = chunk.Value;

                // Already-described cells are the overwhelming majority of any steady-state sweep,
                // and rejecting them here rather than in the drain keeps both the pending list and
                // the sort over it proportional to the work actually left instead of to the total
                // area ever described. A coord with no chunk entity yet has necessarily never been
                // described, so it stays pending.
                if (controller.Chunks.TryGetValue(coords, out var existing) && sensedQuery.HasComp(existing))
                    continue;

                if (!_pendingSet.Add((map, coords)))
                    continue;

                var distSq = (WorldGen.ChunkToWorldCoordsCentered(coords) - center).LengthSquared();
                _pending.Add((distSq, coords, map));
            }
        }

        if (_pending.Count > 1)
            _pending.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));
    }

    /// <summary>
    ///     Everything that makes the space around it real, all at the same reach. Presence decides
    ///     what exists: a ship, a person, or a station anchors the world identically, and debris is
    ///     there whether or not anything is pointed at it. No sensor is consulted here. A radar
    ///     decides what a console is *told* about (see <see cref="SensedContactsSystem"/>); it never
    ///     decides what is out there, or a drifting ghost would be minting asteroids.
    /// </summary>
    private void CollectSources()
    {
        // Ships. A loader is bolted wherever its console sits, so reach is measured from the hull's
        // farthest corner; otherwise the far end of a kilometre-long capital outruns its own
        // describe radius.
        var loaders = EntityQueryEnumerator<WorldLoaderComponent, TransformComponent>();
        while (loaders.MoveNext(out var uid, out var loader, out var xform))
        {
            if (loader.Disabled)
                continue;

            AddSource(uid, xform, _describeRange + GetLoaderExtent(uid, xform));
        }

        // People. A miner out on a jetpack anchors the world exactly as a ship does. The extent
        // term folds to zero off-grid, so this is genuinely the same formula rather than a weaker
        // special case, and a crewman standing on a hull simply dedupes into their own ship's halo.
        var ghostQuery = GetEntityQuery<GhostComponent>();
        var minds = EntityQueryEnumerator<MindContainerComponent, TransformComponent>();
        while (minds.MoveNext(out var uid, out var mind, out var xform))
        {
            if (!mind.HasMind || ghostQuery.HasComp(uid))
                continue;

            AddSource(uid, xform, _describeRange + GetLoaderExtent(uid, xform));
        }

        // Stations and points of interest, which hold their surroundings real without crewing a
        // chunk loader. Carrying its own range lets a large POI reach further than a shuttle.
        var anchors = EntityQueryEnumerator<DescribeAnchorComponent, TransformComponent>();
        while (anchors.MoveNext(out var uid, out var anchor, out var xform))
        {
            AddSource(uid, xform, (anchor.Range ?? _describeRange) + GetLoaderExtent(uid, xform));
        }
    }

    private void AddSource(EntityUid uid, TransformComponent xform, float range)
    {
        if (xform.MapUid is not { } map)
            return;

        var center = _xformSys.GetWorldPosition(xform);
        center += _physics.GetMapLinearVelocity(uid, xform: xform) * _describeLead;

        _sources.Add((map, center, range));
    }

    /// <summary>
    ///     Works the pending list down a budget slice per tick, charged against the same per-tick
    ///     pool as <see cref="EnsureDescribed"/>, capping describe work across both paths. The cap
    ///     is soft by one cell: both paths check the pool before a describe rather than after, so a
    ///     tick can run over by at most the single describe that drains it. Ticking the slice
    ///     rather than running it once per
    ///     collect pass is what sets the fill rate: the opening picture around a spawn is roughly
    ///     2600 cells at shipping defaults, and a once-a-second slice took minutes to paint what a
    ///     slice a tick paints in seconds, at the identical worst case per tick.
    /// </summary>
    private void DrainPending()
    {
        if (_pendingHead >= _pending.Count)
            return;

        RollInlineBudget();

        var budget = TimeSpan.FromMilliseconds(_describeBudgetMs);
        var sliceStart = _timing.RealTime;
        var described = 0;

        while (_pendingHead < _pending.Count)
        {
            if (_inlineSpent > budget)
                break;

            var (_, coords, map) = _pending[_pendingHead++];
            var start = _timing.RealTime;

            // CollectPending already dropped described cells, but this list can be most of a second
            // old: EnsureDescribed or an earlier slice may have got here first. GetOrCreateChunk can
            // also mint the chunk entity for a coord that had none at collection time.
            var cell = GetOrCreateChunk(coords, map);
            if (cell is not null && !HasComp<SensedCellComponent>(cell.Value))
            {
                Describe(cell.Value);
                described++;
            }

            _inlineSpent += _timing.RealTime - start;
        }

        if (described > 0)
        {
            SensedMetrics.CellsDescribed.Inc(described);
            SensedMetrics.DescribePass.Observe((_timing.RealTime - sliceStart).TotalSeconds);
        }
    }

    /// <summary>
    ///     Describes a cell if it has not been described yet. Callers that need a cell's
    ///     contents right now (the materialization queue on a chunk load that outran the
    ///     sweep) use this instead of waiting for the next pass.
    ///
    ///     Budgeted per tick, because the number of callers in one tick is set by how many chunks
    ///     entered load range together: a fast ship, several loaders, or players spread across a
    ///     sector can stack whole describes into one tick, which is the exact hitch the budget
    ///     exists to prevent. Returning null past the budget is not a failure: the cell keeps no
    ///     <see cref="SensedCellComponent"/>, so the next sweep collects it like any other
    ///     undescribed cell, a second at most later.
    ///
    ///     The pool is shared with <see cref="DrainPending"/>: demand describes and the sweep's
    ///     per-tick slice draw down the same describe_budget_ms, so the cap on describe work in
    ///     any single tick holds no matter how the two paths interleave.
    /// </summary>
    public SensedCellComponent? EnsureDescribed(EntityUid cell)
    {
        if (TryComp<SensedCellComponent>(cell, out var existing))
            return existing;

        RollInlineBudget();

        if (_inlineSpent > TimeSpan.FromMilliseconds(_describeBudgetMs))
            return null;

        var start = _timing.RealTime;
        var sensed = Describe(cell);
        _inlineSpent += _timing.RealTime - start;

        return sensed;
    }

    /// <summary>Resets the shared per-tick describe pool when the tick has advanced.</summary>
    private void RollInlineBudget()
    {
        if (_inlineTick == _timing.CurTick)
            return;

        _inlineTick = _timing.CurTick;
        _inlineSpent = TimeSpan.Zero;
    }

    /// <summary>
    ///     Rolls a cell's contents. This mirrors <see cref="DebrisFeaturePlacerSystem.OnChunkLoaded"/>
    ///     point-for-point, minus the spawn: same density channel, same Poisson sampling, same
    ///     clip/cancel rolls, same carver and selector events, so gating the placer changes what
    ///     builds entities without changing what the belt contains.
    /// </summary>
    private SensedCellComponent? Describe(EntityUid cell)
    {
        if (!TryComp<WorldChunkComponent>(cell, out var chunk))
            return null;

        var map = chunk.Map;
        if (!TryComp<MapComponent>(map, out var mapComp))
            return null;

        var sensed = AddComp<SensedCellComponent>(cell);

        // No placer on this chunk means the biome puts no debris here: described and empty.
        if (!TryComp<DebrisFeaturePlacerControllerComponent>(cell, out var placer))
            return sensed;

        var densityChannel = placer.DensityNoiseChannel;
        var density = _noiseIndex.Evaluate(cell, densityChannel, chunk.Coordinates + new Vector2(0.5f, 0.5f));
        if (density == 0)
            return sensed;

        var points = GeneratePointsInCell(density, chunk.Coordinates);
        var safetyBounds = Box2.UnitCentered.Enlarged(placer.SafetyZoneRadius);

        foreach (var point in points)
        {
            var pointDensity = _noiseIndex.Evaluate(cell, densityChannel, WorldGen.WorldToChunkCoords(point));
            if (pointDensity == 0 && placer.DensityClip || _random.Prob(placer.RandomCancellationChance))
                continue;

            if (HasCollisions(mapComp.MapId, safetyBounds.Translated(point)))
                continue;

            var coords = new EntityCoordinates(map, point);

            var preEv = new PrePlaceDebrisFeatureEvent(coords, cell);
            RaiseLocalEvent(cell, ref preEv);
            if (preEv.Handled)
                continue;

            var debrisEv = new TryGetPlaceableDebrisFeatureEvent(coords, cell);
            RaiseLocalEvent(cell, ref debrisEv);
            if (debrisEv.DebrisProto is not { } proto)
                continue;

            var record = new DebrisRecord
            {
                Id = _nextRecordId++,
                Cell = cell,
                Map = map,
                Point = point,
                Proto = proto,
                Seed = _random.Next(),
            };

            FillShape(record);
            sensed.Records[point] = record;
            Records[record.Id] = record;
        }

        return sensed;
    }

    /// <summary>
    ///     Rolls the record's shape once, to mark it shaped and size its collision bound. No
    ///     outline is kept server-side: the client re-rolls from (Proto, Seed) through the
    ///     shared generator, and the contact channel ships the recipe rather than geometry.
    /// </summary>
    private void FillShape(DebrisRecord record)
    {
        if (!_proto.TryIndex<EntityPrototype>(record.Proto, out var proto))
            return;

        if (DebrisRecipe.TryFrom(proto) is not { } recipe)
            return;

        var tiles = BlobShapeGen.Roll(new System.Random(record.Seed), recipe);

        if (tiles.Count == 0)
            return;

        record.Shaped = true;

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        foreach (var tile in tiles)
        {
            minX = Math.Min(minX, tile.Pos.X);
            minY = Math.Min(minY, tile.Pos.Y);
            maxX = Math.Max(maxX, tile.Pos.X);
            maxY = Math.Max(maxY, tile.Pos.Y);
        }

        // Coarse collision gate: the farthest any tile corner sits from the blob origin. Corners
        // reach one past the largest tile origin on each axis.
        var boundX = MathF.Max(MathF.Abs(minX), MathF.Abs(maxX + 1));
        var boundY = MathF.Max(MathF.Abs(minY), MathF.Abs(maxY + 1));
        record.Bound = MathF.Sqrt(boundX * boundX + boundY * boundY);
    }

    private List<Vector2> GeneratePointsInCell(float density, Vector2 coords)
    {
        var offs = (int) ((WorldGen.ChunkSize - WorldGen.ChunkSize / 8.0f) / 2.0f);
        var enumerator = _sampler.SampleRectangle(new Vector2(-offs, -offs), new Vector2(offs, offs), density);
        var points = new List<Vector2>();
        var realCenter = WorldGen.ChunkToWorldCoordsCentered(coords.Floored());

        while (enumerator.MoveNext(out var point))
        {
            points.Add(realCenter + point.Value);
        }

        return points;
    }

    /// <summary>
    ///     Whether any grid overlaps the box. The tier's one world-collision probe, shared with
    ///     the materialization queue's spawn-safety check; game thread only, it reuses one
    ///     scratch list.
    /// </summary>
    public bool HasCollisions(MapId mapId, Box2 bounds)
    {
        _gridsIntersecting.Clear();
        _mapManager.FindGridsIntersecting(mapId, bounds, ref _gridsIntersecting);
        return _gridsIntersecting.Count > 0;
    }

    /// <summary>
    ///     Resolves a world-space segment against dormant debris in data space: the first shaped,
    ///     dormant, non-ghost record whose rolled tile set the segment enters. This is how a shot
    ///     stops on a rock that radar promised was there while no entity for it exists.
    ///
    ///     Cost discipline: the cells are the spatial index. Only cells overlapping the segment's
    ///     bounding box (plus one cell of slack, which exceeds any shipping rock's
    ///     <see cref="DebrisRecord.Bound"/>) are consulted, each record passes a coarse
    ///     point-to-segment gate before its tile set is rolled, and rolled sets are cached per
    ///     record. A per-tick projectile segment touches one or two cells holding a handful of
    ///     records; the flat <see cref="Records"/> index is never walked.
    /// </summary>
    public bool TryQuerySegment(EntityUid map, Vector2 start, Vector2 end,
        out DebrisRecord? hit, out Vector2 hitPoint)
    {
        hit = null;
        hitPoint = default;

        if (!_enabled)
            return false;

        var delta = end - start;
        var lengthSq = delta.LengthSquared();
        var bestT = float.MaxValue;
        DebrisRecord? best = null;

        void TestRecord(DebrisRecord record)
        {
            if (!record.Shaped || record.State != SensedState.Dormant)
                return;

            // A ghost rock is filtered off radar, so it must not stop shots either: blocking
            // on something the players cannot see is an invisible wall.
            if (record.GaveUp)
                return;

            // Coarse gate: closest distance from the record centre to the segment.
            var t = lengthSq > 0f ? Math.Clamp(Vector2.Dot(record.Point - start, delta) / lengthSq, 0f, 1f) : 0f;
            var closest = start + delta * t;
            if ((record.Point - closest).LengthSquared() > record.Bound * record.Bound)
                return;

            if (GetTiles(record) is not { } tiles)
                return;

            if (!TileWalk.TraceSegment(start - record.Point, end - record.Point, tiles,
                    out _, out var hitT) || hitT >= bestT)
                return;

            bestT = hitT;
            best = record;
        }

        if (TryComp<WorldControllerComponent>(map, out var controller))
        {
            var sensedQuery = GetEntityQuery<SensedCellComponent>();

            var box = new Box2(Vector2.Min(start, end), Vector2.Max(start, end)).Enlarged(WorldGen.ChunkSize);
            var min = WorldGen.WorldToChunkCoords(box.BottomLeft).Floored();
            var max = WorldGen.WorldToChunkCoords(box.TopRight).Floored();

            for (var cx = min.X; cx <= max.X; cx++)
            {
                for (var cy = min.Y; cy <= max.Y; cy++)
                {
                    if (!controller.Chunks.TryGetValue(new Vector2i(cx, cy), out var cell)
                        || !sensedQuery.TryComp(cell, out var sensed))
                        continue;

                    foreach (var record in sensed.Records.Values)
                    {
                        TestRecord(record);
                    }
                }
            }
        }
        else
        {
            // No world controller means no cell index; fall back to the flat record index
            // filtered by map. Only non-worldgen maps land here, and they hold few or no
            // records, so O(records) is fine where it matters and free where it does not.
            foreach (var record in Records.Values)
            {
                if (record.Map == map)
                    TestRecord(record);
            }
        }

        if (best is null)
            return false;

        hit = best;
        hitPoint = start + delta * bestT;
        return true;
    }

    /// <summary>
    ///     The record's rolled tile set, cached. Null for records whose prototype lost its blob
    ///     builder between describe and query, which shipping content never does.
    /// </summary>
    private HashSet<Vector2i>? GetTiles(DebrisRecord record)
    {
        if (_tileCache.TryGetValue(record.Id, out var cached))
            return cached;

        HashSet<Vector2i>? tiles = null;
        if (_proto.TryIndex<EntityPrototype>(record.Proto, out var proto)
            && DebrisRecipe.TryFrom(proto) is { } recipe)
        {
            var rolled = BlobShapeGen.Roll(new System.Random(record.Seed), recipe);

            tiles = new HashSet<Vector2i>(rolled.Count);
            foreach (var tile in rolled)
            {
                tiles.Add(tile.Pos);
            }
        }

        _tileCache[record.Id] = tiles;
        return tiles;
    }

    /// <summary>
    ///     Distance from a loader to the farthest corner of the grid it rides, so sensing and
    ///     loading are measured from the hull edge rather than a point somewhere inside it.
    /// </summary>
    public float GetLoaderExtent(EntityUid loader, TransformComponent xform)
    {
        if (xform.GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
            return 0f;

        var local = Vector2.Transform(_xformSys.GetWorldPosition(xform), _xformSys.GetInvWorldMatrix(grid));
        var aabb = gridComp.LocalAABB;

        return MathF.Max(
            (aabb.BottomLeft - local).Length(),
            MathF.Max(
                (aabb.TopRight - local).Length(),
                MathF.Max((aabb.TopLeft - local).Length(), (aabb.BottomRight - local).Length())));
    }
}
