#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Station.Components;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A store that fails must leave the ship exactly as usable as it was. The unwind owes a ship
    /// taken off a private paused map, thawed, and flown back to the station, with the private map
    /// scrapped behind it; skipping any of those leaves a ship that is gone, or frozen, or parked
    /// somewhere no player can reach. The image write reads the live hull without changing it, so
    /// the components the ship carried, its station membership among them, have to be exactly as they
    /// were. <see cref="AStoreCancelledMidSliceHandsTheShipBackWhole"/> puts the abort in the middle
    /// of the sliced image write.
    ///
    /// <para>The first failure is induced through the round foreign key rather than by patching the
    /// system under test. Filing a revision against a round that has no row throws inside
    /// <c>FileRevision</c>, after the whole image is written, so the unwind is exercised by a fault
    /// the database really produces rather than by a seam opened for the test. It is not the owner
    /// foreign key, because the capacity gate reads the owner's berths before the pipeline changes
    /// anything: an owner with no row has no berths, and that refusal happens before there is
    /// anything to unwind.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockAbortTest
    {
        /// <summary>
        /// A round id no round row carries, so filing a revision against it violates the foreign
        /// key and throws where the test needs it to.
        /// </summary>
        private const int NoSuchRound = 987654321;

        [Test]
        public async Task AFailedStoreLeavesTheShipUsable()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            var store = server.ResolveDependency<DrydockStore>();
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var fixture = await BuildAbortFixture(pair, sliced: false);

            var revisionsBefore = await CountRevisions(db);

            // The induced fault logs at error level, and a pooled pair fails its return on any such
            // log. Lift the bar only for the span that is supposed to produce one, then put it back,
            // so an unexpected error from anywhere else in this test still fails it.
            var failureLevel = pair.ServerLogHandler.FailureLevel;
            pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;

            // The store must fail, and it must fail loudly rather than reporting a refusal reason:
            // a database fault is not one of the outcomes the enum models.
            Task<(DrydockStoreResult Result, Guid? ShipId)>? storeTask = null;
            await server.WaitPost(() => storeTask = drydock.TryStoreShip(
                fixture.Ship, owner, NoSuchRound, stationUid: fixture.HostStation));

            // Wall clock, not ticks: with the budget off the store's remaining suspensions are
            // thread-pool hops, and a tick ceiling drains long before they land.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!storeTask!.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await pair.RunTicksSync(1);
            }

            await pair.RunTicksSync(5);
            pair.ServerLogHandler.FailureLevel = failureLevel;

            Assert.That(storeTask!.IsCompleted, Is.True, "The store never completed.");
            Assert.That(storeTask.IsFaulted, Is.True,
                "Filing a revision against a round that does not exist must violate the round foreign key.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() => AssertShipCameBackWhole(pair, fixture, "after a database fault"));

            Assert.That(await CountRevisions(db), Is.EqualTo(revisionsBefore),
                "A store that aborts files nothing. A revision here would be a half-committed store, which is the failure this whole ordering exists to prevent.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The abort a sliced store made possible. With the pipeline spread over ticks there is a real
        /// window in which the ship is frozen on a private map with its image half written, and the
        /// world can move under it: an admin, a round restart, a shutdown, or the slice watchdog can
        /// all cancel it there.
        ///
        /// <para>The cancel is timed off the pipeline rather than off a tick count: the test cancels
        /// on the tick after the image write's phase opens, so the write is under way when the
        /// cancellation lands.</para>
        /// </summary>
        [Test]
        public async Task AStoreCancelledMidSliceHandsTheShipBackWhole()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            var store = server.ResolveDependency<DrydockStore>();
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var fixture = await BuildAbortFixture(pair, sliced: true);

            var revisionsBefore = await CountRevisions(db);

            // A real round id, so nothing about this store would refuse on its own. The cancel is
            // the only reason it stops, which is what makes the outcome below mean something.
            var writeOpened = false;
            void OnProgress(int percent, DrydockPhase phase)
            {
                if (phase == DrydockPhase.Serialize)
                    writeOpened = true;
            }

            Task<(DrydockStoreResult Result, Guid? ShipId)>? storeTask = null;
            await server.WaitPost(() => storeTask = drydock.TryStoreShip(
                fixture.Ship, owner, null, stationUid: fixture.HostStation, onProgress: OnProgress));

            // Cancelling logs at error level on purpose: a pipeline abandoned mid-flight is
            // something an administrator should see in the log. Lift the bar across the cancel and
            // the unwind, and put it back before anything is asserted.
            var failureLevel = pair.ServerLogHandler.FailureLevel;
            pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;

            var cancelledAtTick = -1;
            for (var i = 0; i < 600 && cancelledAtTick < 0; i++)
            {
                await pair.RunTicksSync(1);

                if (!writeOpened)
                    continue;

                cancelledAtTick = i;
                await server.WaitPost(() => drydock.CancelAllJobs("a mid-slice abort, induced by DrydockAbortTest"));
            }

            // Wall clock, not ticks: with the budget off the store's remaining suspensions are
            // thread-pool hops, and a tick ceiling drains long before they land.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!storeTask!.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await pair.RunTicksSync(1);
            }

            await pair.RunTicksSync(10);
            pair.ServerLogHandler.FailureLevel = failureLevel;

            Assert.Multiple(() =>
            {
                // Without this the test can pass by never reaching the write at all, cancelling
                // nothing and asserting that an untouched ship is untouched.
                Assert.That(cancelledAtTick, Is.GreaterThanOrEqualTo(0),
                    "The control: the image write never opened, so the cancel never landed on a store in flight.");
                Assert.That(storeTask!.IsCompleted, Is.True, "The cancelled store never finished unwinding.");
                Assert.That(storeTask.IsFaulted, Is.False,
                    "A cancellation is an outcome, not a fault. Letting it escape as an exception would reach Job.ProcessWrap, which logs an error for every one.");
            });

            var (result, _) = await storeTask!;
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Cancelled),
                "A cancelled store reports itself as cancelled rather than as a refusal the player could act on.");

            await server.WaitAssertion(() => AssertShipCameBackWhole(pair, fixture, "after a mid-slice cancellation"));

            Assert.That(await CountRevisions(db), Is.EqualTo(revisionsBefore),
                "Cancelled before the commit means nothing is filed, so a revision here would be a ship that is both flying and stored.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Everything a failed store owes the ship, in one place, because both abort paths owe
        /// exactly the same things and a check that lives in only one of them is a check that will
        /// eventually only be true in one of them.
        /// </summary>
        private static void AssertShipCameBackWhole(TestPair pair, AbortFixture fixture, string when)
        {
            var entMan = pair.Server.EntMan;
            var mapSys = pair.Server.System<SharedMapSystem>();
            var ship = fixture.Ship;

            Assert.Multiple(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.False,
                    $"{when}: a failed store must never despawn the ship. The grid is only disposed of after the document is filed.");

                Assert.That(entMan.HasComponent<DrydockInProgressComponent>(ship), Is.False,
                    $"{when}: the marker is the store's re-entrancy sentinel, and a store refuses outright while it is on. Leaving it behind makes this hull unstorable for the rest of the round.");

                Assert.That(entMan.HasComponent<ShipRepairDataComponent>(ship), Is.True,
                    $"{when}: the store reads the live hull without taking components off it.");

                Assert.That(entMan.HasComponent<StationMemberComponent>(ship), Is.True,
                    $"{when}: station membership stays on the live hull.");

                Assert.That(entMan.GetComponent<StationDataComponent>(fixture.ShipStation).Grids, Does.Contain(ship),
                    $"{when}: and the station still lists the grid in its own set.");

                Assert.That(entMan.HasComponent<ShuttleComponent>(ship), Is.True, $"{when}: the ship still has to be a ship.");
            });

            // Where the ship ended up. The store moves it onto a private paused map before it
            // touches anything, so an unwind that restores every component and forgets the ship is
            // still parked there hands the player a perfect ship they can never reach.
            var mapUid = entMan.GetComponent<TransformComponent>(ship).MapUid;
            Assert.Multiple(() =>
            {
                Assert.That(mapUid, Is.Not.Null, $"{when}: the ship is on a map at all.");
                Assert.That(entMan.HasComponent<DrydockStagingMapComponent>(mapUid!.Value), Is.False,
                    $"{when}: the ship is back in the world rather than left on the drydock's private staging map.");
                Assert.That(mapUid, Is.EqualTo(fixture.HostMap),
                    $"{when}: the unwind flies the ship back to the station it left, not to wherever it happened to be parked.");

                // IsPaused is MapPaused or not-yet-map-initialised. Either one freezes everything
                // aboard whatever the per-entity flags say.
                Assert.That(mapSys.IsPaused(mapUid!.Value), Is.False, $"{when}: the map the ship came back to is live.");
            });

            // The thaw, per entity. The freeze walks the tree, so the unwind has to walk it back,
            // and a walk that stops early leaves a ship that looks intact and cannot act.
            var frozen = new List<string>();
            var visited = 0;
            var stack = new Stack<EntityUid>();
            stack.Push(ship);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var meta = entMan.GetComponent<MetaDataComponent>(current);

                visited++;
                if (meta.EntityPaused)
                    frozen.Add($"{meta.EntityPrototype?.ID ?? "<no prototype>"} ({current})");

                var children = entMan.GetComponent<TransformComponent>(current).ChildEnumerator;
                while (children.MoveNext(out var child))
                    stack.Push(child);
            }

            Assert.Multiple(() =>
            {
                Assert.That(visited, Is.GreaterThan(1),
                    $"{when}: the control on the thaw walk - the fixture puts an airlock aboard, so a walk that only ever sees the grid is not looking at the ship.");
                Assert.That(frozen, Is.Empty,
                    $"{when}: {frozen.Count} entities came back still paused: {string.Join(", ", frozen.Take(10))}.");
            });

            // Nothing left behind. A staging map is process-local and nothing else in the game will
            // ever look at it again, so one leaked here is leaked until the process ends.
            var staging = new List<string>();
            var stagingQuery = entMan.AllEntityQueryEnumerator<DrydockStagingMapComponent>();
            while (stagingQuery.MoveNext(out var uid, out var comp))
                staging.Add($"{comp.Kind} ({uid})");

            Assert.That(staging, Is.Empty,
                $"{when}: {staging.Count} drydock staging map(s) survived the unwind: {string.Join(", ", staging)}. "
                + "A Stranded one here means the unwind could not find anywhere to put the ship, which is the failure the return leg exists to prevent.");
        }

        /// <summary>
        /// The world an aborted store needs: a host station with a grid of its own for the unwind to
        /// fly the ship back to, and a ship that is a station in its own right, the way every hull in
        /// this fork is, so the strip list has both of its entries to take.
        /// </summary>
        private static async Task<AbortFixture> BuildAbortFixture(TestPair pair, bool sliced)
        {
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var map = await pair.CreateTestMap();

            EntityUid hostStation = default;
            EntityUid shipStation = default;
            EntityUid shipGrid = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);

                // Slicing decides which abort this fixture can reach. At zero there is no job, so
                // the whole pipeline runs on the caller's async path and the only place it can be
                // interrupted is a fault. Above zero it suspends between phases, which is what gives
                // the mid-slice test a tick to cancel on.
                cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, sliced ? 2 : 0);

                shipyard.SetupShipyardIfNeeded();

                // The station the console would have resolved. The unwind needs somewhere to put the
                // ship back, and it re-resolves that from the station rather than from the ship,
                // which by then is on a private map with no neighbours.
                hostStation = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(hostStation);
                stationSys.AddGridToStation(hostStation, map.Grid.Owner);

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

                // Two components a store must leave on the live hull: the repair baseline, which the
                // image carries, and station membership, since ships in this fork are stations.
                entMan.EnsureComponent<ShipRepairDataComponent>(shipGrid);

                shipStation = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(shipStation);
                stationSys.AddGridToStation(shipStation, shipGrid);

                // Something aboard, so the freeze, the image write and the thaw all have a tree to
                // walk. A one-entity grid would let a walk that never recursed pass every assertion
                // here.
                entMan.SpawnEntity("Airlock", new EntityCoordinates(shipGrid, new Vector2(1f, 1f)));
            });

            // The host station stands on the test grid, which the fork's janitors are built to
            // delete, and the unwind needs somewhere to fly the ship back to.
            await pair.MakeCleanupImmune(map.Grid.Owner);

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entMan.HasComponent<StationMemberComponent>(shipGrid), Is.True,
                        "AddGridToStation is what puts the strip-list component on the grid; without it this test proves nothing.");
                    Assert.That(entMan.GetComponent<StationDataComponent>(shipStation).Grids, Does.Contain(shipGrid),
                        "The control for the re-book assertion: the grid has to be in the set before the store can remove it.");
                    Assert.That(entMan.GetComponent<StationDataComponent>(hostStation).Grids, Does.Contain(map.Grid.Owner),
                        "The control for the return leg: the host station has to own a grid, or the unwind has nothing to fly the ship back to.");
                });
            });

            return new AbortFixture(hostStation, shipStation, shipGrid, map.MapUid);
        }

        private sealed record AbortFixture(
            EntityUid HostStation,
            EntityUid ShipStation,
            EntityUid Ship,
            EntityUid HostMap);

        private static Task<int> CountRevisions(IServerDbManager db)
        {
            return db.RunTriadDbCommand(
                async (context, token) => await context.DrydockRevision.AsNoTracking().CountAsync(token),
                CancellationToken.None);
        }

    }
}
