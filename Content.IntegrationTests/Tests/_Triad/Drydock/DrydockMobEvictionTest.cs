#nullable enable

using System;
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
using Content.Server.Storage.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A store keeps only map-savable entities, and almost every creature composes <c>save: false</c>, so a creature or
    /// body the organics gate lets through would be deleted with the hull. The gates move each one off to the impound
    /// drop-off with what it wears and carries, and a backstop just before the serialize phase refuses the store for
    /// any that is still aboard.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockMobEvictionTest
    {
        [Test]
        public async Task AStoreMovesItsCreaturesAndBodiesOffWithWhatTheyCarry()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var drydock = server.System<DrydockSystem>();
            var owner = await PrepareOwner(pair);
            var fixture = await BuildFixture(pair, sliced: false);

            EntityUid mouse = default, body = default, crate = default, cratedMouse = default, uniform = default;
            await server.WaitPost(() =>
            {
                var containers = entMan.System<SharedContainerSystem>();
                mouse = entMan.SpawnEntity("MobMouse", new EntityCoordinates(fixture.Ship, new Vector2(0.5f, 0.5f)));

                body = entMan.SpawnEntity("MobHuman", new EntityCoordinates(fixture.Ship, new Vector2(2.5f, 0.5f)));
                uniform = entMan.SpawnEntity("ClothingUniformJumpsuitColorGrey", new EntityCoordinates(fixture.Ship, new Vector2(2.5f, 0.5f)));
                entMan.System<InventorySystem>().TryEquip(body, uniform, "jumpsuit", silent: true, force: true);
                entMan.System<MobStateSystem>().ChangeMobState(body, MobState.Dead);

                crate = entMan.SpawnEntity("CrateGenericSteel", new EntityCoordinates(fixture.Ship, new Vector2(0.5f, 2.5f)));
                cratedMouse = entMan.SpawnEntity("MobMouse", new EntityCoordinates(fixture.Ship, new Vector2(0.5f, 2.5f)));
                containers.Insert(cratedMouse, entMan.GetComponent<EntityStorageComponent>(crate).Contents);
            });

            await pair.RunTicksSync(5);

            var wornBefore = false;
            var cratedBefore = false;
            await server.WaitPost(() =>
            {
                wornBefore = entMan.System<InventorySystem>().TryGetSlotEntity(body, "jumpsuit", out var worn) && worn == uniform;
                cratedBefore = entMan.System<SharedContainerSystem>().IsEntityInContainer(cratedMouse);
            });

            var (result, _) = await DrydockTestHelpers.RunOnServer(pair,
                () => drydock.TryStoreShip(fixture.Ship, owner, null, stationUid: fixture.HostStation));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var containers = entMan.System<SharedContainerSystem>();
                EntityUid? GridOf(EntityUid uid) => entMan.GetComponent<TransformComponent>(uid).GridUid;

                Assert.Multiple(() =>
                {
                    Assert.That(wornBefore, Is.True, "The control: the body has to be wearing the uniform before the store.");
                    Assert.That(cratedBefore, Is.True, "The control: the second mouse has to start inside the crate.");
                    Assert.That(result, Is.EqualTo(DrydockStoreResult.Success), "Creatures the store leaves out are moved off, not refused for.");
                    Assert.That(entMan.Deleted(fixture.Ship), Is.True, "The control: the hull was stored and despawned.");

                    Assert.That(entMan.Deleted(mouse), Is.False, "A creature aboard is moved off, not deleted with the hull.");
                    Assert.That(GridOf(mouse), Is.EqualTo(fixture.HostGrid), "It lands at the drop-off, on the station's grid.");

                    Assert.That(entMan.Deleted(body), Is.False, "A body aboard is moved off too.");
                    Assert.That(GridOf(body), Is.EqualTo(fixture.HostGrid));
                    Assert.That(entMan.System<InventorySystem>().TryGetSlotEntity(body, "jumpsuit", out var worn) && worn == uniform, Is.True,
                        "And it keeps what it wears: the move takes its subtree with it.");

                    Assert.That(entMan.Deleted(cratedMouse), Is.False, "A creature inside a stored container is moved off.");
                    Assert.That(containers.IsEntityInContainer(cratedMouse), Is.False, "Out of the container, which stayed with the hull.");
                    Assert.That(GridOf(cratedMouse), Is.EqualTo(fixture.HostGrid));
                    Assert.That(entMan.Deleted(crate), Is.True, "The control: the crate itself is savable and went with the hull.");
                });
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task ACreatureStillAboardAtTheBackstopRefusesTheStore()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var drydock = server.System<DrydockSystem>();
            var owner = await PrepareOwner(pair);
            var fixture = await BuildFixture(pair, sliced: true);
            var revisionsBefore = await CountRevisions(server.ResolveDependency<IServerDbManager>());

            // Spawned when the preparation phase opens, after the last organics gate has run, so only the
            // backstop is left to catch it.
            EntityUid? mouse = null;
            void OnProgress(int percent, DrydockPhase phase)
            {
                if (phase == DrydockPhase.Prepare && mouse == null)
                    mouse = entMan.SpawnEntity("MobMouse", new EntityCoordinates(fixture.Ship, new Vector2(0.5f, 0.5f)));
            }

            Task<(DrydockStoreResult Result, Guid? ShipId)>? storeTask = null;
            await server.WaitPost(() => storeTask = drydock.TryStoreShip(
                fixture.Ship, owner, null, stationUid: fixture.HostStation, onProgress: OnProgress));

            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!storeTask!.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(60))
                await pair.RunTicksSync(1);

            await pair.RunTicksSync(10);
            var (result, _) = await storeTask!;
            var revisionsAfter = await CountRevisions(server.ResolveDependency<IServerDbManager>());

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(mouse, Is.Not.Null, "The control: the preparation phase has to open, or the mouse was never put aboard.");
                    Assert.That(result, Is.EqualTo(DrydockStoreResult.CreatureAboard),
                        "A creature the store would leave out, still aboard past the gates, refuses the store.");
                    Assert.That(revisionsAfter, Is.EqualTo(revisionsBefore), "A refused store files nothing.");
                    Assert.That(entMan.Deleted(fixture.Ship), Is.False, "The refused hull is handed back, not despawned.");
                    Assert.That(mouse is { } m && !entMan.Deleted(m), Is.True, "The creature is not deleted.");
                    Assert.That(mouse is { } n && entMan.GetComponent<TransformComponent>(n).GridUid == fixture.Ship, Is.True,
                        "And it is still aboard the hull the unwind handed back.");
                });
            });

            await pair.CleanReturnAsync();
        }

        private static async Task<Guid> PrepareOwner(TestPair pair)
        {
            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(pair.Server.ResolveDependency<IServerDbManager>(), owner);
            await pair.Server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            return owner;
        }

        /// <summary>
        /// A host station whose grid carries a late-join spawn point, which is the impound drop-off the eviction moves
        /// creatures to, and a small hull beside it with an airlock aboard.
        /// </summary>
        private static async Task<Fixture> BuildFixture(TestPair pair, bool sliced)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var map = await pair.CreateTestMap();

            EntityUid hostStation = default;
            EntityUid ship = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, sliced ? 2 : 0);
                server.System<ShipyardSystem>().SetupShipyardIfNeeded();

                var mapSys = server.System<SharedMapSystem>();
                hostStation = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(hostStation);
                server.System<StationSystem>().AddGridToStation(hostStation, map.Grid.Owner);
                entMan.SpawnEntity("SpawnPointLatejoin", map.GridCoords);

                var grid = mapSys.CreateGridEntity(map.MapId);
                ship = grid.Owner;
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                        mapSys.SetTile(grid.Owner, grid.Comp, new Vector2i(x, y), new Tile(1));
                }

                entMan.EnsureComponent<ShuttleComponent>(ship);
                entMan.SpawnEntity("Airlock", new EntityCoordinates(ship, new Vector2(1.5f, 1.5f)));
            });

            await pair.MakeCleanupImmune(map.Grid.Owner);
            await pair.RunTicksSync(5);

            return new Fixture(hostStation, map.Grid.Owner, ship);
        }

        private sealed record Fixture(EntityUid HostStation, EntityUid HostGrid, EntityUid Ship);

        private static Task<int> CountRevisions(IServerDbManager db)
        {
            return db.RunTriadDbCommand(
                async (context, token) => await context.DrydockRevision.AsNoTracking().CountAsync(token),
                CancellationToken.None);
        }
    }
}
