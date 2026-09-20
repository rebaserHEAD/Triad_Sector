#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Shared.SmartFridge;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// H09: a restored, stocked smart fridge lists its stock. <c>ContainedEntries</c> is a networked index over the fridge's
    /// container, no longer a data field (a <c>SmartFridgeEntry</c> cannot be a YAML mapping key), and only map init rebuilt it,
    /// which a restore does not raise, so a stocked fridge would report itself empty and its stock be unreachable through the
    /// UI. The handler rebuilds it from the container, once every entity has started.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(SmartFridgeComponent))]
    public sealed class DrydockSmartFridgeRestoreTest
    {
        private static Dictionary<SmartFridgeEntry, int> Index(SmartFridgeComponent fridge) =>
            fridge.ContainedEntries.ToDictionary(entry => entry.Key, entry => entry.Value.Count);

        [Test]
        public async Task AStockedFridgeRestoredListsItsStockAgainAndTheIndexPointsAtItsContents()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var containers = server.System<SharedContainerSystem>();

            var grid = map.Grid.Owner;
            Dictionary<SmartFridgeEntry, int> indexBefore = new();
            List<SmartFridgeEntry> menuBefore = new();
            await server.WaitPost(() =>
            {
                var fridge = entMan.SpawnEntity("SmartFridge", new EntityCoordinates(grid, 0.5f, 0.5f));
                var container = containers.GetContainer(fridge, entMan.GetComponent<SmartFridgeComponent>(fridge).Container);

                // Two of one name and one of another, so a key holds a set and the index holds two keys.
                foreach (var prototype in new[] { "FoodBanana", "FoodBanana", "FoodApple" })
                    containers.Insert(entMan.SpawnEntity(prototype, new EntityCoordinates(grid, 0.5f, 0.5f)), container);

                var component = entMan.GetComponent<SmartFridgeComponent>(fridge);
                indexBefore = Index(component);
                menuBefore = component.Entries.ToList();
            });

            Assert.Multiple(() =>
            {
                Assert.That(indexBefore, Has.Count.EqualTo(2), "The control: the live fridge indexes two names.");
                Assert.That(indexBefore.Values.Sum(), Is.EqualTo(3), "The control: and all three items.");
            });

            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var fridge = fidelity.GridTreeList(result.Grid).Single(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "SmartFridge");
                var component = entMan.GetComponent<SmartFridgeComponent>(fridge);
                var container = containers.GetContainer(fridge, component.Container);

                Assert.Multiple(() =>
                {
                    Assert.That(Index(component), Is.EquivalentTo(indexBefore), "The restored fridge has to index the same names with the same counts.");
                    Assert.That(component.Entries, Is.EquivalentTo(menuBefore), "And keep its menu.");

                    var indexed = component.ContainedEntries.Values.SelectMany(set => set).Select(net => entMan.GetEntity(net)).ToList();
                    Assert.That(indexed, Is.EquivalentTo(container.ContainedEntities), "The index has to point at the entities that are in the container now.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The restore handler contract: a second raise leaves the index as it was (the rebuild clears and refills), no
        /// component is added to the fridge, and a raise at an entity that is gone is not fatal.
        /// </summary>
        [Test]
        public async Task ASecondRestoreRaiseChangesNothingAddsNoComponentAndAGoneEntityIsTolerated()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var containers = server.System<SharedContainerSystem>();
            var grid = map.Grid.Owner;

            await server.WaitPost(() =>
            {
                var fridge = entMan.SpawnEntity("SmartFridge", new EntityCoordinates(grid, 0.5f, 0.5f));
                var gone = entMan.SpawnEntity("SmartFridge", new EntityCoordinates(grid, 0.5f, 0.5f));
                var container = containers.GetContainer(fridge, entMan.GetComponent<SmartFridgeComponent>(fridge).Container);
                containers.Insert(entMan.SpawnEntity("FoodBanana", new EntityCoordinates(grid, 0.5f, 0.5f)), container);

                var indexBefore = Index(entMan.GetComponent<SmartFridgeComponent>(fridge));
                var componentsBefore = entMan.GetComponents(fridge).Select(c => c.GetType().Name).OrderBy(n => n).ToList();
                entMan.DeleteEntity(gone);

                var ev = new GridRestoredEvent(grid);
                entMan.EventBus.RaiseLocalEvent(fridge, ref ev);
                entMan.EventBus.RaiseLocalEvent(fridge, ref ev);

                Assert.Multiple(() =>
                {
                    Assert.That(Index(entMan.GetComponent<SmartFridgeComponent>(fridge)), Is.EquivalentTo(indexBefore), "A second rebuild leaves the index as it was.");
                    Assert.That(entMan.GetComponents(fridge).Select(c => c.GetType().Name).OrderBy(n => n), Is.EqualTo(componentsBefore), "The handler adds no component.");
                    Assert.That(() => entMan.EventBus.RaiseLocalEvent(gone, ref ev), Throws.Nothing, "A raise at an entity that is gone is not fatal.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}
