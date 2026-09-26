#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server._Triad.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// H20: a restored entity's hands, carried by <see cref="HandsCarrySystem"/> on the carried-values seam. A hands
    /// component's hands are not data fields, so without the carry a restored interactor keeps its tool in a container no
    /// hand owns. Each hand, its location, its contents and the active hand come back; the hands' order is the engine's own
    /// insertion rule's, not replayed (ruled).
    /// </summary>
    [TestFixture]
    [TestOf(typeof(HandsCarrySystem))]
    public sealed class DrydockHandsCarryTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: DrydockHandsCarryDummy
  components:
  - type: Hands
  - type: DrydockHandsCarryProbe
";

        /// <summary>
        /// Records what each hand-bearing entity looked like at the head of the restore, before any directed handler ran,
        /// and, for a probe that asks for it, raises the directed event a second time at its entity.
        /// </summary>
        private sealed class DrydockHandsCarryRecorderSystem : EntitySystem
        {
            public readonly Dictionary<EntityUid, (int Hands, EntityUid? Tool)> AtHead = new();
            private bool _reraising;

            public override void Initialize()
            {
                base.Initialize();
                SubscribeLocalEvent<GridRestoringEvent>(OnHead);
                SubscribeLocalEvent<DrydockHandsCarryProbeComponent, GridRestoredEvent>(OnRestored);
            }

            private void OnHead(ref GridRestoringEvent ev)
            {
                foreach (var uid in ev.Entities)
                {
                    if (!TryComp<HandsComponent>(uid, out var hands))
                        continue;

                    var tool = EntityManager.System<SharedContainerSystem>().TryGetContainer(uid, "interactor_tool", out var container)
                        ? container.ContainedEntities.FirstOrDefault()
                        : default;
                    AtHead[uid] = (hands.Hands.Count, tool.IsValid() ? tool : null);
                }
            }

            private void OnRestored(Entity<DrydockHandsCarryProbeComponent> ent, ref GridRestoredEvent args)
            {
                if (!ent.Comp.RaiseTwice || _reraising)
                    return;

                _reraising = true;
                var again = args;
                RaiseLocalEvent(ent.Owner, ref again);
                _reraising = false;
            }
        }

        private static EntityUid Spawn(IEntityManager entMan, EntityUid grid, string prototype) =>
            entMan.SpawnEntity(prototype, new EntityCoordinates(grid, 0.5f, 0.5f));

        private static EntityUid Restored(IEntityManager entMan, DrydockFidelitySystem fidelity, EntityUid grid, string prototype) =>
            fidelity.GridTreeList(grid).Single(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == prototype);

        private static string? Held(IEntityManager entMan, Hand hand) =>
            hand.HeldEntity is { } held ? entMan.GetComponent<MetaDataComponent>(held).EntityPrototype?.ID : null;

        /// <summary>
        /// Test 1: a restored interactor has its one hand again, holding the tool it held, which is the restored tool the
        /// hand's container came back with, not a new one; and the hand is active.
        /// </summary>
        [Test]
        public async Task ARestoredInteractorHoldsItsToolInItsHandAgain()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var recorder = server.System<DrydockHandsCarryRecorderSystem>();
            var containers = server.System<SharedContainerSystem>();
            var grid = map.Grid.Owner;

            DrydockLoadResult result = default!;
            var handsBefore = 0;
            await server.WaitPost(() =>
            {
                recorder.AtHead.Clear();
                var interactor = Spawn(entMan, grid, "Interactor");
                var hands = entMan.GetComponent<HandsComponent>(interactor);
                handsBefore = hands.Hands.Count;
                containers.Insert(Spawn(entMan, grid, "Screwdriver"), hands.Hands["interactor_tool"].Container!);

                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var interactor = Restored(entMan, fidelity, result.Grid, "Interactor");
                var hands = entMan.GetComponent<HandsComponent>(interactor);
                var atHead = recorder.AtHead[interactor];

                Assert.Multiple(() =>
                {
                    Assert.That(handsBefore, Is.EqualTo(1), "The control: map init gave the interactor its one hand.");
                    Assert.That(atHead.Hands, Is.Zero, "The control: at the head of the restore the interactor has no hand.");
                    Assert.That(atHead.Tool, Is.Not.Null, "The control: and its hand's container already holds the tool.");

                    Assert.That(hands.Hands.Keys, Is.EqualTo(new[] { "interactor_tool" }), "The interactor has its one hand again.");
                    Assert.That(hands.Hands["interactor_tool"].HeldEntity, Is.EqualTo(atHead.Tool), "The hand holds the restored tool, not a new one.");
                    Assert.That(Held(entMan, hands.Hands["interactor_tool"]), Is.EqualTo("Screwdriver"));
                    Assert.That(hands.ActiveHand?.Name, Is.EqualTo("interactor_tool"), "And it is the active hand.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 2: hands added at runtime, not by a map init: each comes back at its location with what it held, and the
        /// active hand is the one that was active, which is not the first added. The order is not asserted: the engine's
        /// insertion rule rebuilds it (ruled).
        /// </summary>
        [Test]
        public async Task HandsAddedAtRuntimeComeBackWithTheirLocationsContentsAndActiveHand()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var handsSystem = server.System<SharedHandsSystem>();
            var containers = server.System<SharedContainerSystem>();
            var grid = map.Grid.Owner;

            DrydockLoadResult result = default!;
            string? activeBefore = null;
            await server.WaitPost(() =>
            {
                var dummy = Spawn(entMan, grid, "DrydockHandsCarryDummy");
                var hands = entMan.GetComponent<HandsComponent>(dummy);
                handsSystem.AddHand(dummy, "a", HandLocation.Middle, hands);
                handsSystem.AddHand(dummy, "b", HandLocation.Middle, hands);
                handsSystem.AddHand(dummy, "c", HandLocation.Right, hands);
                containers.Insert(Spawn(entMan, grid, "Crowbar"), hands.Hands["b"].Container!);
                handsSystem.TrySetActiveHand(dummy, "b", hands);
                activeBefore = hands.ActiveHand?.Name;

                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var hands = entMan.GetComponent<HandsComponent>(Restored(entMan, fidelity, result.Grid, "DrydockHandsCarryDummy"));

                Assert.Multiple(() =>
                {
                    Assert.That(activeBefore, Is.EqualTo("b"), "The control: the active hand was not the first added.");
                    Assert.That(hands.Hands.Keys, Is.EquivalentTo(new[] { "a", "b", "c" }), "The same hands come back.");
                    Assert.That(hands.Hands.ToDictionary(h => h.Key, h => h.Value.Location),
                        Is.EquivalentTo(new Dictionary<string, HandLocation> { ["a"] = HandLocation.Middle, ["b"] = HandLocation.Middle, ["c"] = HandLocation.Right }),
                        "Each at its location.");
                    Assert.That(Held(entMan, hands.Hands["b"]), Is.EqualTo("Crowbar"), "The hand that held an item holds it again.");
                    Assert.That(Held(entMan, hands.Hands["a"]), Is.Null);
                    Assert.That(Held(entMan, hands.Hands["c"]), Is.Null);
                    Assert.That(hands.ActiveHand?.Name, Is.EqualTo("b"), "The active hand is the one that was active.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 3: the handler contract. A second directed raise in the same restore changes nothing and adds no component,
        /// and a raise at an entity that is gone is tolerated.
        /// </summary>
        [Test]
        public async Task ASecondRaiseChangesNothingAndAGoneEntityIsTolerated()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var handsSystem = server.System<SharedHandsSystem>();
            var grid = map.Grid.Owner;

            DrydockLoadResult result = default!;
            List<string> componentsBefore = new();
            await server.WaitPost(() =>
            {
                var dummy = Spawn(entMan, grid, "DrydockHandsCarryDummy");
                var hands = entMan.GetComponent<HandsComponent>(dummy);
                handsSystem.AddHand(dummy, "a", HandLocation.Left, hands);
                handsSystem.AddHand(dummy, "b", HandLocation.Right, hands);
                entMan.GetComponent<DrydockHandsCarryProbeComponent>(dummy).RaiseTwice = true;
                componentsBefore = entMan.GetComponents(dummy).Select(c => c.GetType().Name).OrderBy(n => n).ToList();

                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var dummy = Restored(entMan, fidelity, result.Grid, "DrydockHandsCarryDummy");
                var hands = entMan.GetComponent<HandsComponent>(dummy);
                var gone = entMan.SpawnEntity("DrydockHandsCarryDummy", new EntityCoordinates(result.Grid, 0.5f, 0.5f));
                entMan.DeleteEntity(gone);
                var ev = new GridRestoredEvent(result.Grid);

                Assert.Multiple(() =>
                {
                    Assert.That(entMan.GetComponent<DrydockHandsCarryProbeComponent>(dummy).RaiseTwice, Is.True, "The control: the probe asked for the second raise.");
                    Assert.That(hands.Hands.Keys, Is.EquivalentTo(new[] { "a", "b" }), "A second raise adds no hand twice.");
                    Assert.That(hands.SortedHands, Has.Count.EqualTo(2), "Nor lists one twice.");
                    Assert.That(entMan.GetComponents(dummy).Select(c => c.GetType().Name).OrderBy(n => n), Is.EqualTo(componentsBefore), "The handler adds no component.");
                    Assert.That(() => entMan.EventBus.RaiseLocalEvent(gone, ref ev), Throws.Nothing, "A raise at an entity that is gone is not fatal.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Test 4: an entity with a hands component and no hands carries no row.</summary>
        [Test]
        public async Task AnEntityWithNoHandsCarriesNoRow()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var grid = map.Grid.Owner;

            DrydockImageStoreResult stored = default!;
            long id = 0;
            await server.WaitPost(() =>
            {
                var dummy = Spawn(entMan, grid, "DrydockHandsCarryDummy");
                id = image.Walk(grid).Ids[dummy];
                stored = image.Store(grid);
            });

            var row = stored.Image.Entities.Single(e => e.Id == id);
            Assert.Multiple(() =>
            {
                Assert.That(row.Rows.ContainsKey("Hands"), Is.True, "The control: the hands component itself was stored.");
                Assert.That(row.Rows.ContainsKey(DrydockImageSystem.CarriedRow), Is.False, "No hands, no carried row.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Test 5: the store only reads: no component of a hand-bearing entity is dirtied by it.</summary>
        [Test]
        public async Task TheStoreDirtiesNothingOnAHandBearingEntity()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var handsSystem = server.System<SharedHandsSystem>();
            var timing = server.ResolveDependency<IGameTiming>();
            var grid = map.Grid.Owner;

            EntityUid dummy = default;
            await server.WaitPost(() =>
            {
                dummy = Spawn(entMan, grid, "DrydockHandsCarryDummy");
                handsSystem.AddHand(dummy, "a", HandLocation.Middle);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var before = entMan.GetComponents(dummy).ToDictionary(c => c.GetType().Name, c => c.LastModifiedTick);
                var stored = image.Store(grid);
                var changed = entMan.GetComponents(dummy)
                    .Where(c => !before.TryGetValue(c.GetType().Name, out var tick) || tick != c.LastModifiedTick)
                    .Select(c => c.GetType().Name)
                    .ToList();

                Assert.Multiple(() =>
                {
                    Assert.That(stored.Image.Entities.Any(e => e.Rows.ContainsKey(DrydockImageSystem.CarriedRow)), Is.True, "The control: the hand list was carried.");
                    Assert.That(before[nameof(HandsComponent)], Is.LessThan(timing.CurTick), "The control: nothing was dirtied this tick before the store.");
                    Assert.That(changed, Is.Empty, "The store dirtied nothing.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }

    /// <summary>
    /// The test's own marker on its dummy: asks the recorder for a second directed raise. Registered because the
    /// integration test assembly is a content assembly (PoolManager.cs:101).
    /// </summary>
    [RegisterComponent]
    public sealed partial class DrydockHandsCarryProbeComponent : Component
    {
        [Robust.Shared.Serialization.Manager.Attributes.DataField]
        public bool RaiseTwice;
    }
}
