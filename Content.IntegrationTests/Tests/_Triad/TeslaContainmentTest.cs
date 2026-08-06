using System.Numerics;
using System.Threading.Tasks;
using Content.Server.Physics.Controllers;
using Content.Shared.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// Regression cover for the tesla escaping a cage whose interior is narrower than the ball.
///
/// ChaoticJump teleports 5-10 tiles and ignores physics, so containment rests entirely on the
/// probes in <see cref="ChaoticJumpSystem"/> shortening the jump. The swept-footprint probe
/// reports NO hit when it starts already overlapping, and in a one-tile-wide cage the ball's
/// 0.55 radius overlaps the fields on both sides permanently, so that probe is blind on every
/// single jump. Without the centre-line ray backing it up the ball leaves on the first jump.
/// </summary>
[TestOf(typeof(ChaoticJumpSystem))]
public sealed class TeslaContainmentTest
{
    /// <summary>Each cage interior is a single tile column spanning y=0..2.</summary>
    private const int InteriorYMin = 0;
    private const int InteriorYMax = 2;

    /// <summary>Independent cages per run, and the tile pitch between them.</summary>
    private const int CageCount = 16;
    private const int CageStride = 8;

    /// <summary>
    /// Generous slack so ordinary contact jitter against the fields cannot flake the test.
    /// A failed jump lands 5-10 tiles out, which clears this by a wide margin.
    /// </summary>
    private const float Slack = 1.0f;

    /// <summary>
    /// Diagnostic: does a probe built exactly like the one in ChaoticJumpSystem actually see a
    /// containment field? If this fails, no amount of probe logic in the jump can hold the cage.
    /// </summary>
    [Test]
    public async Task ProbesDetectContainmentField()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var mapSys = entMan.System<SharedMapSystem>();
        var rayCast = entMan.System<RayCastSystem>();
        var physics = entMan.System<SharedPhysicsSystem>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();

        var testMap = await pair.CreateTestMap();
        var ball = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            var plating = new Tile(tileDefs["Plating"].TileId);
            for (var y = 0; y <= 2; y++)
                mapSys.SetTile(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(0, y), plating);

            // One field two tiles to the +X side of the ball.
            mapSys.SetTile(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(2, 1), plating);
            entMan.SpawnEntity("ContainmentField",
                mapSys.GridTileToLocal(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(2, 1)));

            ball = entMan.SpawnEntity("TeslaEnergyBall",
                mapSys.GridTileToLocal(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(0, 1)));
        });

        server.RunTicks(10);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var origin = physics.GetPhysicsTransform(ball);
            var filter = new QueryFilter
            {
                MaskBits = (int) CollisionGroup.Impassable,
                IsIgnored = e => e == ball,
            };

            // Straight at the field, far enough to reach it.
            var translation = new Vector2(5f, 0f);

            var mapId = entMan.GetComponent<TransformComponent>(ball).MapID;

            var ray = rayCast.CastRayClosest(mapId, origin.Position, translation, filter);
            var shape = rayCast.CastShape(mapId, new PhysShapeCircle(0.55f), origin,
                translation, filter, RayCastSystem.RayCastClosestCallback);

            Assert.Multiple(() =>
            {
                Assert.That(ray.Hit, Is.True, "Centre-line ray did not see the containment field.");
                Assert.That(shape.Hit, Is.True, "Swept footprint did not see the containment field.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BallCannotJumpOutOfOneWideCage()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();

        var physics = entMan.System<SharedPhysicsSystem>();
        var testMap = await pair.CreateTestMap();
        var balls = new EntityUid[CageCount];

        await server.WaitPost(() =>
        {
            var plating = new Tile(tileDefs["Plating"].TileId);

            // A fleet of independent cages on one grid. Jumps are random, and a single cage only
            // gets ~5 of them in 40 s of game time, which is far too thin to trust. Every cage
            // steps on the same ticks, so this multiplies jumps per run without multiplying
            // runtime.
            for (var i = 0; i < CageCount; i++)
            {
                var bx = i * CageStride;

                // Interior is the single tile column (bx+1, 0..2), ringed by fields.
                for (var x = bx; x <= bx + 2; x++)
                {
                    for (var y = InteriorYMin - 1; y <= InteriorYMax + 1; y++)
                    {
                        mapSys.SetTile(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(x, y), plating);

                        var isInterior = x == bx + 1 && y >= InteriorYMin && y <= InteriorYMax;
                        if (isInterior)
                            continue;

                        var coords = mapSys.GridTileToLocal(
                            testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(x, y));
                        entMan.SpawnEntity("ContainmentField", coords);
                    }
                }

                var centre = mapSys.GridTileToLocal(
                    testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(bx + 1, InteriorYMin + 1));
                var ball = entMan.SpawnEntity("TeslaEnergyBall", centre);

                // The ball is 1.1 tiles across, so in a one-wide cage it permanently overlaps the
                // fields and the solver sits in penetration resolution, which can eject it on its
                // own. Pinning the body static removes every source of motion except ChaoticJump,
                // so any displacement below is unambiguously a teleport rather than physics.
                physics.SetBodyType(ball, BodyType.Static);
                balls[i] = ball;
            }
        });

        server.RunTicks(30);
        await server.WaitIdleAsync();

        // Jumps fire every 8-15 s, so 40 s of game time is several attempts per cage.
        var escapePos = (Vector2?) null;
        var escapedCage = -1;
        for (var step = 0; step < 40 && escapePos == null; step++)
        {
            server.RunTicks(60);
            await server.WaitIdleAsync();

            await server.WaitPost(() =>
            {
                for (var i = 0; i < CageCount && escapePos == null; i++)
                {
                    // Tile (x,y) spans world [x, x+1], so cage i's interior is
                    // x in [bx+1, bx+2], y in [0, 3].
                    var bx = i * CageStride;
                    var min = new Vector2(bx + 1 - Slack, InteriorYMin - Slack);
                    var max = new Vector2(bx + 2 + Slack, InteriorYMax + 1 + Slack);

                    var pos = xformSys.GetWorldPosition(balls[i]);
                    if (pos.X < min.X || pos.X > max.X || pos.Y < min.Y || pos.Y > max.Y)
                    {
                        escapePos = pos;
                        escapedCage = i;
                    }
                }
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(escapePos, Is.Null,
                $"Tesla ball in cage {escapedCage} teleported out to {escapePos}. The body is static, " +
                "so this can only be ChaoticJump: containment must hold even when the swept-footprint " +
                "probe is blinded by starting contact.");
        });

        await pair.CleanReturnAsync();
    }
}
