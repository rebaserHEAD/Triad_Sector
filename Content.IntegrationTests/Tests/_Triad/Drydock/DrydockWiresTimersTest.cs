#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server._Triad.Wires;
using Content.Server.Access;
using Content.Server.Doors;
using Content.Server.Electrocution;
using Content.Server.Power;
using Content.Server.Speech;
using Content.Server.VendingMachines;
using Content.Server.Wires;
using Content.Shared.Access.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Electrocution;
using Content.Shared.Power;
using Content.Shared.Speech;
using Content.Shared.VendingMachines;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Reflection;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// H12-timers: a restored entity's running wire timers, carried by <see cref="WiresCarrySystem"/> on the carried-values
    /// seam. A pulse arms a timer that reverts it; the timer is not a data field, so without the carry the pulse's effect
    /// stays for ever. Each timer runs again for the seconds it had left, from when its entity unpauses.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(WiresCarrySystem))]
    public sealed class DrydockWiresTimersTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: wireLayout
  id: DrydockWiresTimersDoorLayout
  wires:
  - !type:AccessWireAction
    pulseTimeout: 4
  - !type:LogWireAction
    pulseTimeout: 4
  - !type:DoorSafetyWireAction
    timeout: 4
  - !type:DoorTimingWireAction
    timeout: 4

- type: entity
  parent: Airlock
  id: DrydockWiresTimersDoor
  components:
  - type: Wires
    layoutId: DrydockWiresTimersDoorLayout
  - type: DrydockWiresTimersProbe

- type: wireLayout
  id: DrydockWiresTimersBoxLayout
  wires:
  - !type:PowerWireAction
    pulseTimeout: 4
  - !type:DrydockWiresTimersProbeWireAction

- type: entity
  id: DrydockWiresTimersBox
  components:
  - type: Wires
    layoutId: DrydockWiresTimersBoxLayout
  - type: Electrified
    enabled: false
  - type: DrydockWiresTimersProbe
";

        /// <summary>
        /// Every timed wire the carry re-arms, as the audit pins it: the action that starts the timer, its key, and the
        /// private method its end calls, by the type that declares it. An image stored before an upstream rename of one of
        /// these carries the old name and drops that timer with a warning at its load, so a rename fails here first, where
        /// whoever merges it can decide what the stored images need.
        /// </summary>
        private static readonly (Type Action, Func<Enum> Key, Type ExpiryType, string Expiry)[] Bindings =
        {
            (typeof(AccessWireAction), () => Nested(typeof(AccessWireAction), "PulseTimeoutKey", "Key"), typeof(AccessWireAction), "AwaitPulseCancel"),
            (typeof(LogWireAction), () => Nested(typeof(LogWireAction), "PulseTimeoutKey", "Key"), typeof(LogWireAction), "AwaitPulseCancel"),
            (typeof(DoorSafetyWireAction), () => Nested(typeof(DoorSafetyWireAction), "PulseTimeoutKey", "Key"), typeof(DoorSafetyWireAction), "AwaitSafetyTimerFinish"),
            (typeof(DoorTimingWireAction), () => Nested(typeof(DoorTimingWireAction), "PulseTimeoutKey", "Key"), typeof(DoorTimingWireAction), "AwaitTimingTimerFinish"),
            (typeof(PowerWireAction), () => PowerWireActionKey.PulseCancel, typeof(PowerWireAction), "AwaitPulseCancel"),
            (typeof(PowerWireAction), () => PowerWireActionKey.ElectrifiedCancel, typeof(PowerWireAction), "AwaitElectrifiedCancel"),
            (typeof(ListenWireAction), () => ListenWireActionKey.TimeoutKey, typeof(BaseToggleWireAction), "AwaitPulseCancel"),
            (typeof(VendingMachineContrabandWireAction), () => ContrabandWireKey.TimeoutKey, typeof(BaseToggleWireAction), "AwaitPulseCancel"),
        };

        /// <summary>A private enum nested in <paramref name="action"/>, by its name and its member's, as the carry meets it.</summary>
        private static Enum Nested(Type action, string type, string member) =>
            (Enum) Enum.Parse(action.GetNestedType(type, BindingFlags.NonPublic)
                              ?? throw new InvalidOperationException($"{action.Name} declares no {type}"), member);

        /// <summary>
        /// The audit, part 1, with no server: each binding's key exists, and its end still binds through
        /// <see cref="WiresCarrySystem.Expiry"/> on an instance of its action, so an upstream rename fails the suite.
        /// </summary>
        [Test]
        public void EveryAuditedTimerEndBinds()
        {
            Assert.Multiple(() =>
            {
                foreach (var binding in Bindings)
                {
                    var action = (IWireAction) Activator.CreateInstance(binding.Action)!;
                    Assert.That(WiresCarrySystem.Expiry(action, binding.ExpiryType.Name, binding.Expiry), Is.Not.Null,
                        $"{binding.Action.Name}'s timer end {binding.ExpiryType.Name}.{binding.Expiry} has to bind on an instance of it.");
                    Assert.That(() => binding.Key(), Throws.Nothing, $"{binding.Action.Name}'s timer key has to exist.");
                }
            });
        }

        /// <summary>
        /// The audit, part 2: each audited key travels as the engine writes an enum and comes back as the same value, the
        /// four private <c>PulseTimeoutKey</c>s kept apart though they share a name.
        /// </summary>
        [Test]
        public async Task EveryAuditedKeyRoundTripsAsAnEnumReference()
        {
            await using var pair = await PoolManager.GetServerClient();
            var reflection = pair.Server.ResolveDependency<IReflectionManager>();

            Assert.Multiple(() =>
            {
                foreach (var binding in Bindings)
                {
                    var key = binding.Key();
                    var reference = reflection.GetEnumReference(key);
                    Assert.That(reflection.TryParseEnumReference(reference, out var back, false), Is.True, $"{reference} has to parse back.");
                    Assert.That(back, Is.EqualTo(key), $"{reference} has to parse back to its own key, not another action's.");
                }
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>For a probe that asks for it, raises the directed restore event a second time, inside the load.</summary>
        private sealed class DrydockWiresTimersRecorderSystem : EntitySystem
        {
            public bool Reraised;
            private bool _reraising;

            public override void Initialize()
            {
                base.Initialize();
                SubscribeLocalEvent<DrydockWiresTimersProbeComponent, GridRestoredEvent>(OnRestored);
            }

            private void OnRestored(Entity<DrydockWiresTimersProbeComponent> ent, ref GridRestoredEvent args)
            {
                if (!ent.Comp.RaiseTwice || _reraising)
                    return;

                _reraising = true;
                var again = args;
                RaiseLocalEvent(ent.Owner, ref again);
                _reraising = false;
                Reraised = true;
            }
        }

        /// <summary>A starter, as its test case runs it: the prototype, the wire, the start, and the effect in place.</summary>
        public sealed record Starter(string Name, string Prototype, string Wire, Enum Key, Action<IEntityManager, EntityUid, Wire> Start,
            Func<IEntityManager, WiresSystem, EntityUid, bool> EffectInPlace)
        {
            public override string ToString() => Name;
        }

        private static bool Pulsed(WiresSystem wires, EntityUid uid) => wires.TryGetData<bool>(uid, PowerWireActionKey.Pulsed, out var pulsed) && pulsed;

        private static readonly Starter[] Starters =
        {
            new("access", "DrydockWiresTimersDoor", "AccessWireAction#0", Bindings[0].Key(),
                (_, _, wire) => wire.Action!.Pulse(EntityUid.Invalid, wire),
                (entMan, _, uid) => !entMan.GetComponent<AccessReaderComponent>(uid).Enabled),
            new("log", "DrydockWiresTimersDoor", "LogWireAction#0", Bindings[1].Key(),
                (_, _, wire) => wire.Action!.Pulse(EntityUid.Invalid, wire),
                (entMan, _, uid) => entMan.GetComponent<AccessReaderComponent>(uid).LoggingDisabled),
            new("door safety", "DrydockWiresTimersDoor", "DoorSafetyWireAction#0", Bindings[2].Key(),
                (_, _, wire) => wire.Action!.Pulse(EntityUid.Invalid, wire),
                (entMan, _, uid) => !entMan.GetComponent<AirlockComponent>(uid).Safety),
            new("door timing", "DrydockWiresTimersDoor", "DoorTimingWireAction#0", Bindings[3].Key(),
                (_, _, wire) => wire.Action!.Pulse(EntityUid.Invalid, wire),
                (entMan, _, uid) => Math.Abs(entMan.GetComponent<AirlockComponent>(uid).AutoCloseDelayModifier - 0.5f) < 1e-4f),
            new("power pulse", "DrydockWiresTimersBox", "PowerWireAction#0", Bindings[4].Key(),
                (_, _, wire) => wire.Action!.Pulse(EntityUid.Invalid, wire),
                (_, wires, uid) => Pulsed(wires, uid)),
            // The electrified timer starts from the power wire's update while pulsed and powered (PowerWireAction.cs:153-159);
            // the box has no power receiver, so it reads powered.
            new("power electrified", "DrydockWiresTimersBox", "PowerWireAction#0", Bindings[5].Key(),
                (_, _, wire) =>
                {
                    wire.Action!.Pulse(EntityUid.Invalid, wire);
                    wire.Action.Update(wire);
                },
                (entMan, _, uid) => entMan.GetComponent<ElectrifiedComponent>(uid).Enabled),
            new("toggle", "DrydockWiresTimersBox", "DrydockWiresTimersProbeWireAction#0", DrydockWiresTimersProbeKey.Timeout,
                (_, _, wire) => wire.Action!.Pulse(EntityUid.Invalid, wire),
                (entMan, _, uid) => !entMan.GetComponent<DrydockWiresTimersProbeComponent>(uid).On),
        };

        private static IEnumerable<Starter> StarterCases() => Starters;

        private static EntityUid Spawn(IEntityManager entMan, EntityUid grid, string prototype) =>
            entMan.SpawnEntity(prototype, new EntityCoordinates(grid, 0.5f, 0.5f));

        private static EntityUid Restored(IEntityManager entMan, DrydockFidelitySystem fidelity, EntityUid grid, string prototype) =>
            fidelity.GridTreeList(grid).Single(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == prototype);

        private static Wire WireNamed(WiresComponent wires, string name)
        {
            var ranks = new Dictionary<string, int>();
            foreach (var wire in wires.WiresList.OrderBy(w => w.OriginalPosition))
            {
                var action = wire.Action?.GetType().Name ?? string.Empty;
                var rank = ranks.GetValueOrDefault(action);
                ranks[action] = rank + 1;
                if ($"{action}#{rank}" == name)
                    return wire;
            }

            throw new InvalidOperationException($"no wire {name}");
        }

        private static float? TimeLeft(WiresSystem wires, EntityUid uid, Enum key) =>
            wires.RunningTimers(uid).Where(t => Equals(t.Key, key)).Select(t => (float?) t.TimeLeft).FirstOrDefault();

        private static int Ticks(IGameTiming timing, double seconds) => (int) Math.Ceiling(seconds / timing.TickPeriod.TotalSeconds);

        /// <summary>
        /// Test 1, one case per starter: a pulse's timer runs again after a store and a load onto a live map, for the seconds
        /// it had left, not its full delay; its key is back in the state data a cut reads; its effect is still in place; and
        /// at its end the effect reverts.
        /// </summary>
        [TestCaseSource(nameof(StarterCases))]
        public async Task ARunningTimerRunsAgainForTheTimeItHadLeft(Starter starter)
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var timing = server.ResolveDependency<IGameTiming>();
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var wires = server.System<WiresSystem>();
            var grid = map.Grid.Owner;

            EntityUid source = default;
            float? full = null;
            await server.WaitPost(() =>
            {
                source = Spawn(entMan, grid, starter.Prototype);
                var wire = WireNamed(entMan.GetComponent<WiresComponent>(source), starter.Wire);
                starter.Start(entMan, source, wire);
                full = TimeLeft(wires, source, starter.Key);
            });

            await pair.RunTicksSync(Ticks(timing, 1));

            DrydockLoadResult result = default!;
            float? stored = null, restored = null;
            bool keyBack = false, effectBefore = false, effectAfterLoad = false;
            EntityUid loaded = default;
            await server.WaitPost(() =>
            {
                stored = TimeLeft(wires, source, starter.Key);
                effectBefore = starter.EffectInPlace(entMan, wires, source);
                var image1 = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(image1.Image, map.MapUid);
                loaded = Restored(entMan, fidelity, result.Grid, starter.Prototype);
                restored = TimeLeft(wires, loaded, starter.Key);
                keyBack = wires.HasData(loaded, starter.Key);
                effectAfterLoad = starter.EffectInPlace(entMan, wires, loaded);
            });

            await pair.RunTicksSync(Ticks(timing, (restored ?? 0) + 1));

            bool effectAtEnd = true;
            float? leftAtEnd = null;
            await server.WaitPost(() =>
            {
                effectAtEnd = starter.EffectInPlace(entMan, wires, loaded);
                leftAtEnd = TimeLeft(wires, loaded, starter.Key);
            });

            Assert.Multiple(() =>
            {
                Assert.That(full, Is.Not.Null, "The control: the start armed the timer.");
                Assert.That(effectBefore, Is.True, "The control: the pulse's effect was in place at the store.");
                Assert.That(stored, Is.LessThan(full ?? 0f), "The control: a second ran, so the time left is under the full delay.");
                Assert.That(restored, Is.EqualTo(stored).Within(1e-4f), "The timer runs again for the seconds it had left.");
                Assert.That(keyBack, Is.True, "Its key is back in the state data, where a cut or a mend finds it.");
                Assert.That(effectAfterLoad, Is.True, "The effect is still in place after the load.");
                Assert.That(leftAtEnd, Is.Null, "At its end the timer is gone.");
                Assert.That(effectAtEnd, Is.False, "And the effect has reverted.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 2: a timer loaded onto a paused map does not run while its entity is paused, and runs from its unpause for the
        /// seconds it had left.
        /// </summary>
        [Test]
        public async Task ATimerOnAPausedLoadWaitsForTheUnpause()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var timing = server.ResolveDependency<IGameTiming>();
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var wires = server.System<WiresSystem>();
            var maps = server.System<SharedMapSystem>();
            var key = Starters[0].Key;

            EntityUid source = default, loaded = default;
            float? stored = null, whilePaused = null, afterUnpause = null;
            var pausedMap = EntityUid.Invalid;
            await server.WaitPost(() =>
            {
                source = Spawn(entMan, map.Grid.Owner, "DrydockWiresTimersDoor");
                Starters[0].Start(entMan, source, WireNamed(entMan.GetComponent<WiresComponent>(source), Starters[0].Wire));
                stored = TimeLeft(wires, source, key);

                pausedMap = maps.CreateMap(out _, runMapInit: true);
                maps.SetPaused(pausedMap, true);
                var stored1 = image.Store(map.Grid.Owner);
                image.Despawn(map.Grid.Owner);
                var result = image.Load(stored1.Image, pausedMap);
                loaded = Restored(entMan, fidelity, result.Grid, "DrydockWiresTimersDoor");
                whilePaused = TimeLeft(wires, loaded, key);
            });

            await pair.RunTicksSync(Ticks(timing, 2));
            await server.WaitPost(() =>
            {
                Assert.That(TimeLeft(wires, loaded, key), Is.Null, "Still paused two seconds on, the timer is still held.");
                maps.SetPaused(pausedMap, false);
                afterUnpause = TimeLeft(wires, loaded, key);
            });

            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<MetaDataComponent>(loaded).EntityPaused, Is.False, "The control: the unpause reached the entity.");
                Assert.That(whilePaused, Is.Null, "A timer on a paused entity is held, not started.");
                Assert.That(afterUnpause, Is.EqualTo(stored).Within(1e-4f), "At the unpause it runs for the seconds it had left, none spent while paused.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 3: a wire cut while its pulse runs cancels the timer, whose end then runs at the next update and reverts
        /// nothing because the wire is cut; once it has run nothing is carried and nothing re-arms, and the cut's effect
        /// stays.
        /// </summary>
        [Test]
        public async Task ACutWiresTimerIsNotCarried()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var wires = server.System<WiresSystem>();
            var key = Starters[0].Key;

            EntityUid source = default;
            float? beforeCut = null;
            await server.WaitPost(() =>
            {
                source = Spawn(entMan, map.Grid.Owner, "DrydockWiresTimersDoor");
                var wire = WireNamed(entMan.GetComponent<WiresComponent>(source), Starters[0].Wire);
                Starters[0].Start(entMan, source, wire);
                beforeCut = TimeLeft(wires, source, key);
                if (wire.Action!.Cut(EntityUid.Invalid, wire))
                    wire.IsCut = true;
            });

            await pair.RunTicksSync(1);

            DrydockImageStoreResult stored = default!;
            EntityUid loaded = default;
            float? afterLoad = null;
            var readerEnabled = true;
            await server.WaitPost(() =>
            {
                stored = image.Store(map.Grid.Owner);
                image.Despawn(map.Grid.Owner);
                var result = image.Load(stored.Image, map.MapUid);
                loaded = Restored(entMan, fidelity, result.Grid, "DrydockWiresTimersDoor");
                afterLoad = TimeLeft(wires, loaded, key);
                readerEnabled = entMan.GetComponent<AccessReaderComponent>(loaded).Enabled;
            });

            Assert.Multiple(() =>
            {
                Assert.That(beforeCut, Is.Not.Null, "The control: the pulse armed a timer before the cut.");
                Assert.That(stored.Image.Entities.Any(e => e.Rows.TryGetValue(DrydockImageSystem.CarriedRow, out var carried) && carried.Contains(WiresCarrySystem.TimersKey)),
                    Is.False, "Nothing is carried for a timer the cut has ended.");
                Assert.That(afterLoad, Is.Null, "And nothing re-arms.");
                Assert.That(readerEnabled, Is.False, "The cut's effect stays.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 4, the handler contract: a second directed raise starts no timer twice, an entity held for its unpause and
        /// deleted before it is dropped, and the store dirties nothing on an entity with a timer running.
        /// </summary>
        [Test]
        public async Task ASecondRaiseStartsNothingTwiceAndAHeldEntityCanGo()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var wires = server.System<WiresSystem>();
            var maps = server.System<SharedMapSystem>();
            var timing = server.ResolveDependency<IGameTiming>();
            var recorder = server.System<DrydockWiresTimersRecorderSystem>();
            var key = Starters[0].Key;

            EntityUid source = default;
            await server.WaitPost(() =>
            {
                recorder.Reraised = false;
                source = Spawn(entMan, map.Grid.Owner, "DrydockWiresTimersDoor");
                entMan.GetComponent<DrydockWiresTimersProbeComponent>(source).RaiseTwice = true;
                Starters[0].Start(entMan, source, WireNamed(entMan.GetComponent<WiresComponent>(source), Starters[0].Wire));
            });

            await pair.RunTicksSync(5);

            var changed = new List<string>();
            var timersAfterSecondRaise = 0;
            float? stored1 = null, afterSecondRaise = null;
            var pausedMap = EntityUid.Invalid;
            EntityUid held = default;
            await server.WaitPost(() =>
            {
                stored1 = TimeLeft(wires, source, key);
                var before = entMan.GetComponents(source).ToDictionary(c => c.GetType().Name, c => c.LastModifiedTick);
                var stored = image.Store(map.Grid.Owner);
                changed = entMan.GetComponents(source)
                    .Where(c => !before.TryGetValue(c.GetType().Name, out var tick) || tick != c.LastModifiedTick)
                    .Select(c => c.GetType().Name)
                    .ToList();

                image.Despawn(map.Grid.Owner);
                var result = image.Load(stored.Image, map.MapUid);
                var loaded = Restored(entMan, fidelity, result.Grid, "DrydockWiresTimersDoor");
                timersAfterSecondRaise = wires.RunningTimers(loaded).Count(t => Equals(t.Key, key));
                afterSecondRaise = TimeLeft(wires, loaded, key);

                pausedMap = maps.CreateMap(out _, runMapInit: true);
                maps.SetPaused(pausedMap, true);
                var restored2 = image.Store(result.Grid);
                image.Despawn(result.Grid);
                var paused = image.Load(restored2.Image, pausedMap);
                held = Restored(entMan, fidelity, paused.Grid, "DrydockWiresTimersDoor");
                entMan.DeleteEntity(held);
            });

            await server.WaitPost(() => maps.SetPaused(pausedMap, false));
            await pair.RunTicksSync(Ticks(timing, 5));

            Assert.Multiple(() =>
            {
                Assert.That(recorder.Reraised, Is.True, "The control: the probe raised the directed event a second time, with the carried lookup.");
                Assert.That(changed, Is.Empty, "The store dirtied nothing on an entity with a timer running.");
                Assert.That(timersAfterSecondRaise, Is.EqualTo(1), "A second directed raise starts the timer no second time.");
                Assert.That(afterSecondRaise, Is.EqualTo(stored1).Within(1e-4f), "Nor restarts the one running.");
                Assert.That(entMan.EntityExists(held), Is.False, "The control: the held entity was deleted before the unpause, which was not fatal.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 5: a carried timer whose end no longer binds, as an older image's would after a rename, is dropped, and the
        /// entity's other timers run again.
        /// </summary>
        [Test]
        public async Task ATimerThatNoLongerBindsIsDroppedAndTheOthersRun()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var wires = server.System<WiresSystem>();

            float? access = null, log = null;
            var edited = false;
            await server.WaitPost(() =>
            {
                var door = Spawn(entMan, map.Grid.Owner, "DrydockWiresTimersDoor");
                var comp = entMan.GetComponent<WiresComponent>(door);
                Starters[0].Start(entMan, door, WireNamed(comp, Starters[0].Wire));
                Starters[1].Start(entMan, door, WireNamed(comp, Starters[1].Wire));

                var stored = image.Store(map.Grid.Owner);
                image.Despawn(map.Grid.Owner);

                // The access timer's end renamed, as an image stored before an upstream rename would carry it.
                var entities = stored.Image.Entities.Select(e =>
                {
                    if (!e.Rows.TryGetValue(DrydockImageSystem.CarriedRow, out var carried) || !carried.Contains("AccessWireAction"))
                        return e;

                    var rows = e.Rows.ToDictionary(r => r.Key, r => r.Value);
                    var at = carried.IndexOf("AccessWireAction", StringComparison.Ordinal);
                    var expiry = carried.IndexOf("AwaitPulseCancel", at, StringComparison.Ordinal);
                    rows[DrydockImageSystem.CarriedRow] = carried[..expiry] + "AwaitPulseCancelled" + carried[(expiry + "AwaitPulseCancel".Length)..];
                    edited = true;
                    return e with { Rows = rows };
                }).ToList();

                var result = image.Load(stored.Image with { Entities = entities }, map.MapUid);
                var loaded = Restored(entMan, fidelity, result.Grid, "DrydockWiresTimersDoor");
                access = TimeLeft(wires, loaded, Starters[0].Key);
                log = TimeLeft(wires, loaded, Starters[1].Key);
            });

            Assert.Multiple(() =>
            {
                Assert.That(edited, Is.True, "The control: the access timer's end was renamed in the image.");
                Assert.That(access, Is.Null, "A timer whose end binds to nothing is dropped.");
                Assert.That(log, Is.Not.Null, "The entity's other timer runs again.");
            });

            await pair.CleanReturnAsync();
        }
    }

    /// <summary>A toggle wire of the test's own, on <see cref="BaseToggleWireAction"/>, whose timer end is the base's.</summary>
    public sealed partial class DrydockWiresTimersProbeWireAction : BaseToggleWireAction
    {
        public override Color Color { get; set; } = Color.Purple;
        public override string Name { get; set; } = "wire-name-listen";
        public override object? StatusKey { get; } = null;
        public override object? TimeoutKey { get; } = DrydockWiresTimersProbeKey.Timeout;
        public override int Delay { get; } = 4;

        public override void ToggleValue(EntityUid owner, bool setting) =>
            EntityManager.GetComponent<DrydockWiresTimersProbeComponent>(owner).On = setting;

        public override bool GetValue(EntityUid owner) =>
            EntityManager.GetComponent<DrydockWiresTimersProbeComponent>(owner).On;
    }

    public enum DrydockWiresTimersProbeKey : byte
    {
        Timeout,
    }

    /// <summary>
    /// The test's toggled value, and its ask for a second directed raise. Registered because the integration test assembly
    /// is a content assembly (PoolManager.cs:101).
    /// </summary>
    [RegisterComponent]
    public sealed partial class DrydockWiresTimersProbeComponent : Component
    {
        [Robust.Shared.Serialization.Manager.Attributes.DataField]
        public bool On = true;

        [Robust.Shared.Serialization.Manager.Attributes.DataField]
        public bool RaiseTwice;
    }
}
