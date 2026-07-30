using System.Linq;
using System.Numerics;
using Content.Server.Worldgen;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Components.Debris;
using Content.Server.Worldgen.Systems;
using Content.Server.Worldgen.Systems.Debris;
using Content.Server.Worldgen.Tools;
using Content.Shared._Mono.Detection;
using Content.Shared._Triad.CCVar;
using Content.Shared.Ghost;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Shared;
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
///     identical to the grid that eventually loads in.
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
    private float _sensedRange;
    private float _describeBudgetMs;
    private float _describeLead;

    /// <summary>Half of the PVS view square: how far a person can actually see from where they stand.</summary>
    private float _viewRange;

    private float _accumulator;
    private int _nextRecordId = 1;

    /// <summary>Cells wanted by loaders but not yet described, nearest-first per pass.</summary>
    private readonly List<(float DistSq, Vector2i Coords, EntityUid Map)> _pending = new();
    private readonly HashSet<(EntityUid Map, Vector2i Coords)> _pendingSet = new();

    /// <summary>Everything making space real this pass, as (map, centre, radius).</summary>
    private readonly List<(EntityUid Map, Vector2 Center, float Range)> _sources = new();
    private List<Entity<MapGridComponent>> _gridsIntersecting = new();

    /// <summary>Flat id index over every live record, for the contact channel's diffing.</summary>
    public readonly Dictionary<int, DebrisRecord> Records = new();

    public override void Initialize()
    {
        Subs.CVar(_cfg, TriadCCVars.WorldgenSensedEnabled, v => _enabled = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenSensedRange, v => _sensedRange = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenDescribeBudgetMs, v => _describeBudgetMs = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenDescribeLeadS, v => _describeLead = v, true);
        // net.pvs_range is the side of the view square, so half of it is the reach from centre.
        Subs.CVar(_cfg, CVars.NetMaxUpdateRange, v => _viewRange = v * 0.5f, true);

        SubscribeLocalEvent<SensedCellComponent, ComponentShutdown>(OnCellShutdown);
    }

    private void OnCellShutdown(EntityUid uid, SensedCellComponent component, ComponentShutdown args)
    {
        foreach (var record in component.Records.Values)
        {
            Records.Remove(record.Id);
        }
    }

    public override void Update(float frameTime)
    {
        if (!_enabled)
            return;

        _accumulator += frameTime;
        if (_accumulator < UpdateInterval)
            return;
        _accumulator -= UpdateInterval;

        CollectPending();
        DrainPending();

        SensedMetrics.Records.Set(Records.Count);
    }

    /// <summary>
    ///     Gathers undescribed cells around everything present in space, ordered nearest-first
    ///     so the picture fills outward from people rather than in map order.
    /// </summary>
    private void CollectPending()
    {
        _pending.Clear();
        _pendingSet.Clear();
        _sources.Clear();

        CollectSources();

        var controllerQuery = GetEntityQuery<WorldControllerComponent>();

        foreach (var (map, center, range) in _sources)
        {
            if (!controllerQuery.HasComp(map))
                continue;

            var radius = (int) MathF.Ceiling(range / WorldGen.ChunkSize) + 1;
            var chunks = new GridPointsNearEnumerator(WorldGen.WorldToChunkCoords(center).Floored(), radius);

            while (chunks.MoveNext(out var chunk))
            {
                var coords = chunk.Value;
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
    ///     Everything that makes space around it real, each with the radius it earns. Rock is
    ///     described for anyone present, not only for whoever owns a sensor: a player out on a
    ///     jetpack has real asteroids around them, and a scanner in their hand only decides how
    ///     far out they can see the ones already there.
    /// </summary>
    private void CollectSources()
    {
        var loaders = EntityQueryEnumerator<WorldLoaderComponent, TransformComponent>();
        while (loaders.MoveNext(out var uid, out var loader, out var xform))
        {
            if (loader.Disabled)
                continue;

            // A console sits wherever it sits on its ship; sense from the hull, not the console,
            // or the far end of a kilometre-long capital outruns its own describe radius.
            AddSource(uid, xform, _sensedRange + GetLoaderExtent(uid, xform));
        }

        // Anyone present in space makes the space around them real, sensor or not. The halo is
        // whichever box reaches further: the hull they are standing on, or what they can see
        // from it. A jetpack miner is their own view box; a crewman is their ship.
        var ghostQuery = GetEntityQuery<GhostComponent>();
        var minds = EntityQueryEnumerator<MindContainerComponent, TransformComponent>();
        while (minds.MoveNext(out var uid, out var mind, out var xform))
        {
            if (!mind.HasMind || ghostQuery.HasComp(uid))
                continue;

            AddSource(uid, xform, MathF.Max(_viewRange, GetLoaderExtent(uid, xform)));
        }

        // A powered handheld scanner reaches further than the person holding it, so it extends
        // the halo to full sensed range: the picture on a mass scanner is as honest as the one
        // on a bridge console, just weaker for its lower detection multiplier.
        var toggleQuery = GetEntityQuery<ItemToggleComponent>();
        var consoles = EntityQueryEnumerator<RadarConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out var uid, out var console, out var xform))
        {
            if (toggleQuery.TryComp(uid, out var toggle) && !toggle.Activated)
                continue;

            AddSource(uid, xform, MathF.Min(console.MaxRange, _sensedRange));
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

    private void DrainPending()
    {
        if (_pending.Count == 0)
            return;

        var budget = TimeSpan.FromMilliseconds(_describeBudgetMs);
        var start = _timing.RealTime;
        var described = 0;

        // Already-described cells stay in the sweep and cost a lookup each; the budget only
        // has to bound real describe work, and rechecking is how a cell that was GC'd out
        // from under us gets rebuilt.
        foreach (var (_, coords, map) in _pending)
        {
            if (_timing.RealTime - start > budget)
                break;

            var cell = GetOrCreateChunk(coords, map);
            if (cell is null || HasComp<SensedCellComponent>(cell.Value))
                continue;

            Describe(cell.Value);
            described++;
        }

        if (described > 0)
        {
            SensedMetrics.CellsDescribed.Inc(described);
            SensedMetrics.DescribePass.Observe((_timing.RealTime - start).TotalSeconds);
        }
    }

    /// <summary>
    ///     Describes a cell if it has not been described yet. Callers that need a cell's
    ///     contents right now (the materialization queue on a chunk load that outran the
    ///     sweep) use this instead of waiting for the next pass.
    /// </summary>
    public SensedCellComponent? EnsureDescribed(EntityUid cell)
    {
        if (TryComp<SensedCellComponent>(cell, out var existing))
            return existing;

        return Describe(cell);
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
    ///     Computes the record's radar-facing data from its prototype and seed: hull outline,
    ///     family colour, and the detection terms <see cref="DetectionSystem"/> would apply to
    ///     the real grid, so a contact appears at exactly the range its grid would.
    /// </summary>
    private void FillShape(DebrisRecord record)
    {
        if (!_proto.TryIndex<EntityPrototype>(record.Proto, out var proto))
            return;

        if (proto.TryGetComponent<IFFComponent>("IFF", out var iff))
            record.IffColor = iff.Color;

        if (proto.TryGetComponent<DetectedAtRangeMultiplierComponent>("DetectedAtRangeMultiplier", out var detect))
            record.DetectBias = detect.VisualBias;
        else
            detect = null;

        if (!proto.TryGetComponent<BlobFloorPlanBuilderComponent>("BlobFloorPlanBuilder", out var blob))
            return;

        var tiles = BlobShapeGen.Roll(new System.Random(record.Seed), blob.Radius, blob.FloorPlacements,
            blob.BlobDrawProb, Math.Max(1, blob.FloorTileset.Count));

        if (tiles.Count == 0)
            return;

        record.Hull = BlobShapeGen.ComputeHull(tiles);

        // DetectionSystem sizes a grid by its local AABB diagonal; the hull's AABB is that
        // same box, computed without ever building the grid.
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var vert in record.Hull)
        {
            min = Vector2.Min(min, vert);
            max = Vector2.Max(max, vert);
        }

        var size = max - min;
        record.DetectSignature = MathF.Sqrt(size.X * size.X + size.Y * size.Y) * (detect?.VisualMultiplier ?? 1f);
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

    private bool HasCollisions(MapId mapId, Box2 point)
    {
        _gridsIntersecting.Clear();
        _mapManager.FindGridsIntersecting(mapId, point, ref _gridsIntersecting);
        return _gridsIntersecting.Count > 0;
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
