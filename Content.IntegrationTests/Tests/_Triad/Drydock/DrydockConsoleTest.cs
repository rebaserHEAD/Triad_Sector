#nullable enable

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Containers.ItemSlots;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The console tier: the half a player actually touches. Everything else in this folder proves
    /// the pipeline moves a ship correctly; nothing until now proved a person standing at a console
    /// can reach it, or that the console refuses the people it should.
    ///
    /// <para>These need a connected pair, because the console resolves the operator's account from
    /// a real player session and the default pooled pair has none.</para>
    ///
    /// <para>The operator entity is deliberately spawned well off the ship. A session-bearing mob
    /// standing on the grid trips the organics gate, which is that gate working rather than a
    /// problem with the test.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(ShipyardSystem))]
    public sealed class DrydockConsoleTest
    {
        /// <summary>
        /// The whole player-facing loop in one pass: store from the console, see the ship appear in
        /// the retrieve list, retrieve it, and get a working deed back.
        ///
        /// <para>The two deed assertions are the point. A store leaves the card pointing at a grid
        /// that no longer exists, so the deed has to come off; a retrieve produces a ship nobody
        /// holds a claim on, so a fresh one has to be minted. Get either backwards and the ship is
        /// either unflyable or the card is a handle on nothing.</para>
        /// </summary>
        [Test]
        public async Task AnOwnerCanStoreAndRetrieveFromTheConsole()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var drydockStore = server.ResolveDependency<DrydockStore>();

            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // The control for the store assertion below: the card has to be carrying a deed before
            // the store, or "the deed came off" proves nothing.
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "The console store resolves its ship from this deed; without it the test never reaches the pipeline.");
            });

            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(stored, Is.Not.Null, "A store by the ship's own owner must reach the pipeline rather than being refused at the console.");
            Assert.That(stored!.Value.Result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.True, "A successful store despawns the grid.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.False,
                    "The deed points at a grid that no longer exists, so the store has to strip it, as selling does.");
            });

            // Identity, never counts. Pooled pairs share one database, so ships filed by earlier
            // tests in this run are still on this account and any assertion on list length is
            // really an assertion about test ordering.
            var shipId = stored.Value.ShipId!.Value;

            // Separates "the store filed nothing under this account" from "the console dropped it":
            // if this row is here and the tab does not show it, the fault is the console's filter.
            var rows = await RunOnServer(pair, () => drydockStore.GetShipsByOwner(session.UserId.UserId));
            var filed = rows.SingleOrDefault(r => r.ShipGuid == shipId);
            Assert.That(filed, Is.Not.Null,
                "The store must file the ship under the operating player's own account, or nothing downstream can find it.");
            Assert.That(filed!.State, Is.EqualTo(DrydockShipState.Stored),
                "A ship that has just been put away has to be in the state the console lists on.");
            Assert.That(filed.Investigating, Is.False);

            // The list the drydock tab renders. It is filled by an awaited database read, so it is
            // the console's own view of what this player may retrieve.
            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                var listed = consoleComp.CachedStoredShips.SingleOrDefault(s => s.ShipId == shipId);
                Assert.That(listed, Is.Not.Null,
                    "The ship was just stored by this operator, so it has to be offered back to them.");
                Assert.That(listed!.Name, Is.EqualTo("Kestrel"));
            });

            var retrieved = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));

            Assert.That(retrieved, Is.Not.Null, "The owner must be able to take back the ship they just put away.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "A retrieved ship nobody holds a claim on is unflyable; retrieve has to mint a fresh deed onto the card.");

                var deed = entMan.GetComponent<ShuttleDeedComponent>(card);
                Assert.That(deed.ShuttleUid, Is.EqualTo(retrieved!.Value),
                    "The minted deed has to point at the ship that actually came back.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The duplicate gate, which is the one thing here that must never come loose.
        ///
        /// <para>A retrieved ship's row moves to <see cref="DrydockShipState.CheckedOut"/> and
        /// nothing returns it, so the console must neither list it nor retrieve it a second time.
        /// The inherited one-ship-per-card rule looks like it covers this and does not: a card deed
        /// is not cleared when a grid dies, so a player who unloads cargo and lets the hull go
        /// would otherwise retrieve the same revision again and have the cargo twice. The row state
        /// is what actually closes that, and this test is what says so.</para>
        ///
        /// <para>Deliberately asked with a <em>clean</em> card. Refusing because the card is full
        /// would pass this test while proving the wrong gate, so the deed is stripped first and the
        /// refusal that remains can only be the row's.</para>
        /// </summary>
        [Test]
        public async Task ACheckedOutShipCannotBeRetrievedTwice()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));

            // By id, not by position: pooled pairs share a database, so this account may already
            // carry ships filed by other tests in the same run.
            var shipId = stored!.Value.ShipId!.Value;

            await pair.RunTicksSync(5);

            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(consoleComp.CachedStoredShips.Any(s => s.ShipId == shipId), Is.True,
                    "The control: a stored ship is listed, so its later absence means something.");
            });

            var first = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(first, Is.Not.Null, "The control: the first retrieve has to succeed, or the second proves nothing.");

            await pair.RunTicksSync(5);

            // Clear the card, so what refuses below is the row and not card capacity.
            await server.WaitPost(() => entMan.RemoveComponent<ShuttleDeedComponent>(card));
            await pair.RunTicksSync(2);

            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                // A ship that is out stays on the list so its owner can see why a berth is empty,
                // but it is listed as out, which is what disables its retrieve button. The row
                // state below is what actually refuses; this is the console telling the truth.
                var listed = consoleComp.CachedStoredShips.SingleOrDefault(s => s.ShipId == shipId);
                Assert.That(listed, Is.Not.Null, "A ship that is out is still the player's ship and still on their list.");
                Assert.That(listed!.State, Is.EqualTo(nameof(DrydockShipState.CheckedOut)),
                    "The list must say the ship is out, or the tab would offer a retrieve that the row refuses.");
            });

            var second = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));

            Assert.That(second, Is.Null,
                "Retrieving a checked-out ship a second time would duplicate it and everything aboard. The row state is the only thing standing in the way of that, since a card deed is never cleared when a grid dies.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Garage ownership follows the ship's stamped account, never the card in the slot. Cards
        /// get lent and stolen, and a deed is a claim on flying a ship rather than on filing it
        /// away, so a borrowed one must not let somebody put another person's ship into storage.
        /// </summary>
        [Test]
        public async Task ABorrowedDeedCannotStoreSomeoneElsesShip()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            // The ship belongs to somebody who is not at the console.
            var absentOwner = new Robust.Shared.Network.NetUserId(Guid.NewGuid());
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, absentOwner);

            var result = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(result, Is.Null,
                "The console must refuse before the pipeline is entered: holding the deed is not the same as owning the ship.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.False, "A refused store must leave the ship flying.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "A refused store must not strip the deed off a card it had no business acting on.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A transfer is bound to the player, not the card. The offer needs a character with a
        /// mind behind the click and a session that owns the row; the accept needs a different
        /// session. The operator this harness spawns has no mind, which is the control: the same
        /// click that is refused for a bare entity is accepted once a mind is attached.
        /// </summary>
        [Test]
        public async Task ATransferOfferIsBoundToTheOfferingPlayer()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();
            var mindSys = server.System<Content.Server.Mind.MindSystem>();

            var session = playerMan.Sessions.First();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            var shipId = stored!.Value.ShipId!.Value;
            await pair.RunTicksSync(5);

            // No mind yet: a card in a console is not a person at a console.
            var unverified = await RunOnServer(pair,
                () => shipyard.TryOfferTransfer(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.Multiple(() =>
            {
                Assert.That(unverified, Is.False, "An operator with no mind cannot offer a ship, whatever card is in the slot.");
                Assert.That(consoleComp.PendingTransfer, Is.Null);
            });

            await server.WaitPost(() =>
            {
                var mind = mindSys.CreateMind(session.UserId, "Operator");
                mindSys.TransferTo(mind, operatorEnt);
            });
            await pair.RunTicksSync(2);

            var offered = await RunOnServer(pair,
                () => shipyard.TryOfferTransfer(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(offered, Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(consoleComp.PendingTransfer, Is.Not.Null);
                Assert.That(consoleComp.PendingTransfer!.ShipId, Is.EqualTo(shipId));
                Assert.That(consoleComp.PendingTransfer.OwnerUserId, Is.EqualTo(session.UserId.UserId));
            });

            // The offerer's own session cannot complete the handshake.
            var selfAccept = await RunOnServer(pair,
                () => shipyard.TryAcceptTransfer(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(selfAccept, Is.False, "Accepting your own offer would be a no-op transfer at best; it is refused outright.");
            Assert.That((await store.LoadCurrent(shipId))!.Ship.OwnerUserId, Is.EqualTo(session.UserId.UserId));
            Assert.That(consoleComp.PendingTransfer, Is.Not.Null, "A refused accept leaves the offer standing for the right person.");

            var cancelled = await RunOnServer(pair,
                () => shipyard.TryCancelTransfer(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(cancelled, Is.True);
            Assert.That(consoleComp.PendingTransfer, Is.Null);

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Builds a station, a ship stamped to <paramref name="shipOwner"/>, and a console holding a
        /// deed card for it, with the operator standing clear of the grid.
        /// </summary>
        private static async Task<(EntityUid Station, EntityUid StationGrid, EntityUid Ship, EntityUid Console, ShipyardConsoleComponent Comp, EntityUid Card, EntityUid Operator)>
            BuildConsoleAndShip(TestPair pair, Robust.Shared.Network.NetUserId shipOwner)
        {
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();
            var itemSlots = server.System<ItemSlotsSystem>();
            var metaData = server.System<MetaDataSystem>();
            var transform = server.System<SharedTransformSystem>();

            var map = await pair.CreateTestMap();
            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            EntityUid station = default;
            EntityUid ship = default;
            EntityUid console = default;
            EntityUid card = default;
            EntityUid operatorEnt = default;
            ShipyardConsoleComponent comp = default!;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                shipyard.SetupShipyardIfNeeded();

                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);

                var shipGrid = mapSys.CreateGridEntity(map.MapId);
                ship = shipGrid.Owner;
                var tile = new Tile(1);
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        mapSys.SetTile(ship, shipGrid.Comp, new Vector2i(x, y), tile);
                    }
                }

                // Clear of the station's grid, and it has to be done by moving the ship rather than
                // by placing the console carefully. Both grids are created at the origin, and grid
                // traversal reparents an entity to whichever grid it is physically over, so a
                // console placed on the station grid inside the ship's footprint silently becomes
                // part of the ship and is despawned along with it by the very store being tested.
                transform.SetWorldPosition(ship, new Vector2(100f, 100f));

                entMan.EnsureComponent<ShuttleComponent>(ship);
                metaData.SetEntityName(ship, "Kestrel");
                entMan.EnsureComponent<ShipOwnershipComponent>(ship).OwnerUserId = shipOwner;

                // Well clear of both grids. An operator standing aboard the ship would trip the
                // organics gate, and the card would go into storage with it.
                operatorEnt = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(64f, 64f), map.MapId));
                playerMan.SetAttachedEntity(session, operatorEnt);

                // On the station's own grid, not loose on the map. Retrieve resolves where to put
                // the ship from the console's owning station, and a console parented to nothing
                // belongs to no station, so an off-grid console refuses every retrieve.
                console = entMan.SpawnEntity(null, new EntityCoordinates(map.Grid.Owner, new Vector2(0.5f, 0.5f)));
                comp = entMan.EnsureComponent<ShipyardConsoleComponent>(console);

                card = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(64f, 64f), map.MapId));
                shipyard.MintCardDeed(card, ship, operatorEnt);
                itemSlots.TryInsert(console, comp.TargetIdSlot, card, user: null);
            });

            await pair.RunTicksSync(5);
            return (station, map.Grid.Owner, ship, console, comp, card, operatorEnt);
        }

        private static async Task<T> RunOnServer<T>(TestPair pair, Func<Task<T>> start)
        {
            Task<T>? task = null;
            await pair.Server.WaitPost(() => task = start());

            for (var i = 0; i < 600 && !task!.IsCompleted; i++)
            {
                await pair.RunTicksSync(1);
            }

            Assert.That(task!.IsCompleted, Is.True, "A drydock console operation never completed.");
            return await task;
        }
    }
}
