// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Worldgen.Cells;
using Content.Server.Worldgen.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

/// <summary>
///     Pins the seeded-baseline law for debris interiors: the same record seed must rebuild the
///     same interior, walls and loot both, because consumption capture later diffs against this
///     baseline and because "rocks do not grow back rock" is only true if a re-materialized rock
///     is the same rock. Two grids rolled from one seed here means an unload/reload cycle in the
///     live game rebuilds identically, without paying for a real worldgen map and loader.
/// </summary>
[TestFixture]
public sealed class InteriorSeedTest
{
    private const string RockProto = "TriadInteriorSeedTestRock";

    private const string LootSpawner = "TriadSeedTestLootSpawner";

    // WallRock is inert; the loot spawner is a real RandomSpawner marker, so the test exercises
    // the whole chain: populator tile pick (InteriorStage), marker seed stamping (LootStage),
    // and ConditionalSpawnerSystem honoring the stamp for the pick and the scatter. A dedicated
    // marker with chance 1 rather than a shipping salvage spawner, because those gate their
    // output on their own loot-table odds and a test must not depend on content tuning. The
    // blank entry keeps some tiles empty so the tile pick genuinely varies.
    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {LootSpawner}
  components:
  - type: RandomSpawner
    chance: 1.0
    offset: 0.4
    prototypes:
    - SheetSteel1
    - SheetGlass1
    - SheetPlasma1

- type: entity
  id: {RockProto}
  components:
  - type: MapGrid
  - type: BlobFloorPlanBuilder
    radius: 6
    floorPlacements: 14
    blobDrawProb: 0.5
    floorTileset:
    - FloorAsteroidSand
  - type: SimpleFloorPlanPopulator
    entries:
      FloorAsteroidSand:
      - prob: 0.3
      - id: WallRock
        prob: 0.4
      - id: {LootSpawner}
        prob: 0.3
";

    /// <summary>
    ///     Spawns the test rock with the given seed, fires the populate event the locality
    ///     loader would fire, and harvests the multiset of (prototype, grid-local position) for
    ///     every direct child of the grid, all in one post. The whole spawn chain is synchronous
    ///     inside the event, and harvesting before a single physics tick runs is what makes
    ///     exact float positions comparable: once physics gets a pass, a scattered item clipping
    ///     a wall fixture gets depenetrated in a world-position-sensitive way, and the two
    ///     builds deliberately sit at different world positions. Spawner markers are still
    ///     present (their QueueDel has not flushed) and are skipped by name.
    /// </summary>
    private static async Task<Dictionary<(string Proto, float X, float Y), int>> SpawnAndHarvest(
        Content.IntegrationTests.Pair.TestPair pair, EntityUid map, Vector2 pos, int seed)
    {
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();

        var result = new Dictionary<(string, float, float), int>();
        await server.WaitPost(() =>
        {
            var overrides = new ComponentRegistry();
            overrides[entManager.ComponentFactory.GetComponentName(typeof(PredeterminedShapeComponent))] =
                new EntityPrototype.ComponentRegistryEntry(new PredeterminedShapeComponent { Seed = seed });

            var grid = entManager.SpawnAttachedTo(RockProto, new EntityCoordinates(map, pos), overrides);
            entManager.EventBus.RaiseLocalEvent(grid, new LocalStructureLoadedEvent());

            var xform = entManager.GetComponent<TransformComponent>(grid);
            var children = xform.ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                var meta = entManager.GetComponent<MetaDataComponent>(child);
                var proto = meta.EntityPrototype?.ID ?? "<none>";
                if (proto == LootSpawner)
                    continue;

                var childXform = entManager.GetComponent<TransformComponent>(child);
                var key = (proto, childXform.LocalPosition.X, childXform.LocalPosition.Y);
                result[key] = result.GetValueOrDefault(key) + 1;
            }
        });

        return result;
    }

    [Test]
    public async Task SameSeedRebuildsIdenticalInterior()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();

        var map = await pair.CreateTestMap();

        // Same seed, two builds, far enough apart that nothing overlaps. Local coordinates make
        // the world positions irrelevant.
        var first = await SpawnAndHarvest(pair, map.MapUid, new Vector2(0f, 0f), seed: 741);
        var second = await SpawnAndHarvest(pair, map.MapUid, new Vector2(200f, 0f), seed: 741);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Empty, "the populator spawned nothing at all");
            Assert.That(first.Keys.Any(k => k.Proto.StartsWith("Sheet")),
                "no loot-chain entity spawned, so the seeded spawner path was never exercised "
                + "(the marker guarantees output, so this is a broken chain, not seed luck)");
            Assert.That(second, Is.EquivalentTo(first),
                "the same seed built a different interior: the seeded-baseline law is broken and "
                + "consumption capture has nothing stable to diff against");
        });

        await server.WaitPost(() => entManager.DeleteEntity(map.MapUid));
        await pair.CleanReturnAsync();
    }
}
