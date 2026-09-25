#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The carried-values seam: a store subscriber carries state no component member holds (<see cref="GridStoringEvent"/>),
    /// the store writes it into the entity's <see cref="DrydockImageSystem.CarriedRow"/>, and the same owner reads it back
    /// from the restore events through a lookup valid only while the load's <c>Complete</c> runs. The owner here is
    /// <see cref="DrydockCarryProbeSystem"/>, acting only on entities that hold <see cref="DrydockCarryProbeComponent"/>, so
    /// no other test's image gains a row.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockImageSystem))]
    public sealed class DrydockCarriedSeamTest
    {
        private const string Probe = "DrydockCarryProbeDummy";

        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: DrydockCarryProbeDummy
  components:
  - type: DrydockCarryProbe

- type: entity
  id: DrydockCarryProbeUnsavableDummy
  save: false
  components:
  - type: DrydockCarryProbe
";

        /// <summary>
        /// The fake owner: carries <see cref="DrydockCarryProbeComponent.Live"/> and <see cref="DrydockCarryProbeComponent.Second"/>,
        /// which are not data fields, so only the carried row brings them back, and records what each restore raise showed it.
        /// </summary>
        private sealed class DrydockCarryProbeSystem : EntitySystem
        {
            public const string LiveKey = "Probe.Live";
            public const string SecondKey = "Probe.Second";

            public readonly List<EntityUid> Directed = new();
            public readonly List<EntityUid> DeletedAtHead = new();
            public readonly Dictionary<EntityUid, bool> HasAfterDelete = new();
            public DrydockCarried? LastLookup;

            public override void Initialize()
            {
                base.Initialize();
                SubscribeLocalEvent<DrydockCarryProbeComponent, GridStoringEvent>(OnStoring);
                SubscribeLocalEvent<GridRestoringEvent>(OnHead);
                SubscribeLocalEvent<DrydockCarryProbeComponent, GridRestoredEvent>(OnRestored);
            }

            public void Clear()
            {
                Directed.Clear();
                DeletedAtHead.Clear();
                HasAfterDelete.Clear();
                LastLookup = null;
            }

            private void OnStoring(Entity<DrydockCarryProbeComponent> ent, ref GridStoringEvent args)
            {
                var probe = ent.Comp;
                if (probe.DirtyAtStore)
                    Dirty(ent.Owner, Transform(ent.Owner));

                if (probe.CarryNothing)
                    return;

                if (probe.SecondFirst && probe.Second != 0)
                    args.Carry(SecondKey, probe.Second);

                args.Carry(LiveKey, probe.Live);

                if (!probe.SecondFirst && probe.Second != 0)
                    args.Carry(SecondKey, probe.Second);

                switch (probe.Fault)
                {
                    case DrydockCarryProbeFault.Reference:
                        args.Carry("Probe.Reference", new Dictionary<string, EntityUid> { ["self"] = ent.Owner });
                        break;
                    case DrydockCarryProbeFault.Holder:
                        args.Carry("Probe.Holder", new DrydockCarryProbeHolder { Target = ent.Owner });
                        break;
                    case DrydockCarryProbeFault.Time:
                        args.Carry("Probe.Time", TimeSpan.FromSeconds(5));
                        break;
                    case DrydockCarryProbeFault.Unserializable:
                        args.Carry("Probe.Unserializable", new DrydockCarryProbeOpaque());
                        break;
                    case DrydockCarryProbeFault.Duplicate:
                        args.Carry(LiveKey, probe.Live);
                        break;
                }
            }

            private void OnHead(ref GridRestoringEvent ev)
            {
                foreach (var uid in ev.Entities)
                {
                    if (!TryComp<DrydockCarryProbeComponent>(uid, out var probe))
                        continue;

                    probe.HasAtHead = ev.Carried?.Has(uid, LiveKey);
                    if (ev.TryGetCarried<int>(uid, LiveKey, out var live))
                        probe.ReadAtHead = live;

                    if (!probe.DeleteAtHead)
                        continue;

                    Del(uid);
                    DeletedAtHead.Add(uid);
                    HasAfterDelete[uid] = ev.Carried!.Has(uid, LiveKey);
                }
            }

            private void OnRestored(Entity<DrydockCarryProbeComponent> ent, ref GridRestoredEvent args)
            {
                Directed.Add(ent.Owner);
                LastLookup = args.Carried;

                var probe = ent.Comp;
                probe.HasDirected = args.Carried?.Has(ent.Owner, LiveKey);
                if (args.TryGetCarried<int>(LiveKey, out var live))
                {
                    probe.ReadDirected = live;
                    probe.Live = live;
                }

                if (args.TryGetCarried<int>(SecondKey, out var second))
                    probe.Second = second;

                if (!probe.ReadWrongType)
                    return;

                try
                {
                    args.TryGetCarried<Dictionary<string, int>>(LiveKey, out _);
                }
                catch (DrydockCarriedException e)
                {
                    probe.WrongTypeError = e.Message;
                }
            }
        }

        private sealed class CaptureShell(IConsoleHost host) : IConsoleShell
        {
            public readonly List<string> Lines = new();

            public IConsoleHost ConsoleHost { get; } = host;
            public bool IsLocal => true;
            public bool IsServer => true;
            public ICommonSession? Player => null;

            public void ExecuteCommand(string command) { }
            public void RemoteExecuteCommand(string command) { }
            public void WriteLine(string text) => Lines.Add(text);
            public void WriteLine(FormattedMessage message) => Lines.Add(message.ToString());
            public void WriteError(string text) => Lines.Add($"ERROR: {text}");
            public void Clear() => Lines.Clear();
        }

        private static EntityUid SpawnProbe(IEntityManager entMan, EntityUid grid, int live, Action<DrydockCarryProbeComponent>? configure = null, string prototype = Probe)
        {
            var uid = entMan.SpawnEntity(prototype, new EntityCoordinates(grid, 0.5f, 0.5f));
            var probe = entMan.GetComponent<DrydockCarryProbeComponent>(uid);
            probe.Live = live;
            configure?.Invoke(probe);
            return uid;
        }

        private static List<Entity<DrydockCarryProbeComponent>> Probes(IEntityManager entMan, DrydockFidelitySystem fidelity, EntityUid grid) =>
            fidelity.GridTreeList(grid)
                .Where(entMan.HasComponent<DrydockCarryProbeComponent>)
                .Select(uid => new Entity<DrydockCarryProbeComponent>(uid, entMan.GetComponent<DrydockCarryProbeComponent>(uid)))
                .ToList();

        private static List<string> CarriedRows(DrydockImage image) =>
            image.Entities
                .Where(entity => entity.Rows.ContainsKey(DrydockImageSystem.CarriedRow))
                .Select(entity => entity.Rows[DrydockImageSystem.CarriedRow])
                .OrderBy(row => row, StringComparer.Ordinal)
                .ToList();

        /// <summary>Test 1: a carried value comes back through the directed raise, and the head reads the same value.</summary>
        [Test]
        public async Task ACarriedValueComesBackThroughTheDirectedRaiseAndTheHeadReadsTheSame()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var owner = server.System<DrydockCarryProbeSystem>();
            var grid = map.Grid.Owner;

            DrydockImageStoreResult stored = default!;
            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                owner.Clear();
                SpawnProbe(entMan, grid, 42);

                // The control: the same value on a probe that carries nothing, so only the component row could bring it back.
                SpawnProbe(entMan, grid, 17, probe => probe.CarryNothing = true);

                stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var probes = Probes(entMan, fidelity, result.Grid);
                var carrying = probes.Single(probe => !probe.Comp.CarryNothing);
                var control = probes.Single(probe => probe.Comp.CarryNothing);

                Assert.Multiple(() =>
                {
                    Assert.That(stored.Whole, Is.True);
                    Assert.That(CarriedRows(stored.Image), Has.Count.EqualTo(1), "One entity carried a value.");
                    Assert.That(control.Comp.Live, Is.EqualTo(0), "The control: the owner's value is no data field, so without the seam it comes back as the default.");

                    Assert.That(owner.Directed, Does.Contain(carrying.Owner));
                    Assert.That(carrying.Comp.ReadDirected, Is.EqualTo(42), "The directed raise reads the value carried.");
                    Assert.That(carrying.Comp.ReadAtHead, Is.EqualTo(42), "The head reads the same value for the same entity.");
                    Assert.That(carrying.Comp.Live, Is.EqualTo(42), "And the owner rebuilt its state from it.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 2: an image with no probe has no carried row at all, which is what keeps every other image byte-identical; an
        /// owner that carried nothing gets false from <c>TryGetCarried</c> and <c>Has</c>.
        /// </summary>
        [Test]
        public async Task NothingCarriedWritesNoRowAndReadsAsAbsent()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var owner = server.System<DrydockCarryProbeSystem>();
            var grid = map.Grid.Owner;

            DrydockImageStoreResult withoutProbe = default!;
            DrydockImageStoreResult withSilentProbe = default!;
            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                owner.Clear();
                entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, 0.5f, 0.5f));
                entMan.SpawnEntity("APCBasic", new EntityCoordinates(grid, 0.5f, 0.5f));
                withoutProbe = image.Store(grid);

                SpawnProbe(entMan, grid, 9, probe => probe.CarryNothing = true);
                withSilentProbe = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(withSilentProbe.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var probe = Probes(entMan, fidelity, result.Grid).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(withoutProbe.Image.Entities, Has.Count.GreaterThan(2), "The control: the store wrote the grid, the wall and the APC.");
                    Assert.That(CarriedRows(withoutProbe.Image), Is.Empty, "An image with no probe has no carried row.");
                    Assert.That(CarriedRows(withSilentProbe.Image), Is.Empty, "An owner that carried nothing adds no row.");
                    Assert.That(withSilentProbe.Whole, Is.True);

                    Assert.That(owner.Directed, Does.Contain(probe.Owner), "The control: the owner's directed handler ran.");
                    Assert.That(probe.Comp.HasAtHead, Is.False, "Has answers false at the head.");
                    Assert.That(probe.Comp.HasDirected, Is.False, "And at the directed raise.");
                    Assert.That(probe.Comp.ReadDirected, Is.Null, "TryGetCarried answers false.");
                    Assert.That(probe.Comp.ReadAtHead, Is.Null);
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Test 3a: an entity the walk leaves out is never raised at, so it writes no row and refuses nothing.</summary>
        [Test]
        public async Task AnEntityTheWalkLeavesOutIsNeverAskedToCarry()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var owner = server.System<DrydockCarryProbeSystem>();
            var grid = map.Grid.Owner;

            DrydockImageStoreResult stored = default!;
            DrydockWalk walk = default;
            await server.WaitPost(() =>
            {
                owner.Clear();
                SpawnProbe(entMan, grid, 7, prototype: "DrydockCarryProbeUnsavableDummy");
                SpawnProbe(entMan, grid, 8);
                walk = image.Walk(grid);
                stored = image.Store(grid);
            });

            var carrying = stored.Image.Entities.Where(entity => entity.Rows.ContainsKey(DrydockImageSystem.CarriedRow)).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(walk.UnsavedByPrototype.GetValueOrDefault("DrydockCarryProbeUnsavableDummy"), Is.EqualTo(1), "The walk left the unsavable probe out.");
                Assert.That(carrying.Select(entity => entity.Prototype), Is.EqualTo(new[] { Probe }), "Only the savable probe carried; the control is that it did.");
                Assert.That(stored.UnwritableCarried, Is.Empty);
                Assert.That(stored.Whole, Is.True);
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 3b: an entity a head handler deletes is skipped by the directed raise, so its value is never decoded and
        /// nothing throws; the lookup still answers for its row, because it answers about the image.
        /// </summary>
        [Test]
        public async Task AnEntityGoneBeforeItsDirectedRaiseIsNeverReadAndNothingThrows()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var owner = server.System<DrydockCarryProbeSystem>();
            var grid = map.Grid.Owner;

            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                owner.Clear();
                SpawnProbe(entMan, grid, 5, probe => probe.DeleteAtHead = true);
                SpawnProbe(entMan, grid, 6);
                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var survivor = Probes(entMan, fidelity, result.Grid).Single();
                var gone = owner.DeletedAtHead.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(entMan.EntityExists(gone), Is.False);
                    Assert.That(owner.Directed, Does.Not.Contain(gone), "The directed raise skips an entity a head handler deleted.");
                    Assert.That(owner.HasAfterDelete[gone], Is.True, "The lookup answers about the image, whatever became of the entity.");
                    Assert.That(survivor.Comp.ReadDirected, Is.EqualTo(6), "The control: the other probe restored as usual.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Test 4: the restored ship, stored again, carries the same values in the same bytes.</summary>
        [Test]
        public async Task ASecondStoreOfTheRestoredShipCarriesTheSameBytes()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var owner = server.System<DrydockCarryProbeSystem>();
            var grid = map.Grid.Owner;

            DrydockImageStoreResult first = default!;
            DrydockImageStoreResult second = default!;
            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                owner.Clear();
                SpawnProbe(entMan, grid, 11, probe => probe.Second = 12);
                first = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(first.Image, map.MapUid);
                second = image.Store(result.Grid);
            });

            await server.WaitAssertion(() =>
            {
                var probe = Probes(entMan, fidelity, result.Grid).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(probe.Comp.Live, Is.EqualTo(11));
                    Assert.That(probe.Comp.Second, Is.EqualTo(12));
                    Assert.That(CarriedRows(first.Image), Has.Count.EqualTo(1), "The control: the first image carried a row.");
                    Assert.That(CarriedRows(second.Image), Is.EqualTo(CarriedRows(first.Image)), "The second store carries the same bytes.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 5: a value the guard or the serializer refuses is listed in <see cref="DrydockImageStoreResult.UnwritableCarried"/>
        /// with its entity and prototype, is left out of the row, and makes the result not whole, which the round-trip command
        /// refuses on; the entity's other values stay. A key carried twice throws.
        /// </summary>
        [Test]
        public async Task ARefusedValueIsListedLeftOutAndRefusesTheStore()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var owner = server.System<DrydockCarryProbeSystem>();
            var host = server.ResolveDependency<IConsoleHost>();
            var grid = map.Grid.Owner;

            var faults = new[]
            {
                DrydockCarryProbeFault.Reference, DrydockCarryProbeFault.Holder, DrydockCarryProbeFault.Time, DrydockCarryProbeFault.Unserializable,
            };

            DrydockImageStoreResult stored = default!;
            var probes = new Dictionary<DrydockCarryProbeFault, EntityUid>();
            await server.WaitPost(() =>
            {
                owner.Clear();
                for (var i = 0; i < faults.Length; i++)
                {
                    var fault = faults[i];
                    probes[fault] = SpawnProbe(entMan, grid, i + 1, probe => probe.Fault = fault);
                }

                stored = image.Store(grid);
            });

            var byKey = stored.UnwritableCarried.ToDictionary(value => value.Key);
            var rows = CarriedRows(stored.Image);
            Assert.Multiple(() =>
            {
                Assert.That(stored.Whole, Is.False, "A refused value makes the store not whole.");
                Assert.That(stored.Unwritable, Is.Empty, "The control: no manifest member was lost, so the carried value alone refuses it.");
                Assert.That(byKey.Keys, Is.EquivalentTo(new[] { "Probe.Reference", "Probe.Holder", "Probe.Time", "Probe.Unserializable" }));

                Assert.That(byKey["Probe.Reference"].Message, Does.Contain(nameof(EntityUid)), "An entity reference inside a dictionary is refused.");
                Assert.That(byKey["Probe.Holder"].Message, Does.Contain(nameof(DrydockCarryProbeHolder.Target)), "One inside a data definition's field is refused, naming the field.");
                Assert.That(byKey["Probe.Time"].Message, Does.Contain(nameof(TimeSpan)), "A time is refused.");
                Assert.That(byKey["Probe.Unserializable"].Exception, Is.EqualTo(nameof(ArgumentException)), "A type the serializer cannot write is refused by the serializer.");

                foreach (var fault in faults)
                {
                    var key = $"Probe.{fault}";
                    Assert.That(byKey[key].Entity, Is.EqualTo(probes[fault]), $"{key} names its entity.");
                    Assert.That(byKey[key].Prototype, Is.EqualTo(Probe), $"{key} names its prototype.");
                }

                Assert.That(rows, Has.Count.EqualTo(faults.Length), "Each probe still wrote a row for what it could carry.");
                Assert.That(rows, Has.All.Contain(DrydockCarryProbeSystem.LiveKey), "The value beside a refused one stays.");
                foreach (var key in byKey.Keys)
                    Assert.That(rows, Has.None.Contain(key), $"{key} is left out of the row.");
            });

            var shell = new CaptureShell(host);
            await server.WaitPost(() =>
                host.AvailableCommands["drydock_image_roundtrip"].Execute(shell, entMan.GetNetEntity(grid).ToString(), new[] { entMan.GetNetEntity(grid).ToString() }));
            Assert.Multiple(() =>
            {
                Assert.That(shell.Lines.Any(line => line.StartsWith("ERROR: Refused:", StringComparison.Ordinal) && line.Contains("carried value(s)", StringComparison.Ordinal)), Is.True,
                    $"The round-trip command refuses a store that is not whole: {string.Join(" | ", shell.Lines)}");
                Assert.That(entMan.EntityExists(grid), Is.True, "And leaves the grid where it is.");
            });

            await server.WaitPost(() =>
            {
                SpawnProbe(entMan, grid, 1, probe => probe.Fault = DrydockCarryProbeFault.Duplicate);
                Assert.That(() => image.Store(grid), Throws.InvalidOperationException.With.Message.Contain(DrydockCarryProbeSystem.LiveKey),
                    "Two carries under one key on one entity throw.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 6: the lookup throws once the load's <c>Complete</c> has returned, and a present value asked for as a type it
        /// does not decode as throws, naming the prototype, the key and the type.
        /// </summary>
        [Test]
        public async Task TheLookupClosesAfterCompleteAndAWrongTypeThrows()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var owner = server.System<DrydockCarryProbeSystem>();
            var grid = map.Grid.Owner;

            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                owner.Clear();
                SpawnProbe(entMan, grid, 3, probe => probe.ReadWrongType = true);
                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var probe = Probes(entMan, fidelity, result.Grid).Single();
                var lookup = owner.LastLookup!;
                Assert.Multiple(() =>
                {
                    Assert.That(probe.Comp.ReadDirected, Is.EqualTo(3), "The control: the lookup answered while the load ran.");
                    Assert.That(probe.Comp.WrongTypeError, Does.Contain(Probe).And.Contain(DrydockCarryProbeSystem.LiveKey).And.Contain("Dictionary"),
                        "A present value that does not decode as the type asked throws, naming the prototype, the key and the type.");
                    Assert.That(() => lookup.TryGet<int>(probe.Owner, DrydockCarryProbeSystem.LiveKey, out _), Throws.InvalidOperationException,
                        "After Complete the lookup is closed.");
                    Assert.That(() => lookup.Has(probe.Owner, DrydockCarryProbeSystem.LiveKey), Throws.InvalidOperationException);
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Test 7: the row's bytes do not depend on the order the owner carried its keys in.</summary>
        [Test]
        public async Task TheRowIsTheSameWhateverOrderTheValuesWereCarriedIn()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var owner = server.System<DrydockCarryProbeSystem>();
            var grid = map.Grid.Owner;

            List<string> liveFirst = new();
            List<string> secondFirst = new();
            await server.WaitPost(() =>
            {
                owner.Clear();
                var uid = SpawnProbe(entMan, grid, 3, probe => probe.Second = 9);
                liveFirst = CarriedRows(image.Store(grid).Image);
                entMan.GetComponent<DrydockCarryProbeComponent>(uid).SecondFirst = true;
                secondFirst = CarriedRows(image.Store(grid).Image);
            });

            Assert.Multiple(() =>
            {
                Assert.That(liveFirst.Single(), Does.Contain(DrydockCarryProbeSystem.LiveKey).And.Contain(DrydockCarryProbeSystem.SecondKey),
                    "The control: both keys were carried.");
                Assert.That(secondFirst, Is.EqualTo(liveFirst), "The same bytes in either order.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 8: a store with a subscriber that only reads leaves every component of every entity it wrote unmodified: the
        /// seam's own path dirties nothing. The same detector, given a subscriber that dirties, reports it.
        /// </summary>
        [Test]
        public async Task AStoreWithAReadOnlySubscriberDirtiesNothing()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var owner = server.System<DrydockCarryProbeSystem>();
            var timing = server.ResolveDependency<IGameTiming>();
            var grid = map.Grid.Owner;

            EntityUid probeUid = default;
            await server.WaitPost(() =>
            {
                owner.Clear();
                probeUid = SpawnProbe(entMan, grid, 4, probe => probe.Second = 5);
                entMan.SpawnEntity("APCBasic", new EntityCoordinates(grid, 0.5f, 0.5f));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var aboard = image.Walk(grid).Aboard;

                // The component names whose modified tick a store moved, on every entity it wrote.
                List<string> Changed(Func<DrydockImageStoreResult> store, out DrydockImageStoreResult stored)
                {
                    var before = aboard
                        .SelectMany(uid => entMan.GetComponents(uid).Select(c => (Key: (uid, c.GetType().Name), c.LastModifiedTick)))
                        .ToDictionary(entry => entry.Key, entry => entry.LastModifiedTick);
                    stored = store();
                    return aboard
                        .SelectMany(uid => entMan.GetComponents(uid).Select(c => (Key: (uid, c.GetType().Name), c.LastModifiedTick)))
                        .Where(entry => !before.TryGetValue(entry.Key, out var tick) || tick != entry.LastModifiedTick)
                        .Select(entry => entry.Key.Name)
                        .ToList();
                }

                var xformBefore = entMan.GetComponent<TransformComponent>(probeUid).LastModifiedTick;
                var readOnly = Changed(() => image.Store(grid), out var stored);

                // The control: the same detector, given a subscriber that dirties the probe's transform, sees it.
                entMan.GetComponent<DrydockCarryProbeComponent>(probeUid).DirtyAtStore = true;
                var mutating = Changed(() => image.Store(grid), out _);

                Assert.Multiple(() =>
                {
                    Assert.That(CarriedRows(stored.Image), Has.Count.EqualTo(1), "The control: the subscriber carried.");
                    Assert.That(xformBefore, Is.LessThan(timing.CurTick), "The control: nothing was dirtied this tick before the store, so a dirty would show.");
                    Assert.That(mutating, Does.Contain(nameof(TransformComponent)), "The control: a subscriber that dirties is caught.");
                    Assert.That(readOnly, Is.Empty, "A store with a read-only subscriber dirtied nothing.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }

    /// <summary>
    /// The fake owner's component for <see cref="DrydockCarriedSeamTest"/>. Registered because the integration test assembly
    /// is a content assembly (PoolManager.cs:101).
    /// </summary>
    [RegisterComponent]
    public sealed partial class DrydockCarryProbeComponent : Component
    {
        // The owner's state: not data fields, so only the carried row brings them back.
        public int Live;
        public int Second;

        // What the test asks of the owner: data fields, so they reach the loaded entity through its component row.
        [DataField]
        public bool CarryNothing;

        [DataField]
        public bool SecondFirst;

        [DataField]
        public DrydockCarryProbeFault Fault;

        [DataField]
        public bool DeleteAtHead;

        [DataField]
        public bool ReadWrongType;

        /// <summary>Breaks the read-only contract on purpose, so test 8 can show its detector catches a subscriber that dirties.</summary>
        [DataField]
        public bool DirtyAtStore;

        // What the owner saw during the load.
        public bool? HasAtHead;
        public int? ReadAtHead;
        public bool? HasDirected;
        public int? ReadDirected;
        public string? WrongTypeError;
    }

    public enum DrydockCarryProbeFault : byte
    {
        None,
        Reference,
        Holder,
        Time,
        Unserializable,
        Duplicate,
    }

    /// <summary>A data definition holding an entity reference in a field, which the carry guard has to find.</summary>
    [DataDefinition]
    public sealed partial class DrydockCarryProbeHolder
    {
        [DataField]
        public EntityUid? Target;
    }

    /// <summary>A type with no serializer and no data definition, which only the serializer can refuse.</summary>
    public sealed class DrydockCarryProbeOpaque
    {
        public int Value = 1;
    }
}
