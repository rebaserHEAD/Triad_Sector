#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Mono.Cleanup;
using Content.Server._Triad.Atmos.EntitySystems;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Codec;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.NodeGroups;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Atmos;
using Content.Shared.NodeContainer;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Pipe-net gas across a store and load, carried by <see cref="PipeGasCarrySystem"/> on the carried-values seam: each
    /// kept pipe node carries its share of its net by volume, keyed by node name, and the load pours each share into the net
    /// its node joins at that node's first rebuild, once.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(PipeGasCarrySystem))]
    public sealed class DrydockPipeGasCarryTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: DrydockPipeGasUnsavablePipe
  save: false
  components:
  - type: Transform
    anchored: true
  - type: NodeContainer
    nodes:
      pipe:
        !type:PipeNode
        nodeGroupID: Pipe
        pipeDirection: Longitudinal
";

        private const float Tolerance = 0.001f;

        private static PipeNode Pipe(IEntityManager entMan, EntityUid uid, string node) =>
            (PipeNode) entMan.GetComponent<NodeContainerComponent>(uid).Nodes[node];

        private static EntityUid SpawnAt(IEntityManager entMan, EntityUid grid, string prototype, int y) =>
            entMan.SpawnEntity(prototype, new EntityCoordinates(grid, 0.5f, y + 0.5f));

        private static void Tiles(SharedMapSystem maps, Entity<MapGridComponent> grid, Tile tile, int count)
        {
            for (var y = 0; y < count; y++)
                maps.SetTile(grid.Owner, grid.Comp, new Vector2i(0, y), tile);
        }

        /// <summary>The restored entity of <paramref name="prototype"/> whose local y is <paramref name="y"/>.</summary>
        private static EntityUid At(IEntityManager entMan, DrydockFidelitySystem fidelity, EntityUid grid, string prototype, int y) =>
            fidelity.GridTreeList(grid).Single(uid =>
                entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == prototype
                && (int) MathF.Floor(entMan.GetComponent<TransformComponent>(uid).LocalPosition.Y) == y);

        /// <summary>What the store carried for <paramref name="uid"/>, read back from the image's row.</summary>
        private static Dictionary<string, GasMixture>? Carried(ISerializationManager serialization, DrydockImage image, long id)
        {
            var entity = image.Entities.Single(e => e.Id == id);
            if (!entity.Rows.TryGetValue(DrydockImageSystem.CarriedRow, out var text))
                return null;

            var row = (MappingDataNode) DrydockNodeJson.Decode(JsonNode.Parse(text));
            return serialization.Read<Dictionary<string, GasMixture>>(row[PipeGasCarrySystem.SharesKey], notNullableOverride: true);
        }

        /// <summary>
        /// A pump sits between two nets. Each net's gas comes back into its own net, not the other's, the pump carries a share
        /// per node, an entity with no node container carries nothing, and the store adds no component and moves no modified
        /// tick.
        /// </summary>
        [Test]
        public async Task EachNetsGasComesBackIntoItsOwnNetAndTheStoreOnlyReads()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var pipeGas = server.System<PipeGasCarrySystem>();
            var serialization = server.ResolveDependency<ISerializationManager>();
            var grid = map.Grid.Owner;

            EntityUid top = default, pump = default, bottom = default;
            await server.WaitPost(() =>
            {
                Tiles(server.System<SharedMapSystem>(), map.Grid, map.Tile.Tile, 3);
                bottom = SpawnAt(entMan, grid, "GasPipeStraight", 0);
                pump = SpawnAt(entMan, grid, "GasPressurePump", 1);
                top = SpawnAt(entMan, grid, "GasPipeStraight", 2);
            });

            await pair.RunTicksSync(3);

            DrydockImageStoreResult stored = default!;
            Dictionary<EntityUid, long> ids = new();
            List<string> added = new(), dirtied = new();
            await server.WaitPost(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(Pipe(entMan, top, "pipe").NodeGroup, Is.SameAs(Pipe(entMan, pump, "inlet").NodeGroup), "The control: the top pipe and the pump's inlet are one net.");
                    Assert.That(Pipe(entMan, bottom, "pipe").NodeGroup, Is.SameAs(Pipe(entMan, pump, "outlet").NodeGroup), "The control: the bottom pipe and the pump's outlet are another.");
                    Assert.That(Pipe(entMan, top, "pipe").NodeGroup, Is.Not.SameAs(Pipe(entMan, bottom, "pipe").NodeGroup));
                });

                Pipe(entMan, top, "pipe").Air.AdjustMoles(Gas.Oxygen, 50f);
                Pipe(entMan, bottom, "pipe").Air.AdjustMoles(Gas.Nitrogen, 30f);

                ids = image.Walk(grid).Ids;
                var before = ids.Keys.SelectMany(uid => entMan.GetComponents(uid).Select(c => (Key: (uid, c.GetType().Name), c.LastModifiedTick)))
                    .ToDictionary(entry => entry.Key, entry => entry.LastModifiedTick);
                stored = image.Store(grid);
                var after = ids.Keys.SelectMany(uid => entMan.GetComponents(uid).Select(c => (Key: (uid, c.GetType().Name), c.LastModifiedTick))).ToList();
                added = after.Where(entry => !before.ContainsKey(entry.Key)).Select(entry => entry.Key.Name).ToList();
                dirtied = after.Where(entry => before.TryGetValue(entry.Key, out var tick) && tick != entry.LastModifiedTick).Select(entry => entry.Key.Name).ToList();
            });

            var pumpShares = Carried(serialization, stored.Image, ids[pump]);
            Assert.Multiple(() =>
            {
                Assert.That(added, Is.Empty, "The store adds no component.");
                Assert.That(dirtied, Is.Empty, "And dirties none.");
                Assert.That(pumpShares?.Keys, Is.EquivalentTo(new[] { "inlet", "outlet" }), "The pump carries a share for each of its nodes.");
                Assert.That(Carried(serialization, stored.Image, ids[grid]), Is.Null, "An entity with no node container carries nothing.");
            });

            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await pair.RunTicksSync(3);

            await server.WaitAssertion(() =>
            {
                var restoredTop = Pipe(entMan, At(entMan, fidelity, result.Grid, "GasPipeStraight", 2), "pipe").Air;
                var restoredBottom = Pipe(entMan, At(entMan, fidelity, result.Grid, "GasPipeStraight", 0), "pipe").Air;
                var restoredPump = At(entMan, fidelity, result.Grid, "GasPressurePump", 1);

                Assert.Multiple(() =>
                {
                    Assert.That(restoredTop.GetMoles(Gas.Oxygen), Is.EqualTo(50f).Within(Tolerance), "The top net has its oxygen back.");
                    Assert.That(restoredTop.GetMoles(Gas.Nitrogen), Is.EqualTo(0f).Within(Tolerance), "And none of the other net's nitrogen.");
                    Assert.That(restoredBottom.GetMoles(Gas.Nitrogen), Is.EqualTo(30f).Within(Tolerance), "The bottom net has its nitrogen back.");
                    Assert.That(restoredBottom.GetMoles(Gas.Oxygen), Is.EqualTo(0f).Within(Tolerance), "And none of the other net's oxygen.");
                    Assert.That(pipeGas.Holds(restoredPump), Is.False, "Nothing is left waiting once every node has poured.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A share is poured once: a rebuild forced after the pour, which raises the rebuild event at every member again,
        /// moves no gas.
        /// </summary>
        [Test]
        public async Task AShareIsPouredOnceAndALaterRebuildMovesNothing()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var nodeGroups = server.System<NodeGroupSystem>();
            var grid = map.Grid.Owner;

            EntityUid lower = default;
            await server.WaitPost(() =>
            {
                Tiles(server.System<SharedMapSystem>(), map.Grid, map.Tile.Tile, 2);
                lower = SpawnAt(entMan, grid, "GasPipeStraight", 0);
                SpawnAt(entMan, grid, "GasPipeStraight", 1);
            });

            await pair.RunTicksSync(3);

            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                Pipe(entMan, lower, "pipe").Air.AdjustMoles(Gas.Oxygen, 40f);
                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await pair.RunTicksSync(3);

            await server.WaitAssertion(() =>
            {
                var pipe = Pipe(entMan, At(entMan, fidelity, result.Grid, "GasPipeStraight", 0), "pipe");
                var poured = pipe.Air.TotalMoles;
                var group = pipe.NodeGroup;

                nodeGroups.QueueRemakeGroup((BaseNodeGroup) group!);
                nodeGroups.ForceUpdate();

                Assert.Multiple(() =>
                {
                    Assert.That(poured, Is.EqualTo(40f).Within(Tolerance), "The control: the net came back with its gas.");
                    Assert.That(pipe.NodeGroup, Is.Not.SameAs(group), "The control: the net was rebuilt.");
                    Assert.That(pipe.Air.TotalMoles, Is.EqualTo(poured).Within(Tolerance), "A later rebuild pours nothing again.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>A share still waiting for its node's first rebuild is dropped when its entity is deleted.</summary>
        [Test]
        public async Task AWaitingShareIsDroppedWhenItsEntityIsDeleted()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var pipeGas = server.System<PipeGasCarrySystem>();
            var grid = map.Grid.Owner;

            EntityUid lower = default;
            await server.WaitPost(() =>
            {
                Tiles(server.System<SharedMapSystem>(), map.Grid, map.Tile.Tile, 2);
                lower = SpawnAt(entMan, grid, "GasPipeStraight", 0);
                SpawnAt(entMan, grid, "GasPipeStraight", 1);
            });

            await pair.RunTicksSync(3);

            await server.WaitPost(() =>
            {
                Pipe(entMan, lower, "pipe").Air.AdjustMoles(Gas.Oxygen, 40f);
                var stored = image.Store(grid);
                image.Despawn(grid);
                var result = image.Load(stored.Image, map.MapUid);
                var restored = At(entMan, fidelity, result.Grid, "GasPipeStraight", 0);
                var waiting = pipeGas.Holds(restored);

                entMan.DeleteEntity(restored);

                Assert.Multiple(() =>
                {
                    Assert.That(waiting, Is.True, "The control: at the load the node has no net yet, so its share waits.");
                    Assert.That(pipeGas.Holds(restored), Is.False, "Deleting the entity drops its waiting share.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A net that crosses a dock belongs to the stored grid only in proportion to the volume on it: the ship carries its
        /// fraction, and the store leaves the net, and so the other grid's share, as it was.
        /// </summary>
        [Test]
        public async Task ANetDockedToAnotherGridCarriesOnlyTheShipsFraction()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var maps = server.System<SharedMapSystem>();
            var transform = server.System<SharedTransformSystem>();
            var docking = server.System<DockingSystem>();
            var image = server.System<DrydockImageSystem>();
            var serialization = server.ResolveDependency<ISerializationManager>();
            var station = map.Grid.Owner;
            await pair.MakeCleanupImmune(station);

            EntityUid ship = default, stationLock = default, shipLock = default;
            await server.WaitPost(() =>
            {
                var shipGrid = maps.CreateGridEntity(map.MapId);
                ship = shipGrid.Owner;
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                        maps.SetTile(ship, shipGrid.Comp, new Vector2i(x, y), map.Tile.Tile);
                }

                transform.SetWorldPosition(ship, new Vector2(1f, -1f));
                entMan.EnsureComponent<ShuttleComponent>(ship);
                entMan.EnsureComponent<CleanupImmuneComponent>(ship);

                stationLock = entMan.SpawnEntity("Gaslock", new EntityCoordinates(station, new Vector2(0.5f, 0.5f)));
                transform.SetLocalRotation(stationLock, Direction.East.ToAngle());
                shipLock = entMan.SpawnEntity("Gaslock", new EntityCoordinates(ship, new Vector2(0.5f, 1.5f)));
                transform.SetLocalRotation(shipLock, Direction.West.ToAngle());

                docking.Dock(
                    (stationLock, entMan.GetComponent<DockingComponent>(stationLock)),
                    (shipLock, entMan.GetComponent<DockingComponent>(shipLock)));
            });

            await pair.RunTicksSync(3);

            DrydockImageStoreResult stored = default!;
            Dictionary<EntityUid, long> ids = new();
            float before = 0, afterStore = 0, expected = 0;
            await server.WaitPost(() =>
            {
                var stationOutlet = Pipe(entMan, stationLock, "outlet");
                var shipOutlet = Pipe(entMan, shipLock, "outlet");
                Assert.That(shipOutlet.NodeGroup, Is.SameAs(stationOutlet.NodeGroup), "The control: the two gaslocks' outlets are one net across the dock.");

                shipOutlet.Air.AdjustMoles(Gas.Oxygen, 100f);
                before = shipOutlet.Air.GetMoles(Gas.Oxygen);
                expected = before * shipOutlet.Volume / (shipOutlet.Volume + stationOutlet.Volume);

                ids = image.Walk(ship).Ids;
                stored = image.Store(ship);
                afterStore = stationOutlet.Air.GetMoles(Gas.Oxygen);
            });

            var shares = Carried(serialization, stored.Image, ids[shipLock]);
            Assert.Multiple(() =>
            {
                Assert.That(expected, Is.LessThan(before), "The control: the ship's fraction is less than the whole net.");
                Assert.That(shares?.Keys, Is.EquivalentTo(new[] { "outlet" }), "Only the gaslock's docked node has gas to carry.");
                Assert.That(shares!["outlet"].GetMoles(Gas.Oxygen), Is.EqualTo(expected).Within(Tolerance), "The ship carries its fraction of the net, not the whole net.");
                Assert.That(afterStore, Is.EqualTo(before).Within(Tolerance), "The store leaves the net as it was, the other grid's share with it.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An unsavable member on the stored grid is deleted with it, so its share goes to the kept members: a net of one kept
        /// pipe and one unsavable one carries the whole net on the kept pipe.
        /// </summary>
        [Test]
        public async Task AnUnsavableMemberOnTheStoredGridGivesItsShareToTheKeptMembers()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var serialization = server.ResolveDependency<ISerializationManager>();
            var grid = map.Grid.Owner;

            EntityUid kept = default, unsavable = default;
            await server.WaitPost(() =>
            {
                Tiles(server.System<SharedMapSystem>(), map.Grid, map.Tile.Tile, 2);
                kept = SpawnAt(entMan, grid, "GasPipeStraight", 0);
                unsavable = SpawnAt(entMan, grid, "DrydockPipeGasUnsavablePipe", 1);
            });

            await pair.RunTicksSync(3);

            DrydockImageStoreResult stored = default!;
            Dictionary<EntityUid, long> ids = new();
            await server.WaitPost(() =>
            {
                Assert.That(Pipe(entMan, unsavable, "pipe").NodeGroup, Is.SameAs(Pipe(entMan, kept, "pipe").NodeGroup), "The control: the two pipes are one net.");
                Pipe(entMan, kept, "pipe").Air.AdjustMoles(Gas.Oxygen, 60f);
                ids = image.Walk(grid).Ids;
                stored = image.Store(grid);
            });

            var shares = Carried(serialization, stored.Image, ids[kept]);
            Assert.Multiple(() =>
            {
                Assert.That(ids.ContainsKey(unsavable), Is.False, "The control: the walk left the unsavable pipe out.");
                Assert.That(shares!["pipe"].GetMoles(Gas.Oxygen), Is.EqualTo(60f).Within(Tolerance), "The kept pipe carries the whole net.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
