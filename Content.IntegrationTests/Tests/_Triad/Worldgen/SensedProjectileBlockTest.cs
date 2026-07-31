// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Worldgen.Cells;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

/// <summary>
///     Pins the tier-2 promise: a shot stops on a rock the radar paints even though the rock is
///     pure data, and only on rocks the radar paints. Projectiles are moved by hand rather than
///     fired, because the system under test consumes per-tick position segments and hand-moving
///     makes the segment exact; ballistics are the gun system's business, not this fixture's.
/// </summary>
[TestFixture]
public sealed class SensedProjectileBlockTest
{
    private const string RockProto = "TriadBlockTestRock";
    private const string ShotProto = "TriadBlockTestShot";
    private const int RecordId = 910_001;

    // The rock prototype exists so the query can re-roll the record's tile set from its blob
    // parameters; the shot is the minimal thing the block system's query matches. No physics on
    // purpose: nothing else may kill the projectile, so a deleted shot proves the block.
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
  id: {ShotProto}
  components:
  - type: Projectile
    damage:
      types:
        Blunt: 1
  - type: ShipWeaponProjectile
";

    /// <summary>
    ///     Spawns a shot at <paramref name="from"/>, gives the block system one tick to record
    ///     its position, teleports it to <paramref name="to"/>, and runs the sweep plus deletion
    ///     flush. Returns whether the shot survived the trip.
    /// </summary>
    private static async Task<bool> Fly(Content.IntegrationTests.Pair.TestPair pair, MapId mapId,
        Vector2 from, Vector2 to)
    {
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var xformSys = entManager.System<SharedTransformSystem>();

        EntityUid shot = default;
        await server.WaitPost(() => shot = entManager.SpawnEntity(ShotProto, new MapCoordinates(from, mapId)));
        await server.WaitRunTicks(1);

        await server.WaitPost(() => xformSys.SetWorldPosition(shot, to));
        await server.WaitRunTicks(3);
        await server.WaitIdleAsync();

        var alive = false;
        await server.WaitPost(() =>
        {
            alive = entManager.EntityExists(shot) && !entManager.IsQueuedForDeletion(shot);
            if (alive)
                entManager.DeleteEntity(shot);
        });

        return alive;
    }

    [Test]
    public async Task ShotStopsOnDormantRockAndOnlyThere()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();

        var map = await pair.CreateTestMap();
        var describe = server.System<CellDescribeSystem>();

        DebrisRecord record = null!;
        await server.WaitPost(() =>
        {
            cfg.SetCVar(Content.Shared._Triad.CCVar.TriadCCVars.WorldgenSensedEnabled, true);

            // Injected straight into the flat index, like the contact channel fixture: the test
            // map has no world controller, so the query's flat fallback serves it. Every blob
            // roll contains tile (0, 0), so a segment through the record centre at y = 0.5 in
            // local units is a guaranteed hit regardless of seed.
            record = new DebrisRecord
            {
                Id = RecordId,
                Map = map.MapUid,
                Point = new Vector2(50f, -0.5f),
                Proto = RockProto,
                Seed = 999,
                Shaped = true,
                Bound = 12f,
                State = SensedState.Dormant,
            };
            describe.Records[record.Id] = record;
        });

        // Through the rock: dies. Wide of the rock: lives. Through a ghost rock: lives, because
        // radar does not paint ghosts and an invisible wall is worse than a pass-through.
        var throughRock = await Fly(pair, map.MapId, new Vector2(30f, 0f), new Vector2(70f, 0f));
        var wideMiss = await Fly(pair, map.MapId, new Vector2(30f, 30f), new Vector2(70f, 30f));

        await server.WaitPost(() => record.BlockedAttempts = DebrisRecord.MaxBlockedAttempts);
        var throughGhost = await Fly(pair, map.MapId, new Vector2(30f, 0f), new Vector2(70f, 0f));

        Assert.Multiple(() =>
        {
            Assert.That(throughRock, Is.False,
                "a ship projectile flew through a described rock; the data-space block never fired");
            Assert.That(wideMiss, Is.True,
                "a projectile nowhere near the rock was blocked; the coarse gate or the walk is wrong");
            Assert.That(throughGhost, Is.True,
                "a ghost rock blocked a shot; rocks filtered off radar must not be invisible walls");
        });

        await server.WaitPost(() =>
        {
            describe.Records.Remove(RecordId);
            entManager.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }
}
