// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Worldgen.Cells;
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
///     Covers the capture rung of the two-rung ladder: a rock touched while materialized
///     serializes to a blob at cell unload and restores its exact state next visit, closing the
///     unload/reload duplication exploit (consumed loot and mined state must not regenerate
///     from the seed). The untouched rock in the same belt proves the free rung stays free.
/// </summary>
[TestFixture]
public sealed class RecordCaptureTest
{
    private const string LoaderProto = "TriadCaptureTestLoader";
    private const string MarkerProto = "TriadCaptureTestMarker";

    [TestPrototypes]
    private const string Prototypes = $@"
# 640 like RecordsTest's far loader: materialization needs a wide loaded area to reliably
# hold two shaped blob rocks, and a 128 loader only wakes a couple of cells.
- type: entity
  id: {LoaderProto}
  components:
  - type: WorldLoader
    radius: 640

- type: entity
  id: {MarkerProto}
";

    [Test]
    public async Task TouchedRockCapturesUntouchedRockRollsBack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var serManager = server.ResolveDependency<ISerializationManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var tileMan = server.ResolveDependency<ITileDefinitionManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var describe = server.System<CellDescribeSystem>();
        var capture = server.System<DebrisCaptureSystem>();

        EntityUid mapUid = default;
        MapId mapId = default;
        EntityUid loader = default;

        await server.WaitPost(() =>
        {
            cfg.SetCVar(TriadCCVars.WorldgenRecordsEnabled, true);
            cfg.SetCVar(TriadCCVars.WorldgenDescribeRange, 512f);
            cfg.SetCVar(TriadCCVars.WorldgenDescribeBudgetMs, 500f);
            cfg.SetCVar(TriadCCVars.WorldgenMaterializeBudgetMs, 500f);
            cfg.SetCVar(TriadCCVars.WorldgenUnloadGraceS, 0f);

            mapUid = mapSystem.CreateMap(out mapId);
            protoManager.Index<WorldgenConfigPrototype>("NFDefault").Apply(mapUid, serManager, entManager);
            loader = entManager.SpawnEntity(LoaderProto, new MapCoordinates(Vector2.Zero, mapId));
        });

        // Two materialized blob rocks near the loader: one to touch, one as the free-rung control.
        var map = mapUid;
        await PoolManager.WaitUntil(server, () => describe.Records.Values.Count(r =>
            r.Map == map && r is { State: RecordState.Materialized, Shaped: true, Entity: not null }) >= 2,
            maxTicks: 900);
        await server.WaitIdleAsync();

        DebrisRecord touched = null!;
        DebrisRecord untouched = null!;
        Vector2i minedTile = default;
        int steelTileId = default;

        await server.WaitPost(() =>
        {
            var materialized = describe.Records.Values.Where(r =>
                    r.Map == map && r is { State: RecordState.Materialized, Shaped: true, Entity: not null })
                .ToList();
            touched = materialized[0];
            untouched = materialized[1];

            var grid = touched.Entity!.Value;
            var gridComp = entManager.GetComponent<MapGridComponent>(grid);

            // The consumption stand-in: change one of the rock's tiles and leave a loose item
            // aboard. If the blob restores, both survive the unload cycle exactly as left; if
            // the seed re-rolls, the tile reverts and the marker vanishes.
            minedTile = mapSystem.GetAllTiles(grid, gridComp).First().GridIndices;
            steelTileId = tileMan["FloorSteel"].TileId;
            mapSystem.SetTile(grid, gridComp, minedTile, new Tile(steelTileId));

            Assert.That(mapSystem.GetTileRef(grid, gridComp, minedTile).Tile.TypeId, Is.EqualTo(steelTileId),
                "the consumption stand-in never landed: SetTile did not stick before capture");

            entManager.SpawnEntity(MarkerProto,
                new EntityCoordinates(grid, new Vector2(minedTile.X + 0.5f, minedTile.Y + 0.5f)));
        });

        // Unload: no loader, zero grace. The unload event queues the capture and the budgeted
        // drain performs it a tick or two later, while the GC hold keeps the grid alive.
        await server.WaitPost(() => entManager.DeleteEntity(loader));
        await PoolManager.WaitUntil(server, () => capture.HasBlob(touched.Id), maxTicks: 300);

        // Ground truth: the serialized yaml must contain the consumption stand-ins, or the
        // restore assertions below are testing the loader against a blob that never held them.
        await server.WaitAssertion(() =>
        {
            Assert.That(capture.TryGetBlobYaml(touched.Id, out var yaml), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(yaml, Does.Contain(MarkerProto),
                    "the item left aboard was not serialized into the blob");
                Assert.That(yaml, Does.Contain("FloorSteel"),
                    "the changed tile was not serialized into the blob (tilemap or chunk data is stale)");
            });
        });

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(touched.Captured, Is.True, "the touched rock was not marked captured at unload");
                Assert.That(touched.Version, Is.EqualTo(1), "capture did not bump the wire version");
                Assert.That(touched.CapturedOutline, Is.Not.Null, "capture traced no outline for a tiled rock");

                Assert.That(capture.HasBlob(untouched.Id), Is.False,
                    "an untouched rock paid for a blob; the free rung is not free");
                Assert.That(untouched.Captured, Is.False, "an untouched rock was marked captured");
                Assert.That(untouched.Version, Is.EqualTo(0), "an untouched rock's version moved");
            });
        });

        // The GC queue deletes unloaded debris on its own schedule; forcing the delete here is
        // the same teardown, just now, and must read as a rollback (record survives, dormant).
        await server.WaitPost(() =>
        {
            foreach (var record in new[] { touched, untouched })
            {
                if (record.Entity is { } ent && entManager.EntityExists(ent))
                    entManager.DeleteEntity(ent);
            }
        });
        await PoolManager.WaitUntil(server, () =>
            touched.State == RecordState.Dormant && untouched.State == RecordState.Dormant, maxTicks: 60);

        // Revisit: a fresh loader on the touched rock re-materializes it from the blob.
        await server.WaitPost(() =>
        {
            entManager.SpawnEntity(LoaderProto, new MapCoordinates(touched.Point, mapId));
        });
        await PoolManager.WaitUntil(server, () =>
            touched is { State: RecordState.Materialized, Entity: not null }, maxTicks: 600);

        await server.WaitAssertion(() =>
        {
            var grid = touched.Entity!.Value;
            var gridComp = entManager.GetComponent<MapGridComponent>(grid);

            var markers = 0;
            var children = entManager.GetComponent<TransformComponent>(grid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                if (entManager.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID == MarkerProto)
                    markers++;
            }

            var steelAt = new System.Collections.Generic.List<Vector2i>();
            var tileCount = 0;
            foreach (var t in mapSystem.GetAllTiles(grid, gridComp))
            {
                tileCount++;
                if (t.Tile.TypeId == steelTileId)
                    steelAt.Add(t.GridIndices);
            }

            var diag = $"record {touched.Id}: Captured={touched.Captured} Version={touched.Version} "
                + $"HasBlob={capture.HasBlob(touched.Id)} grid={entManager.ToPrettyString(grid)} "
                + $"pos={entManager.GetComponent<TransformComponent>(grid).LocalPosition} point={touched.Point} "
                + $"tiles={tileCount} steelAt=[{string.Join(", ", steelAt)}]";

            var tile = mapSystem.GetTileRef(grid, gridComp, minedTile);

            Assert.Multiple(() =>
            {
                Assert.That(markers, Is.EqualTo(1),
                    $"the item left aboard did not survive the unload cycle exactly once "
                    + $"(0 = seed re-roll, 2+ = double restore); {diag}");
                Assert.That(tile.Tile.TypeId, Is.EqualTo(steelTileId),
                    $"a captured rock came back with its seed-rolled tile at {minedTile}; {diag}");
            });
        });

        // The classifier's container altitude: moving loot from a container on the rock into a
        // container elsewhere reparents crate-to-bag, and the grid never appears as a parent.
        // This was the dupe vector for every crate on a wreck (take the loot, fly off, come
        // back to a refilled crate), so it gets its own regression check against the restored
        // rock: the extraction alone, from a clean baseline, must mark the grid.
        await server.WaitAssertion(() =>
        {
            var containerSys = entManager.System<Robust.Shared.Containers.SharedContainerSystem>();
            var grid = touched.Entity!.Value;

            var crate = entManager.SpawnEntity(MarkerProto,
                new EntityCoordinates(grid, new Vector2(minedTile.X + 0.5f, minedTile.Y + 0.5f)));
            var crateContainer = containerSys.EnsureContainer<Robust.Shared.Containers.Container>(crate, "loot");
            var item = entManager.SpawnEntity(MarkerProto, new MapCoordinates(touched.Point, mapId));
            Assert.That(containerSys.Insert(item, crateContainer), Is.True);

            var bag = entManager.SpawnEntity(MarkerProto,
                new MapCoordinates(touched.Point + new Vector2(200f, 200f), mapId));
            var bagContainer = containerSys.EnsureContainer<Robust.Shared.Containers.Container>(bag, "loot");

            capture.DebugSetTouched(grid, false);
            var recorded = entManager.GetComponent<RecordedDebrisComponent>(grid);
            Assert.That(recorded.Touched, Is.False, "baseline reset failed; the classifier check proves nothing");

            Assert.That(containerSys.Insert(item, bagContainer), Is.True);
            Assert.That(recorded.Touched, Is.True,
                "container-to-container extraction off a recorded grid did not mark it touched; "
                + "crate looting would regenerate on the free rung");
        });

        await server.WaitPost(() => entManager.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }
}
