// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Worldgen.Cells;
using Content.Server.Shuttles.Systems;
using Content.Server.Worldgen.Prototypes;
using Content.Shared._Triad.CCVar;
using Content.Shared.Damage;
using Content.Shared.Weapons.Hitscan.Events;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

/// <summary>
///     Covers the records consumers beyond projectiles: ship hitscan beams stop on dormant
///     rocks, FTL arrival slides its landing spot off them, and the shared box query they both
///     ask through honours the same shaped/dormant/ghost gates as everything else.
/// </summary>
[TestFixture]
public sealed class RecordConsumersTest
{
    private const string RockProto = "TriadConsumerTestRock";
    private const string BeamProto = "TriadConsumerTestBeam";
    private const string LoaderProto = "TriadConsumerTestLoader";
    private const int RecordId = 920_001;

    // The beam mirrors a ship laser's load-bearing parts: raycast, damage, and the ship-grade
    // marker that opts it into data-space blocking. Damage carries Structural so the control
    // shot provably hurts a wall.
    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {RockProto}
  components:
  - type: BlobFloorPlanBuilder
    radius: 6
    floorPlacements: 14
    blobDrawProb: 0.5
    floorTileset:
    - FloorAsteroidSand

- type: entity
  id: {BeamProto}
  components:
  - type: HitscanBasicRaycast
    maxDistance: 200
  - type: HitscanBasicDamage
    damage:
      types:
        Heat: 35
        Structural: 50
  - type: ShipWeaponHitscan

- type: entity
  id: {LoaderProto}
  components:
  - type: WorldLoader
    radius: 128
";

    /// <summary>
    ///     A ship beam fired down y = 0 through a described rock deals no damage to the wall
    ///     behind it; the same beam past a ghosted rock burns the wall. Runs on a plain map via
    ///     the segment query's flat fallback, exactly like the projectile block fixture.
    /// </summary>
    [Test]
    public async Task ShipBeamStopsOnDormantRockAndOnlyThere()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();

        var map = await pair.CreateTestMap();
        var describe = server.System<CellDescribeSystem>();

        DebrisRecord record = null!;
        EntityUid beam = default;
        EntityUid wall = default;

        await server.WaitPost(() =>
        {
            cfg.SetCVar(TriadCCVars.WorldgenRecordsEnabled, true);

            // Every blob roll contains tile (0, 0), so centring the record at y = -0.5 makes a
            // y = 0 beam a guaranteed hit regardless of seed; see RecordProjectileBlockTest.
            record = new DebrisRecord
            {
                Id = RecordId,
                Map = map.MapUid,
                Point = new Vector2(50f, -0.5f),
                Proto = RockProto,
                Seed = 999,
                Shaped = true,
                Bound = 12f,
                State = RecordState.Dormant,
            };
            describe.Records[record.Id] = record;

            beam = entManager.SpawnEntity(BeamProto, new MapCoordinates(new Vector2(30f, 0f), map.MapId));
            wall = entManager.SpawnEntity("WallSolid", new MapCoordinates(new Vector2(80f, 0f), map.MapId));
        });

        await server.WaitRunTicks(1);

        var damageable = entManager.GetComponent<DamageableComponent>(wall);

        await server.WaitPost(() => Fire(entManager, beam, map.MapUid, new Vector2(30f, 0f)));
        await server.WaitRunTicks(1);

        Assert.That(damageable.TotalDamage.Value, Is.EqualTo(0),
            "a ship beam burned a wall through a described rock; the hitscan block never intercepted");

        // Ghost the rock: radar stops painting it, so the beam must pass.
        await server.WaitPost(() =>
        {
            record.BlockedAttempts = DebrisRecord.MaxBlockedAttempts;
            Fire(entManager, beam, map.MapUid, new Vector2(30f, 0f));
        });
        await server.WaitRunTicks(1);

        Assert.That(damageable.TotalDamage.Value, Is.GreaterThan(0),
            "the control beam dealt no damage; the fixture is not actually exercising the damage path");

        await server.WaitPost(() =>
        {
            describe.Records.Remove(RecordId);
            entManager.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }

    private static void Fire(IEntityManager entManager, EntityUid beam, EntityUid mapUid, Vector2 from)
    {
        var ev = new HitscanTraceEvent
        {
            FromCoordinates = new EntityCoordinates(mapUid, from),
            ShotDirection = Vector2.UnitX,
            Gun = beam,
        };
        entManager.EventBus.RaiseLocalEvent(beam, ref ev);
    }

    /// <summary>
    ///     On a real belt: the box query returns a known dormant rock and honours the ghost
    ///     filter, and an FTL jump aimed dead at that rock lands clear of its bound instead of
    ///     inside it.
    /// </summary>
    [Test]
    public async Task FtlArrivalAndBoxQueryRespectDormantRocks()
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
        var xformSys = entManager.System<SharedTransformSystem>();
        var describe = server.System<CellDescribeSystem>();
        var shuttles = server.System<ShuttleSystem>();

        EntityUid mapUid = default;
        MapId mapId = default;

        await server.WaitPost(() =>
        {
            cfg.SetCVar(TriadCCVars.WorldgenRecordsEnabled, true);
            cfg.SetCVar(TriadCCVars.WorldgenDescribeRange, 512f);
            cfg.SetCVar(TriadCCVars.WorldgenDescribeBudgetMs, 500f);
            cfg.SetCVar(TriadCCVars.WorldgenMaterializeBudgetMs, 500f);

            mapUid = mapSystem.CreateMap(out mapId);
            protoManager.Index<WorldgenConfigPrototype>("NFDefault").Apply(mapUid, serManager, entManager);
            entManager.SpawnEntity(LoaderProto, new MapCoordinates(Vector2.Zero, mapId));
        });

        // A dormant rock well outside the loader's materialization range, so it stays data.
        var map = mapUid;
        await PoolManager.WaitUntil(server, () => describe.Records.Values.Any(r =>
            r.Map == map && r is { State: RecordState.Dormant, Shaped: true } && !r.GaveUp
            && r.Point.Length() > 300f), maxTicks: 900);
        await server.WaitIdleAsync();

        DebrisRecord target = null!;
        var results = new System.Collections.Generic.List<DebrisRecord>();

        await server.WaitAssertion(() =>
        {
            target = describe.Records.Values.First(r =>
                r.Map == map && r is { State: RecordState.Dormant, Shaped: true } && !r.GaveUp
                && r.Point.Length() > 300f);

            describe.CollectIntersecting(map, Box2.CenteredAround(target.Point, new Vector2(10f, 10f)), results);
            Assert.That(results, Does.Contain(target), "the box query missed a dormant rock under the box");

            describe.CollectIntersecting(map, Box2.CenteredAround(target.Point + new Vector2(5000f, 5000f), new Vector2(10f, 10f)), results);
            Assert.That(results, Is.Empty, "the box query found rocks in empty space");
        });

        // Jump a one-tile grid straight at the rock's centre.
        EntityUid shuttle = default;
        await server.WaitPost(() =>
        {
            var grid = mapSystem.CreateGridEntity(mapId);
            shuttle = grid.Owner;
            mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(tileMan["FloorSteel"].TileId));
            xformSys.SetWorldPosition(shuttle, new Vector2(-200f, -200f));

            shuttles.TryFTLProximity((shuttle, null), new EntityCoordinates(map, target.Point));
        });

        await server.WaitAssertion(() =>
        {
            var landed = xformSys.GetWorldPosition(shuttle);
            Assert.That((landed - target.Point).Length(), Is.GreaterThan(target.Bound),
                "FTL arrival landed inside a dormant rock's bound; the clear-spot search never consulted the records");
        });

        // Ghost filter: the same rock stops answering the box query once it gives up.
        await server.WaitAssertion(() =>
        {
            target.BlockedAttempts = DebrisRecord.MaxBlockedAttempts;
            describe.CollectIntersecting(map, Box2.CenteredAround(target.Point, new Vector2(10f, 10f)), results);
            Assert.That(results, Does.Not.Contain(target), "a ghost rock answered the box query");
        });

        await server.WaitPost(() => entManager.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }
}
