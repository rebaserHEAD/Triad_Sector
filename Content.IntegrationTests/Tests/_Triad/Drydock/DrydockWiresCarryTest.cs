#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server._Triad.Wires;
using Content.Server.Wires;
using Content.Shared.Power;
using Content.Shared.Wires;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// H12: a restored entity's wire list and cut wires, carried by <see cref="WiresCarrySystem"/> on the carried-values
    /// seam. The wire list is not a data field and only map init builds it, so without the handler every restored panel is
    /// empty. The list comes back as the round's layout builds it, the same wires by action are cut, the power wire's count
    /// follows the cut wires, and the panel state is pushed.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(WiresCarrySystem))]
    public sealed class DrydockWiresCarryTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: wireLayout
  id: DrydockWiresCarryPanelLayout
  wires:
  - !type:DoorBoltWireAction
  dummyWires: 2

- type: wireLayout
  id: DrydockWiresCarryShuffledLayout
  wires:
  - !type:PowerWireAction
  - !type:PowerWireAction
    pulseTimeout: 15
  - !type:DoorBoltWireAction
  dummyWires: 5

- type: entity
  id: DrydockWiresCarryPanel
  components:
  - type: Wires
    layoutId: DrydockWiresCarryPanelLayout
  - type: UserInterface
    interfaces:
      enum.WiresUiKey.Key:
        type: WiresBoundUserInterface

- type: entity
  id: DrydockWiresCarryShuffled
  components:
  - type: Wires
    layoutId: DrydockWiresCarryShuffledLayout
    alwaysRandomize: true

- type: entity
  id: DrydockWiresCarryTwice
  components:
  - type: Wires
    layoutId: Airlock
  - type: DrydockWiresCarryProbe
    raiseTwice: true
";

        /// <summary>
        /// Records each wired entity's list length at the head of the restore, before any directed handler ran, and, for a
        /// probe that asks for it, raises the directed event a second time at its entity.
        /// </summary>
        private sealed class DrydockWiresCarryRecorderSystem : EntitySystem
        {
            public readonly Dictionary<EntityUid, int> AtHead = new();
            private bool _reraising;

            public override void Initialize()
            {
                base.Initialize();
                SubscribeLocalEvent<GridRestoringEvent>(OnHead);
                SubscribeLocalEvent<DrydockWiresCarryProbeComponent, GridRestoredEvent>(OnRestored);
            }

            private void OnHead(ref GridRestoringEvent ev)
            {
                foreach (var uid in ev.Entities)
                {
                    if (TryComp<WiresComponent>(uid, out var wires))
                        AtHead[uid] = wires.WiresList.Count;
                }
            }

            private void OnRestored(Entity<DrydockWiresCarryProbeComponent> ent, ref GridRestoredEvent args)
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

        /// <summary>
        /// Each wire with its name, worked out here independently of the handler: its action's type name, empty for none,
        /// and its rank among the wires of that name by original position.
        /// </summary>
        private static List<(string Action, int Rank, Wire Wire)> Named(WiresComponent wires)
        {
            var ranks = new Dictionary<string, int>();
            var named = new List<(string, int, Wire)>();
            foreach (var wire in wires.WiresList.OrderBy(w => w.OriginalPosition))
            {
                var action = wire.Action?.GetType().Name ?? string.Empty;
                var rank = ranks.GetValueOrDefault(action);
                ranks[action] = rank + 1;
                named.Add((action, rank, wire));
            }

            return named;
        }

        private static void CutWire(WiresComponent wires, string action, int rank) =>
            Named(wires).Single(n => n.Action == action && n.Rank == rank).Wire.IsCut = true;

        private static string[] CutNames(WiresComponent wires) =>
            Named(wires).Where(n => n.Wire.IsCut).Select(n => $"{n.Action}#{n.Rank}").ToArray();

        private static int[] CutIdsInState(SharedUserInterfaceSystem ui, EntityUid uid) =>
            ui.TryGetUiState<WiresBoundUserInterfaceState>(uid, WiresUiKey.Key, out var state)
                ? state.WiresList.Where(w => w.IsCut).Select(w => w.Id).OrderBy(id => id).ToArray()
                : new[] { -1 };

        private static int? CutPowerWires(WiresSystem system, EntityUid uid) =>
            system.TryGetData<int?>(uid, PowerWireActionKey.CutWires, out var count) ? count : null;

        /// <summary>
        /// Test 1: a restored airlock has its full wire list again, the second power wire and the bolt wire cut and no other,
        /// the power wire count at one, and a pushed panel state that shows the same two cut. A panel with no power wire,
        /// where nothing in the rebuild pushes the state, shows its cut wires as well.
        /// </summary>
        [Test]
        public async Task ARestoredEntityHasItsWireListBackWithTheSameWiresCut()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var recorder = server.System<DrydockWiresCarryRecorderSystem>();
            var wiresSystem = server.System<WiresSystem>();
            var ui = server.System<SharedUserInterfaceSystem>();
            var grid = map.Grid.Owner;

            DrydockLoadResult result = default!;
            var airlockWiresBefore = 0;
            await server.WaitPost(() =>
            {
                recorder.AtHead.Clear();
                var airlock = Spawn(entMan, grid, "Airlock");
                var airlockWires = entMan.GetComponent<WiresComponent>(airlock);
                airlockWiresBefore = airlockWires.WiresList.Count;
                CutWire(airlockWires, "PowerWireAction", 1);
                CutWire(airlockWires, "DoorBoltWireAction", 0);
                wiresSystem.SetData(airlock, PowerWireActionKey.CutWires, 1, airlockWires);

                var panel = Spawn(entMan, grid, "DrydockWiresCarryPanel");
                var panelWires = entMan.GetComponent<WiresComponent>(panel);
                CutWire(panelWires, "DoorBoltWireAction", 0);
                CutWire(panelWires, string.Empty, 1);

                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var airlock = Restored(entMan, fidelity, result.Grid, "Airlock");
                var airlockWires = entMan.GetComponent<WiresComponent>(airlock);
                var panel = Restored(entMan, fidelity, result.Grid, "DrydockWiresCarryPanel");
                var panelWires = entMan.GetComponent<WiresComponent>(panel);

                Assert.Multiple(() =>
                {
                    Assert.That(airlockWiresBefore, Is.EqualTo(8), "The control: map init gave the airlock its eight wires.");
                    Assert.That(recorder.AtHead[airlock], Is.Zero, "The control: at the head of the restore the airlock's list is empty.");
                    Assert.That(recorder.AtHead[panel], Is.Zero, "The control: and so is the panel's.");

                    Assert.That(airlockWires.WiresList, Has.Count.EqualTo(airlockWiresBefore), "The airlock has its full wire list again.");
                    Assert.That(CutNames(airlockWires), Is.EquivalentTo(new[] { "PowerWireAction#1", "DoorBoltWireAction#0" }),
                        "The same wires by action are cut, and no other.");
                    Assert.That(CutPowerWires(wiresSystem, airlock), Is.EqualTo(1), "The power wire count is the one cut power wire.");
                    Assert.That(CutIdsInState(ui, airlock),
                        Is.EqualTo(airlockWires.WiresList.Where(w => w.IsCut).Select(w => w.Id).OrderBy(id => id).ToArray()),
                        "The pushed panel state shows the same wires cut.");

                    Assert.That(panelWires.WiresList, Has.Count.EqualTo(3), "The panel has its wire list again.");
                    Assert.That(CutNames(panelWires), Is.EquivalentTo(new[] { "DoorBoltWireAction#0", "#1" }), "A cut dummy wire is cut again too.");
                    Assert.That(CutIdsInState(ui, panel),
                        Is.EqualTo(panelWires.WiresList.Where(w => w.IsCut).Select(w => w.Id).OrderBy(id => id).ToArray()),
                        "With no power wire to push it on the way, the panel state is pushed and shows the cut wires.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 2: on an entity that shuffles its wires at every build, the restored list is in another order, and the cut
        /// wires are the same wires by action and by their place in the layout prototype, not by their place in the list.
        /// </summary>
        [Test]
        public async Task ACutWireFollowsItsActionWhenTheListIsShuffled()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var wiresSystem = server.System<WiresSystem>();
            var grid = map.Grid.Owner;

            DrydockLoadResult result = default!;
            int[] orderBefore = default!;
            string[] cutBefore = default!;
            await server.WaitPost(() =>
            {
                var shuffled = Spawn(entMan, grid, "DrydockWiresCarryShuffled");
                var wires = entMan.GetComponent<WiresComponent>(shuffled);
                CutWire(wires, "PowerWireAction", 1);
                CutWire(wires, "DoorBoltWireAction", 0);
                CutWire(wires, string.Empty, 2);
                wiresSystem.SetData(shuffled, PowerWireActionKey.CutWires, 1, wires);
                orderBefore = wires.WiresList.Select(w => w.OriginalPosition).ToArray();
                cutBefore = wires.WiresList.Where(w => w.IsCut).Select(w => $"{w.Action?.GetType().Name}@{w.OriginalPosition}").OrderBy(s => s).ToArray();

                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var shuffled = Restored(entMan, fidelity, result.Grid, "DrydockWiresCarryShuffled");
                var wires = entMan.GetComponent<WiresComponent>(shuffled);
                var orderAfter = wires.WiresList.Select(w => w.OriginalPosition).ToArray();

                Assert.Multiple(() =>
                {
                    Assert.That(orderAfter, Is.Not.EqualTo(orderBefore),
                        "The control: the rebuild shuffled the list, so a place in the list names another wire now. "
                        + "One order in 40320 repeats, and then this control fails rather than letting the test pass on nothing.");
                    Assert.That(cutBefore, Has.Length.EqualTo(3), "The control: three wires were cut before the store.");
                    Assert.That(wires.WiresList.Where(w => w.IsCut).Select(w => $"{w.Action?.GetType().Name}@{w.OriginalPosition}").OrderBy(s => s),
                        Is.EqualTo(cutBefore), "The same wires by action and by place in the layout prototype are cut.");
                    Assert.That(CutPowerWires(wiresSystem, shuffled), Is.EqualTo(1));
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 3: the handler contract. A second directed raise in the same restore builds no second list, cuts nothing more,
        /// leaves the count alone and adds no component, and a raise at an entity that is gone is tolerated.
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
            var wiresSystem = server.System<WiresSystem>();
            var grid = map.Grid.Owner;

            DrydockLoadResult result = default!;
            var countBefore = 0;
            List<string> componentsBefore = new();
            await server.WaitPost(() =>
            {
                var twice = Spawn(entMan, grid, "DrydockWiresCarryTwice");
                var wires = entMan.GetComponent<WiresComponent>(twice);
                countBefore = wires.WiresList.Count;
                CutWire(wires, "PowerWireAction", 0);
                wiresSystem.SetData(twice, PowerWireActionKey.CutWires, 1, wires);
                componentsBefore = entMan.GetComponents(twice).Select(c => c.GetType().Name).OrderBy(n => n).ToList();

                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var twice = Restored(entMan, fidelity, result.Grid, "DrydockWiresCarryTwice");
                var wires = entMan.GetComponent<WiresComponent>(twice);
                var gone = entMan.SpawnEntity("DrydockWiresCarryPanel", new EntityCoordinates(result.Grid, 0.5f, 0.5f));
                entMan.DeleteEntity(gone);
                var ev = new GridRestoredEvent(result.Grid);

                Assert.Multiple(() =>
                {
                    Assert.That(entMan.GetComponent<DrydockWiresCarryProbeComponent>(twice).RaiseTwice, Is.True, "The control: the probe asked for the second raise.");
                    Assert.That(countBefore, Is.EqualTo(8), "The control: map init gave it the airlock layout's eight wires.");
                    Assert.That(wires.WiresList, Has.Count.EqualTo(countBefore), "A second raise builds no second list.");
                    Assert.That(CutNames(wires), Is.EqualTo(new[] { "PowerWireAction#0" }), "Nor cuts anything more.");
                    Assert.That(CutPowerWires(wiresSystem, twice), Is.EqualTo(1), "The count is still the one cut power wire.");
                    Assert.That(entMan.GetComponents(twice).Select(c => c.GetType().Name).OrderBy(n => n), Is.EqualTo(componentsBefore), "The handler adds no component.");
                    Assert.That(() => entMan.EventBus.RaiseLocalEvent(gone, ref ev), Throws.Nothing, "A raise at an entity that is gone is not fatal.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 6: a carried power wire count that disagrees with the carried cut wires gives way to them, since the wires are
        /// what a player sees and mends; a count left above them would keep a power wire that nobody can mend counted as cut.
        /// </summary>
        [Test]
        public async Task ACarriedCountThatDisagreesWithTheCutWiresGivesWayToThem()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var wiresSystem = server.System<WiresSystem>();
            var grid = map.Grid.Owner;

            DrydockLoadResult result = default!;
            int? countStored = null;
            await server.WaitPost(() =>
            {
                var airlock = Spawn(entMan, grid, "Airlock");
                var wires = entMan.GetComponent<WiresComponent>(airlock);
                CutWire(wires, "PowerWireAction", 0);
                wiresSystem.SetData(airlock, PowerWireActionKey.CutWires, 2, wires);
                countStored = CutPowerWires(wiresSystem, airlock);

                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var airlock = Restored(entMan, fidelity, result.Grid, "Airlock");

                Assert.Multiple(() =>
                {
                    Assert.That(countStored, Is.EqualTo(2), "The control: the count carried is two, over one cut power wire.");
                    Assert.That(CutNames(entMan.GetComponent<WiresComponent>(airlock)), Is.EqualTo(new[] { "PowerWireAction#0" }));
                    Assert.That(CutPowerWires(wiresSystem, airlock), Is.EqualTo(1), "The count is the one cut power wire.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Test 4: an entity with no cut wire carries nothing under the wires' key.</summary>
        [Test]
        public async Task AnEntityWithNoCutWireCarriesNothing()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var grid = map.Grid.Owner;

            DrydockImageStoreResult stored = default!;
            long id = 0;
            var wireCount = 0;
            await server.WaitPost(() =>
            {
                var airlock = Spawn(entMan, grid, "Airlock");
                wireCount = entMan.GetComponent<WiresComponent>(airlock).WiresList.Count;
                id = image.Walk(grid).Ids[airlock];
                stored = image.Store(grid);
            });

            var row = stored.Image.Entities.Single(e => e.Id == id);
            Assert.Multiple(() =>
            {
                Assert.That(wireCount, Is.GreaterThan(0), "The control: the airlock had wires, none of them cut.");
                Assert.That(row.Rows.ContainsKey("Wires"), Is.True, "The control: the wires component itself was stored.");
                Assert.That(row.Rows.TryGetValue(DrydockImageSystem.CarriedRow, out var carried) && carried.Contains(WiresCarrySystem.CutKey),
                    Is.False, "No cut wire, nothing carried under the wires' key.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Test 5: the store only reads: no component of a wired entity with cut wires is dirtied by it.</summary>
        [Test]
        public async Task TheStoreDirtiesNothingOnAWiredEntity()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var timing = server.ResolveDependency<IGameTiming>();
            var grid = map.Grid.Owner;

            EntityUid panel = default;
            await server.WaitPost(() =>
            {
                panel = Spawn(entMan, grid, "DrydockWiresCarryPanel");
                CutWire(entMan.GetComponent<WiresComponent>(panel), "DoorBoltWireAction", 0);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var before = entMan.GetComponents(panel).ToDictionary(c => c.GetType().Name, c => c.LastModifiedTick);
                var stored = image.Store(grid);
                var changed = entMan.GetComponents(panel)
                    .Where(c => !before.TryGetValue(c.GetType().Name, out var tick) || tick != c.LastModifiedTick)
                    .Select(c => c.GetType().Name)
                    .ToList();

                Assert.Multiple(() =>
                {
                    Assert.That(stored.Image.Entities.Any(e => e.Rows.TryGetValue(DrydockImageSystem.CarriedRow, out var carried) && carried.Contains(WiresCarrySystem.CutKey)),
                        Is.True, "The control: the cut wire was carried.");
                    Assert.That(before.Values.All(tick => tick < timing.CurTick), Is.True, "The control: nothing was dirtied this tick before the store.");
                    Assert.That(changed, Is.Empty, "The store dirtied nothing.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }

    /// <summary>
    /// The test's own marker: asks the recorder for a second directed raise. Registered because the integration test
    /// assembly is a content assembly (PoolManager.cs:101).
    /// </summary>
    [RegisterComponent]
    public sealed partial class DrydockWiresCarryProbeComponent : Component
    {
        [Robust.Shared.Serialization.Manager.Attributes.DataField]
        public bool RaiseTwice;
    }
}
