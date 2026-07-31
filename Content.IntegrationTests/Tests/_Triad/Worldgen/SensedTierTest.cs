// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Worldgen.Cells;
using Content.Server.Worldgen;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Components.Debris;
using Content.Server.Worldgen.Prototypes;
using Content.Shared._Triad.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

/// <summary>
///     Covers the describe/materialize seam: cells get decided as data before any entity
///     exists, materialization builds the rock that was described, and a rock that unloads
///     comes back identical rather than re-rolled.
/// </summary>
[TestFixture]
public sealed class SensedTierTest
{
    private const string WorldgenConfig = "NFDefault";
    private const string NearLoader = "TriadSensedTestLoaderNear";
    private const string FarLoader = "TriadSensedTestLoaderFar";

    // WorldLoaderComponent.Radius is write-locked to WorldControllerSystem, so the two loader
    // sizes the tests need come from prototypes rather than field pokes.
    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {NearLoader}
  components:
  - type: WorldLoader
    radius: 128

- type: entity
  id: {FarLoader}
  components:
  - type: WorldLoader
    radius: 640
";

    /// <summary>
    ///     Builds a worldgen map with the live belt config applied and a stationary loader at
    ///     origin, then ticks until the describe pass has run.
    /// </summary>
    private static async Task<(EntityUid Map, EntityUid Loader)> SetupBelt(
        Content.IntegrationTests.Pair.TestPair pair, float sensedRange, string loaderProto,
        bool needMaterialized)
    {
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var serManager = server.ResolveDependency<ISerializationManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var mapSystem = entManager.System<SharedMapSystem>();

        EntityUid mapUid = default;
        EntityUid loader = default;

        await server.WaitPost(() =>
        {
            cfg.SetCVar(TriadCCVars.WorldgenSensedEnabled, true);
            cfg.SetCVar(TriadCCVars.WorldgenDescribeRange, sensedRange);
            // Budgets high enough that a test map describes and materializes in a few ticks.
            cfg.SetCVar(TriadCCVars.WorldgenDescribeBudgetMs, 500f);
            cfg.SetCVar(TriadCCVars.WorldgenMaterializeBudgetMs, 500f);

            mapUid = mapSystem.CreateMap(out var mapId);
            protoManager.Index<WorldgenConfigPrototype>(WorldgenConfig).Apply(mapUid, serManager, entManager);

            loader = entManager.SpawnEntity(loaderProto, new MapCoordinates(Vector2.Zero, mapId));
        });

        // Describe and chunk-load both run at 1 Hz, and a pooled server under a loaded test run
        // does not give a fixed tick count a predictable amount of that. Wait on the outcome.
        var map = mapUid;
        await PoolManager.WaitUntil(server, () =>
        {
            var records = entManager.System<CellDescribeSystem>().Records.Values
                .Where(r => r.Map == map)
                .ToList();

            return needMaterialized
                ? records.Any(r => r is { State: SensedState.Materialized, Hull: not null })
                : records.Any(r => r is { State: SensedState.Dormant, Hull: not null });
        }, maxTicks: 900);

        await server.WaitIdleAsync();

        return (mapUid, loader);
    }

    private static List<DebrisRecord> RecordsOnMap(IEntityManager entManager, EntityUid map)
    {
        var describe = entManager.System<CellDescribeSystem>();
        return describe.Records.Values.Where(r => r.Map == map).ToList();
    }

    [Test]
    public async Task DescribesBeyondLoadRadius()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();

        // Sense far, load near: the gap between the two is the whole point of the tier.
        var (map, _) = await SetupBelt(pair, sensedRange: 1024f, loaderProto: NearLoader, needMaterialized: false);

        await server.WaitAssertion(() =>
        {
            var records = RecordsOnMap(entManager, map);
            Assert.That(records, Is.Not.Empty, "describe pass produced no records for the belt map");

            var withHulls = records.Where(r => r.Hull is not null).ToList();
            Assert.That(withHulls, Is.Not.Empty, "no described debris carried a hull to paint on radar");
            // Outlines are true silhouettes now, not convex hulls, so the ceiling is the tracer's
            // sanity valve rather than the hull's vertex cap. A real rock traces to somewhere
            // between 19 and 182 vertices depending on size tier; the floor still matters, since
            // fewer than three vertices is not a polygon.
            Assert.That(withHulls.All(r => r.Hull!.Length is > 2 and <= TileOutline.MaxOutlineVerts),
                "outlines must be bounded polygons, not degenerate or unbounded");
            Assert.That(withHulls.Any(r => r.DetectSignature > 0f),
                "described debris carried no detection signature, so contacts could never resolve");

            var farDormant = records.Where(r => r.Point.Length() > 400f && r.State == SensedState.Dormant).ToList();
            Assert.That(farDormant, Is.Not.Empty,
                "everything described near the loader materialized; nothing is left sensed-only to draw at range");

            foreach (var record in farDormant)
            {
                Assert.That(record.Entity, Is.Null, "a dormant record must own no entity");
            }
        });

        // The map holds a live WorldLoader, so leaving it behind hands the next test on a
        // recycled pair a world that is still describing and loading chunks.
        await server.WaitPost(() => entManager.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MaterializesExactlyWhatWasDescribed()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();

        var (map, _) = await SetupBelt(pair, sensedRange: 512f, loaderProto: FarLoader, needMaterialized: true);

        await server.WaitAssertion(() =>
        {
            var materialized = RecordsOnMap(entManager, map)
                .Where(r => r.State == SensedState.Materialized)
                .ToList();

            Assert.That(materialized, Is.Not.Empty, "nothing materialized inside the loader radius");

            foreach (var record in materialized)
            {
                Assert.That(record.Entity, Is.Not.Null);
                var ent = record.Entity!.Value;
                Assert.That(entManager.EntityExists(ent), Is.True);

                var meta = entManager.GetComponent<MetaDataComponent>(ent);
                Assert.That(meta.EntityPrototype?.ID, Is.EqualTo(record.Proto),
                    "materialized entity does not match the described prototype");

                var xform = entManager.GetComponent<TransformComponent>(ent);
                var worldPos = entManager.System<SharedTransformSystem>().GetWorldPosition(xform);
                Assert.That((worldPos - record.Point).Length(), Is.LessThan(0.01f),
                    "materialized entity is not at the described point");

                // The seed the shape was described from must be the seed the grid built from.
                Assert.That(entManager.TryGetComponent<PredeterminedShapeComponent>(ent, out var shape), Is.True,
                    "materialized debris did not receive its record seed");
                Assert.That(shape!.Seed, Is.EqualTo(record.Seed));
            }
        });

        await server.WaitPost(() => entManager.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     The radar promise, stated at the strength we actually make it: the outline painted while
    ///     dormant is the silhouette of the ROCK that loads in, not of the finished grid. Describe
    ///     models the shape roll and stops there. Decoration runs afterwards at build time and lays
    ///     tiles of its own, chiefly the RoomFill markers in the NF debris tables, so a decorated
    ///     grid traces to a slightly larger outline than the one describe stored.
    ///
    ///     So this asserts equality where nothing decorated the grid and growth-only where something
    ///     did: what built may enclose more than what was painted, never less, and never somewhere
    ///     else. That still catches everything an exact comparison caught, because a seed desync,
    ///     drift between the describe and build generators, and an outline that stops tracking the
    ///     rock all MOVE the shape rather than grow it, and a moved shape fails the bounds check.
    /// </summary>
    [Test]
    public async Task DescribedHullMatchesMaterializedGrid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();

        var (map, _) = await SetupBelt(pair, sensedRange: 512f, loaderProto: FarLoader, needMaterialized: true);

        await server.WaitAssertion(() =>
        {
            var mapSystem = entManager.System<SharedMapSystem>();
            var exact = 0;
            var decorated = 0;
            var all = RecordsOnMap(entManager, map);
            var materializedCount = all.Count(r => r.State == SensedState.Materialized);
            var withHull = all.Count(r => r.State == SensedState.Materialized && r.Hull is not null);
            var withGrid = all.Count(r => r.State == SensedState.Materialized && r.Entity is { } e
                                          && entManager.HasComponent<MapGridComponent>(e));

            foreach (var record in all)
            {
                if (record is not { State: SensedState.Materialized, Hull: not null, Entity: { } ent }
                    || !entManager.TryGetComponent<MapGridComponent>(ent, out var grid))
                    continue;

                // Take the grid's real tiles, not their bounding box. Comparing boxes only ever
                // constrained four numbers out of a couple of hundred tiles and said nothing at
                // all about shape.
                var tiles = mapSystem.GetAllTilesEnumerator(ent, grid);
                var gridTiles = new List<BlobTile>();

                while (tiles.MoveNext(out var tile))
                {
                    gridTiles.Add(new BlobTile(tile.Value.GridIndices, 0));
                }

                if (gridTiles.Count == 0)
                    continue;

                var fromGrid = TileOutline.Trace(gridTiles);

                Assert.That(fromGrid, Is.Not.Null,
                    $"could not trace the materialized grid of {record.Proto}");

                if (record.Hull!.SequenceEqual(fromGrid!))
                {
                    exact++;
                    continue;
                }

                // Decorated. The described silhouette still has to sit inside what actually built,
                // and that is what separates "a room was laid on top of the rock" from "we described
                // a different rock": decoration only ever adds tiles, so it can only push the
                // outline outward. Anything that moves the shape breaks containment immediately.
                var (describedMin, describedMax) = Bounds(record.Hull);
                var (builtMin, builtMax) = Bounds(fromGrid!);

                Assert.That(
                    builtMin.X <= describedMin.X && builtMin.Y <= describedMin.Y
                    && builtMax.X >= describedMax.X && builtMax.Y >= describedMax.Y, Is.True,
                    $"the outline painted at range is not inside the rock that loaded for {record.Proto}: "
                    + $"described {describedMin}..{describedMax} against {builtMin}..{builtMax} from the grid");

                Assert.That(EnclosedCells(fromGrid!), Is.GreaterThanOrEqualTo(EnclosedCells(record.Hull)),
                    $"the materialized grid of {record.Proto} encloses less than the outline painted for it");

                decorated++;
            }

            // At least one undecorated rock has to be in the sample, or the equality half of the
            // promise is never exercised and this degenerates into the containment check alone.
            Assert.That(exact, Is.GreaterThan(0),
                $"no undecorated blob debris was available to compare exactly against its hull "
                + $"(records={all.Count} materialized={materializedCount} withHull={withHull} "
                + $"withGrid={withGrid} exact={exact} decorated={decorated})");
        });

        await server.WaitPost(() => entManager.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }

    /// <summary>Inclusive integer bounding corners of an outline.</summary>
    private static (Vector2i Min, Vector2i Max) Bounds(Vector2i[] outline)
    {
        var min = outline[0];
        var max = outline[0];

        foreach (var vert in outline)
        {
            min = new Vector2i(System.Math.Min(min.X, vert.X), System.Math.Min(min.Y, vert.Y));
            max = new Vector2i(System.Math.Max(max.X, vert.X), System.Math.Max(max.Y, vert.Y));
        }

        return (min, max);
    }

    /// <summary>
    ///     Enclosed area in whole cells. The outline is rectilinear on the integer lattice, so the
    ///     shoelace area is always a whole number of cells and this stays exact.
    /// </summary>
    private static long EnclosedCells(Vector2i[] outline)
    {
        long acc = 0;

        for (var i = 0; i < outline.Length; i++)
        {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Length];
            acc += (long) a.X * b.Y - (long) b.X * a.Y;
        }

        return System.Math.Abs(acc) / 2;
    }

    /// <summary>
    ///     Deleting a materialized rock while its cell is still loaded is destruction, not a
    ///     garbage collection: the record goes away so the contact does not linger.
    /// </summary>
    [Test]
    public async Task DestroyingLoadedDebrisRemovesItsRecord()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();

        var (map, _) = await SetupBelt(pair, sensedRange: 512f, loaderProto: FarLoader, needMaterialized: true);

        var recordId = 0;
        await server.WaitPost(() =>
        {
            var record = RecordsOnMap(entManager, map)
                .First(r => r.State == SensedState.Materialized && r.Entity is not null);

            recordId = record.Id;
            entManager.DeleteEntity(record.Entity!.Value);
        });

        await pair.Server.WaitRunTicks(10);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var describe = entManager.System<CellDescribeSystem>();
            Assert.That(describe.Records.ContainsKey(recordId), Is.False,
                "a rock destroyed in play left its record behind, so radar would keep painting it");
        });

        await server.WaitPost(() => entManager.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     With the tier switched off, nothing describes and the stock burst-spawn path is
    ///     the only thing building debris. This is the revert switch working.
    /// </summary>
    [Test]
    public async Task DisabledFallsBackToStockPlacer()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var serManager = server.ResolveDependency<ISerializationManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var mapSystem = entManager.System<SharedMapSystem>();

        EntityUid mapUid = default;
        await server.WaitPost(() =>
        {
            cfg.SetCVar(TriadCCVars.WorldgenSensedEnabled, false);

            mapUid = mapSystem.CreateMap(out var mapId);
            protoManager.Index<WorldgenConfigPrototype>(WorldgenConfig).Apply(mapUid, serManager, entManager);

            entManager.SpawnEntity(FarLoader, new MapCoordinates(Vector2.Zero, mapId));
        });

        var map = mapUid;
        await PoolManager.WaitUntil(server, () =>
        {
            var debris = entManager.EntityQueryEnumerator<OwnedDebrisComponent, TransformComponent>();
            while (debris.MoveNext(out _, out _, out var xform))
            {
                if (xform.MapUid == map)
                    return true;
            }

            return false;
        }, maxTicks: 900);

        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            Assert.That(RecordsOnMap(entManager, mapUid), Is.Empty,
                "the describe service ran with the sensed tier disabled");

            // Scoped to this map: a pooled server carries cells from whichever test ran before.
            var cells = entManager.EntityQueryEnumerator<SensedCellComponent, TransformComponent>();
            while (cells.MoveNext(out _, out _, out var xform))
            {
                Assert.That(xform.MapUid, Is.Not.EqualTo(mapUid), "cells were described with the tier disabled");
            }

            // The stock placer is the only thing spawning now, and it still spawns.
            var debris = entManager.EntityQueryEnumerator<OwnedDebrisComponent, TransformComponent>();
            var stockSpawned = false;
            while (!stockSpawned && debris.MoveNext(out _, out _, out var xform))
            {
                stockSpawned = xform.MapUid == mapUid;
            }

            Assert.That(stockSpawned, Is.True, "the stock placer spawned nothing with the sensed tier disabled");
        });

        await server.WaitPost(() => entManager.DeleteEntity(mapUid));
        await pair.CleanReturnAsync();
    }
}
