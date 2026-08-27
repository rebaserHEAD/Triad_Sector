#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Wires;
using Content.Shared._Triad.CCVar;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Research.Components;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The first thing that ever moves a ship through the drydock. Everything either pipeline half
    /// does is unproven until this runs: the six Revive steps, both sidecars, the validation
    /// backstop, the manifest, and the claim.
    ///
    /// <para>It builds a ship rather than loading a roster vessel on purpose. A hand-built grid
    /// fails for one reason at a time, which is what you want from the test that establishes the
    /// round trip at all. The roster sweep is the separate test that answers whether real content
    /// survives, and breadth is its job rather than this one's.</para>
    ///
    /// <para>The airlock is not decoration. Its wire layout is the sharpest available probe of the
    /// map-init boundary: <c>WiresComponent.WiresList</c> is not a data field and the only thing
    /// that ever builds it is the map-init handler, which never fires again for a restored entity.
    /// Without the Revive step every panel on a retrieved ship opens empty, and nothing else about
    /// the ship looks wrong.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRoundTripTest
    {
        private const string AirlockProtoId = "Airlock";
        private const string ResearchServerProtoId = "ResearchAndDevelopmentServer";
        private const string LatheProtoId = "Protolathe";

        [Test]
        public async Task AShipStoredComesBackWithItsContentsAndItsWires()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);

            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            // What the ship is made of, before it goes anywhere. Compared against the same census
            // afterwards this catches a drop, a duplicate, and a substitution that keeps the count
            // the same.
            var before = await CensusGrid(pair, shipGrid);
            Assert.That(before.Values.Sum(), Is.GreaterThan(0), "The test ship has to actually carry something.");

            var wiresBefore = await ReadWireCount(pair, airlock);
            Assert.That(wiresBefore, Is.GreaterThan(0),
                "A live airlock must have a populated wire layout, or this test cannot prove Revive rebuilt one.");

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
                Assert.That(shipId, Is.Not.Null, "A successful store names the hull it filed.");
            });

            await pair.RunTicksSync(5);
            Assert.That(entMan.Deleted(shipGrid), Is.True,
                "The grid is despawned only after the document is filed, so a live grid here means a half-committed store.");

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null, "The ship went in, so it has to come out.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<DrydockIdentityComponent>(retrieved!.Value, out var identity), Is.True,
                    "Identity is the one piece of state nothing else on the grid can reconstruct.");
                Assert.That(identity!.ShipId, Is.EqualTo(shipId!.Value),
                    "A retrieve must return the same hull, not a new one that looks similar.");
            });

            var after = await CensusGrid(pair, retrieved!.Value);
            Assert.That(after, Is.EqualTo(before),
                "Every prototype aboard comes back, exactly once each. A difference is a drop, a duplicate or a substitution.");

            // The map-init boundary, made concrete. This is the assertion the whole census on the
            // wiki exists to justify.
            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved.Value);
            Assert.That(retrievedAirlock, Is.Not.Null, "The airlock came back, or the census above would have failed.");

            var wiresAfter = await ReadWireCount(pair, retrievedAirlock!.Value);
            Assert.That(wiresAfter, Is.EqualTo(wiresBefore),
                "WiresList is not a data field, so this passes only because Revive rebuilt the layout by hand.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Damage is the second reason a ship needs a fidelity layer at all, and it is a different
        /// reason from the first. <c>DamageableComponent.Damage</c> is not unserializable, it is
        /// declared read-only to the serializer, so it is never written and a shot-up hull comes
        /// back pristine. That is a free repair on every combat vessel in a fork whose ships get
        /// shot at, which is why the sidecar carries the raw damage dictionary across.
        /// </summary>
        [Test]
        public async Task DamageSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var damageSys = server.System<DamageableSystem>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);

            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            FixedPoint2 damageBefore = default;

            await server.WaitPost(() =>
            {
                var blunt = protoMan.Index<DamageTypePrototype>("Blunt");
                var specifier = new DamageSpecifier(blunt, FixedPoint2.New(37));
                damageSys.TryChangeDamage(airlock, specifier, ignoreResistances: true);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                damageBefore = entMan.GetComponent<DamageableComponent>(airlock).TotalDamage;
                Assert.That(damageBefore, Is.GreaterThan(FixedPoint2.Zero),
                    "The control: the airlock has to actually be damaged, or the comparison after the round trip proves nothing.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved!.Value);
            Assert.That(retrievedAirlock, Is.Not.Null);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<DamageableComponent>(retrievedAirlock!.Value).TotalDamage,
                    Is.EqualTo(damageBefore),
                    "Damage is read-only to the serializer, so this passes only because the sidecar carried it and the rehydrate pass applied it.");

                Assert.That(entMan.HasComponent<DrydockDamageSidecarComponent>(retrievedAirlock.Value), Is.False,
                    "The sidecar is scaffolding for the crossing. Leaving it aboard would re-apply the same damage on the next retrieve.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The second of the three Revive steps the map-init census added. Device network
        /// membership is registration held by the network system rather than state on the device,
        /// and joining happens on map init, which never fires again for a restored entity. Without
        /// the step a retrieved ship's alarms, sensors and consoles come back present, powered, and
        /// deaf: nothing about them looks wrong from the outside.
        /// </summary>
        [Test]
        public async Task DeviceNetworkMembershipComesBack()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var deviceNet = server.System<DeviceNetworkSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);

            // The airlock the wires assertion uses carries DeviceNetwork too, so one entity covers
            // both steps.
            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            await server.WaitAssertion(() =>
            {
                var device = entMan.GetComponent<DeviceNetworkComponent>(airlock);
                Assert.That(deviceNet.IsDeviceConnected(airlock, device), Is.True,
                    "The control: a live airlock has to be on its network, or the check after the round trip means nothing.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved!.Value);
            Assert.That(retrievedAirlock, Is.Not.Null);

            await server.WaitAssertion(() =>
            {
                var device = entMan.GetComponent<DeviceNetworkComponent>(retrievedAirlock!.Value);
                Assert.That(deviceNet.IsDeviceConnected(retrievedAirlock.Value, device), Is.True,
                    "Membership is not serialized state, so this passes only because Revive re-ran the join by hand.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The third. A research client's link to its server is a plain property on one side and a
        /// view-variables list on the other, so neither end serializes, and the only thing that
        /// ever sets it is a map-init scan of the client's own grid. A retrieved ship carrying its
        /// own R&amp;D server would have every lathe disconnected from it until somebody opened the
        /// server-selection menu by hand.
        /// </summary>
        [Test]
        public async Task ResearchClientsComeBackRegistered()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            EntityUid lathe = default;

            await server.WaitPost(() =>
            {
                entMan.SpawnEntity(ResearchServerProtoId, new EntityCoordinates(shipGrid, new Vector2(0f, 0f)));
                lathe = entMan.SpawnEntity(LatheProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<ResearchClientComponent>(lathe).Server, Is.Not.Null,
                    "The control: the lathe has to find its server while the ship is live, which is the map-init scan doing its job.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            var retrievedLathe = await FindChildWithComponent<ResearchClientComponent>(pair, retrieved!.Value);
            Assert.That(retrievedLathe, Is.Not.Null, "The lathe came back with the ship.");

            await server.WaitAssertion(() =>
            {
                var client = entMan.GetComponent<ResearchClientComponent>(retrievedLathe!.Value);
                Assert.That(client.Server, Is.Not.Null,
                    "Neither end of the registration is a data field, so this passes only because Revive re-ran the grid scan.");

                Assert.That(entMan.HasComponent<ResearchServerComponent>(client.Server!.Value), Is.True,
                    "And it has to be pointed at a real server, not merely non-null.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A three-by-three plated grid carrying one airlock, plus a station to dock it at. The
        /// tiles are laid before anything is spawned on them: a spawn at grid-local coordinates
        /// that are not on a set tile silently reparents to the map, and a grid census then reads
        /// the wrong parent.
        /// </summary>
        private static async Task<(EntityUid Station, EntityUid ShipGrid, EntityUid Airlock)> BuildShipAndStation(TestPair pair)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var map = await pair.CreateTestMap();

            EntityUid station = default;
            EntityUid shipGrid = default;
            EntityUid airlock = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);

                // Retrieve refuses without a staging map, and nothing in a test pair creates one.
                shipyard.SetupShipyardIfNeeded();

                // The dock target. As far as the retrieve gate is concerned a station is a
                // StationData component with a grid in it, which is what GetLargestGrid reads.
                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);

                // The ship, on the same map but its own grid, so storing it cannot disturb the
                // dock target.
                var ship = mapSys.CreateGridEntity(map.MapId);
                shipGrid = ship.Owner;

                var tile = new Tile(1);
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        mapSys.SetTile(ship.Owner, ship.Comp, new Vector2i(x, y), tile);
                    }
                }

                entMan.EnsureComponent<ShuttleComponent>(shipGrid);
                entMan.System<MetaDataSystem>().SetEntityName(shipGrid, "Kestrel");

                airlock = entMan.SpawnEntity(AirlockProtoId, new EntityCoordinates(shipGrid, new Vector2(1f, 1f)));
            });

            await pair.RunTicksSync(5);

            return (station, shipGrid, airlock);
        }

        /// <summary>
        /// Starts a server-side async operation on the game thread and pumps the pair until it
        /// finishes. Both pipelines await database work, so the continuation has to come back to a
        /// ticking server; awaiting the task from the test thread alone would never let it resume.
        /// </summary>
        private static async Task<T> RunOnServer<T>(TestPair pair, Func<Task<T>> start)
        {
            Task<T>? task = null;
            await pair.Server.WaitPost(() => task = start());

            for (var i = 0; i < 600 && !task!.IsCompleted; i++)
            {
                await pair.RunTicksSync(1);
            }

            Assert.That(task!.IsCompleted, Is.True,
                "The drydock operation never completed: either it is blocked on the database, or a continuation never came back to the game thread.");

            return await task;
        }

        /// <summary>
        /// Every entity parented under the grid, counted per prototype. Recursive, because the
        /// interesting losses live inside containers rather than on the floor.
        /// </summary>
        private static async Task<Dictionary<string, int>> CensusGrid(TestPair pair, EntityUid grid)
        {
            var census = new Dictionary<string, int>();
            var entMan = pair.Server.EntMan;

            await pair.Server.WaitPost(() =>
            {
                var stack = new Stack<EntityUid>();
                stack.Push(grid);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();

                    var children = entMan.GetComponent<TransformComponent>(current).ChildEnumerator;
                    while (children.MoveNext(out var child))
                    {
                        var proto = entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID ?? "<no prototype>";
                        census[proto] = census.GetValueOrDefault(proto) + 1;
                        stack.Push(child);
                    }
                }
            });

            return census;
        }

        private static async Task<int> ReadWireCount(TestPair pair, EntityUid uid)
        {
            var count = -1;
            await pair.Server.WaitPost(() =>
            {
                count = pair.Server.EntMan.GetComponent<WiresComponent>(uid).WiresList.Count;
            });
            return count;
        }

        private static async Task<EntityUid?> FindChildWithComponent<T>(TestPair pair, EntityUid grid) where T : IComponent
        {
            EntityUid? found = null;
            var entMan = pair.Server.EntMan;

            await pair.Server.WaitPost(() =>
            {
                var children = entMan.GetComponent<TransformComponent>(grid).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    if (!entMan.HasComponent<T>(child))
                        continue;

                    found = child;
                    return;
                }
            });

            return found;
        }

        /// <summary>
        /// The owner column is a real foreign key, so a ship cannot be filed for a player who does
        /// not exist.
        /// </summary>
        private static Task InsertPlayer(IServerDbManager db, Guid userId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                context.Player.Add(new Player
                {
                    UserId = userId,
                    LastSeenUserName = $"drydock-roundtrip-{userId:N}",
                    FirstSeenTime = DateTime.UtcNow,
                    LastSeenTime = DateTime.UtcNow,
                    LastSeenAddress = IPAddress.Loopback,
                });

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }
    }
}
