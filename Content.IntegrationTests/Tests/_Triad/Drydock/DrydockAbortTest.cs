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
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared._Triad.CCVar;
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
    /// A store that fails must leave the ship exactly as usable as it was. This is the half of the
    /// pipeline that had no proof behind it: the protective <c>try</c> was moved to open before the
    /// first restorable mutation rather than after the whole preparation, and until this ran that
    /// correction was an argument rather than a measurement.
    ///
    /// <para>The failure is induced through the owner foreign key rather than by patching the
    /// system under test. Filing a revision for a player who has no row throws inside
    /// <c>FileRevision</c>, which sits after the sidecars, the strip list and the fidelity capture,
    /// so the unwind is exercised across its whole surface by a fault the database really produces
    /// rather than by a seam opened for the test.</para>
    ///
    /// <para>The sharpest assertion here is the station re-book. Stripping station membership fires
    /// the station system's shutdown handler, which removes the grid from the station's own grid
    /// set. Restoring the component brings the reference back but <em>not</em> the set entry, so an
    /// unwind that only restores components looks complete and is not: the ship would come out of a
    /// failed store no longer part of its own station.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockAbortTest
    {
        [Test]
        public async Task AFailedStoreLeavesTheShipUsable()
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

            // Deliberately NOT inserted into the player table. The owner column is a real foreign
            // key, so this is what makes the commit throw.
            var orphanOwner = Guid.NewGuid();


            var map = await pair.CreateTestMap();

            EntityUid shipStation = default;
            EntityUid shipGrid = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                shipyard.SetupShipyardIfNeeded();

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

                // Both entries of the store strip list have to be present, or the unwind is only
                // half exercised. Ships in this fork are stations, which is what makes the re-book
                // below a real path rather than a hypothetical one.
                entMan.EnsureComponent<ShipRepairDataComponent>(shipGrid);

                shipStation = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(shipStation);
                stationSys.AddGridToStation(shipStation, shipGrid);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.HasComponent<StationMemberComponent>(shipGrid), Is.True,
                    "AddGridToStation is what puts the strip-list component on the grid; without it this test proves nothing.");
                Assert.That(entMan.GetComponent<StationDataComponent>(shipStation).Grids, Does.Contain(shipGrid),
                    "The control for the re-book assertion: the grid has to be in the set before the store can remove it.");
            });

            var revisionsBefore = await CountRevisions(db);

            // The induced fault logs at error level, and a pooled pair fails its return on any such
            // log. Lift the bar only for the span that is supposed to produce one, then put it back,
            // so an unexpected error from anywhere else in this test still fails it.
            var failureLevel = pair.ServerLogHandler.FailureLevel;
            pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;

            // The store must fail, and it must fail loudly rather than reporting a refusal reason:
            // a database fault is not one of the outcomes the enum models.
            Task<(DrydockStoreResult Result, Guid? ShipId)>? storeTask = null;
            await server.WaitPost(() => storeTask = drydock.TryStoreShip(shipGrid, orphanOwner, null));

            for (var i = 0; i < 600 && !storeTask!.IsCompleted; i++)
            {
                await pair.RunTicksSync(1);
            }

            await pair.RunTicksSync(5);
            pair.ServerLogHandler.FailureLevel = failureLevel;

            Assert.That(storeTask!.IsCompleted, Is.True, "The store never completed.");
            Assert.That(storeTask.IsFaulted, Is.True,
                "Filing a revision for a player who does not exist must violate the owner foreign key.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entMan.Deleted(shipGrid), Is.False,
                        "A failed store must never despawn the ship. The grid is only disposed of after the document is filed.");

                    Assert.That(entMan.HasComponent<DrydockInProgressComponent>(shipGrid), Is.False,
                        "The in-progress marker blocks every container aboard, hands included. Leaving it on strands the ship.");

                    Assert.That(entMan.HasComponent<ShipRepairDataComponent>(shipGrid), Is.True,
                        "Stripped components are held as deep copies precisely so a refusal can put them back.");

                    Assert.That(entMan.HasComponent<StationMemberComponent>(shipGrid), Is.True,
                        "Station membership is the other strip-list entry.");

                    // The one that a component-only unwind gets wrong.
                    Assert.That(entMan.GetComponent<StationDataComponent>(shipStation).Grids, Does.Contain(shipGrid),
                        "Restoring StationMemberComponent does not re-add the grid to the station's own set: the shutdown handler removed it, and the re-add has to go back through the station system.");

                    Assert.That(entMan.HasComponent<ShuttleComponent>(shipGrid), Is.True,
                        "The ship still has to be a ship.");
                });

                // Sidecars are an implementation detail of a store in flight and have no business
                // riding a ship that is still being flown.
                var gasQuery = entMan.AllEntityQueryEnumerator<DrydockPipeGasComponent>();
                Assert.That(gasQuery.MoveNext(out _, out _), Is.False, "A pipe gas sidecar survived a failed store.");

                var damageQuery = entMan.AllEntityQueryEnumerator<DrydockDamageSidecarComponent>();
                Assert.That(damageQuery.MoveNext(out _, out _), Is.False, "A damage sidecar survived a failed store.");
            });

            Assert.That(await CountRevisions(db), Is.EqualTo(revisionsBefore),
                "A store that aborts files nothing. A revision here would be a half-committed store, which is the failure this whole ordering exists to prevent.");

            await pair.CleanReturnAsync();
        }

        private static Task<int> CountRevisions(IServerDbManager db)
        {
            return db.RunTriadDbCommand(
                async (context, token) => await context.DrydockRevision.AsNoTracking().CountAsync(token),
                CancellationToken.None);
        }
    }
}
