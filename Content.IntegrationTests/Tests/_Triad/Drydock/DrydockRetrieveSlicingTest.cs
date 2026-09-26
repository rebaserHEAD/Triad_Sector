#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A retrieve's restore against the tick budget: after the engine's startup, the after-start members, the re-dirty and
    /// the restore raises run one item per step (<see cref="DrydockPhase.Restore"/>), on started entities paused on the
    /// staging map, and a restore the world moves under aborts cleanly.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRetrieveSlicingTest
    {
        private const string WallProtoId = "WallSolid";
        private const int Side = 40;

        /// <summary>
        /// A hull of <see cref="Side"/> by <see cref="Side"/> walls retrieved at a one-millisecond budget comes back whole,
        /// and its restore spanned more than one tick, which only a suspension inside the phase gives: a phase's force
        /// suspension at its start moves it to the next tick and no further. Control: every wall stored is a wall restored.
        /// </summary>
        [Test]
        public async Task ARestoreAcrossTicksBringsTheWholeHullBack()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var drydock = server.System<DrydockSystem>();

            var (station, shipGrid, owner) = await WalledShip(pair);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 1));
            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 0));

            var walls = 0;
            var paused = 0;
            DrydockPhaseCost restore = default;
            await server.WaitPost(() =>
            {
                restore = drydock.LastPhaseCosts?.GetValueOrDefault(DrydockPhase.Restore) ?? default;
                var children = entMan.GetComponent<TransformComponent>(retrieved.Grid!.Value).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    if (entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID == WallProtoId)
                        walls++;
                    if (entMan.GetComponent<MetaDataComponent>(child).EntityPaused)
                        paused++;
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
                Assert.That(restore.Ticks, Is.GreaterThan(1), $"The restore suspended inside its phase ({restore.Ticks} ticks, {restore.TotalMs:F1} ms).");
                Assert.That(walls, Is.EqualTo(Side * Side), "Every wall stored came back.");
                Assert.That(paused, Is.Zero, "The dock thawed the hull.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The same hull at the same budget with the four deadlines of <see cref="DrydockThawTest.PlaceDeadlines"/> aboard,
        /// all set in one tick: after a restore that sat paused across ticks, each is the same distance from the engine's own
        /// shifted one (<c>RepeatingTriggerComponent.NextTrigger</c>) as when it was set, so the two nothing shifts on unpause
        /// lost none of the time the hull sat paused and the one a hand-written handler shifts was not shifted twice.
        /// Control: the restore spanned more than one tick.
        /// </summary>
        [Test]
        public async Task ARestoreAcrossTicksKeepsEveryDeadline()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var drydock = server.System<DrydockSystem>();

            var (station, shipGrid, owner) = await WalledShip(pair);
            await server.WaitPost(() => DrydockThawTest.PlaceDeadlines(entMan, shipGrid));

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 1));
            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 0));

            DrydockPhaseCost restore = default;
            DrydockThawTest.Deadlines after = default;
            await server.WaitPost(() =>
            {
                restore = drydock.LastPhaseCosts?.GetValueOrDefault(DrydockPhase.Restore) ?? default;
                var hull = new List<EntityUid>();
                var children = entMan.GetComponent<TransformComponent>(retrieved.Grid!.Value).ChildEnumerator;
                while (children.MoveNext(out var child))
                    hull.Add(child);

                after = DrydockThawTest.ReadDeadlines(entMan, hull, TimeSpan.Zero);
            });

            var exact = TimeSpan.FromMilliseconds(1);
            Assert.Multiple(() =>
            {
                Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
                Assert.That(restore.Ticks, Is.GreaterThan(1), $"The control: the restore sat paused across ticks ({restore.Ticks}).");

                Assert.That(after.Advertised - after.Triggered, Is.EqualTo(DrydockThawTest.Advertised - DrydockThawTest.Triggered).Within(exact),
                    "Nothing shifts the next advertisement on unpause; the thaw paid it the pause.");
                Assert.That(after.Charged - after.Triggered, Is.EqualTo(DrydockThawTest.Charged - DrydockThawTest.Triggered).Within(exact),
                    "Nothing shifts a charge's last update on unpause; the thaw paid it the pause.");
                Assert.That(after.Delayed - after.Triggered, Is.EqualTo(DrydockThawTest.Delayed - DrydockThawTest.Triggered).Within(exact),
                    "The use delay's handler shifted it once.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The staging map deleted while the retrieve is parked in its restore, as an admin or a round's end would: the
        /// retrieve answers <see cref="DrydockRetrieveResult.Cancelled"/>, the ship stays stored, and nothing of the load is
        /// left. Control: the staging map did hold the started hull when it was deleted.
        /// </summary>
        [Test]
        public async Task AStagingMapDeletedMidRestoreAbortsTheRetrieve()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var drydock = server.System<DrydockSystem>();
            var store = server.ResolveDependency<DrydockStore>();

            var (station, shipGrid, owner) = await WalledShip(pair);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 1));

            Task<DrydockRetrieve> retrieve = null!;
            await server.WaitPost(() => retrieve = drydock.TryRetrieveShip(shipId!.Value, owner, station, null));

            var deletedWithHull = false;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!retrieve.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await pair.RunTicksSync(1);
                if (deletedWithHull)
                    continue;

                await server.WaitPost(() =>
                {
                    // Every query here sees paused entities: the staging map and everything on it are paused.
                    var query = entMan.AllEntityQueryEnumerator<DrydockStagingMapComponent>();
                    while (query.MoveNext(out var map, out var staging))
                    {
                        if (staging.Kind != DrydockStagingKind.Retrieve || staging.ShipId != shipId)
                            continue;

                        var grids = entMan.AllEntityQueryEnumerator<MapGridComponent, TransformComponent>();
                        while (grids.MoveNext(out var grid, out _, out var xform))
                        {
                            // Started: the load allocates, fills and starts inside one tick, so a grid seen here has.
                            if (xform.MapUid != map || entMan.GetComponent<MetaDataComponent>(grid).EntityLifeStage < EntityLifeStage.Initialized)
                                continue;

                            entMan.DeleteEntity(map);
                            deletedWithHull = true;
                            return;
                        }
                    }
                });
            }

            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 0));

            Assert.That(retrieve.IsCompleted, Is.True, "The retrieve never finished.");
            var outcome = await retrieve;
            var header = await store.GetShipHeader(shipId!.Value);

            var stagingLeft = 0;
            var copiesLeft = 0;
            await server.WaitPost(() =>
            {
                stagingLeft = DrydockRoundTripTest.CountStagingMaps(entMan);
                var identities = entMan.AllEntityQueryEnumerator<DrydockIdentityComponent>();
                while (identities.MoveNext(out _, out var identity))
                {
                    if (identity.ShipId == shipId)
                        copiesLeft++;
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(deletedWithHull, Is.True, "The control: the staging map held the started hull when it went.");
                Assert.That(outcome.Result, Is.EqualTo(DrydockRetrieveResult.Cancelled));
                Assert.That(header!.State, Is.EqualTo(DrydockShipState.Stored), "The claim went back; the ship is still stored.");
                Assert.That(stagingLeft, Is.Zero, "No staging map is left.");
                Assert.That(copiesLeft, Is.Zero, "No copy of the hull is left.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>The round-trip fixture's ship and station, its grid tiled out to <see cref="Side"/> square and walled.</summary>
        private static async Task<(EntityUid Station, EntityUid ShipGrid, Guid Owner)> WalledShip(Pair.TestPair pair)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var db = server.ResolveDependency<IServerDbManager>();
            var mapSys = server.System<SharedMapSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var grid = entMan.GetComponent<MapGridComponent>(shipGrid);
                for (var x = 0; x < Side; x++)
                {
                    for (var y = 3; y < Side + 3; y++)
                        mapSys.SetTile(shipGrid, grid, new Vector2i(x, y), new Tile(1));
                }

                for (var x = 0; x < Side; x++)
                {
                    for (var y = 3; y < Side + 3; y++)
                        entMan.SpawnEntity(WallProtoId, new EntityCoordinates(shipGrid, x + 0.5f, y + 0.5f));
                }
            });

            await pair.RunTicksSync(5);
            return (station, shipGrid, owner);
        }
    }
}
