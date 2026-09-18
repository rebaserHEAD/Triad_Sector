#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Codec;
using Content.Server.Chemistry.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Medical;
using Content.Shared.Robotics;
using Content.Shared.Robotics.Components;
using Content.Shared.Weapons.Melee;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// What the codec's field pass owes a <c>readOnly</c> field. The generator skips such a field in
    /// the writer it emits (<c>RobustToolbox/Robust.Serialization.Generator/Generator.cs:1013</c>)
    /// and nowhere in the reader, so the engine's own write leaves a hole that its own read would
    /// have filled. Both cases below are that hole: the first is a field of a live entity, the
    /// second is a field of a definition sitting inside a dictionary, which is the depth the pass
    /// has to reach rather than recognise.
    ///
    /// <para>Every assertion has its control in the same test, and the control is the bare engine
    /// write of the same component: same manager, same context, <c>alwaysWrite</c>, no pass. That is
    /// the state of the world this commit changes, so a pass that stopped running would not read as
    /// a pass that works.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockCodecFieldPassTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: DrydockCodecDamageDummy
  name: DrydockCodecDamageDummy
  components:
  - type: Damageable
    damageContainer: Biological

# Damage authored as a group, so the specifier's private group dictionary is non-null and the
# engine's own writer emits a groups key beside whatever else is written.
- type: entity
  id: DrydockCodecGroupDamageDummy
  name: DrydockCodecGroupDamageDummy
  components:
  - type: Damageable
    damageContainer: Biological
    damage:
      groups:
        Brute: 30

# A user for a do-after in progress, with nothing else on it for the codec to write.
- type: entity
  id: DrydockCodecDoAfterDummy
  name: DrydockCodecDoAfterDummy
  components:
  - type: DoAfter

# The same authored damage on a member that is not readOnly, which is the reach the walk has to
# have: nothing on the path from the component to DamageDict is readOnly until DamageDict itself.
- type: entity
  id: DrydockCodecWeaponDummy
  name: DrydockCodecWeaponDummy
  components:
  - type: MeleeWeapon
    damage:
      groups:
        Brute: 9
";

        private const string Blunt = "Blunt";
        private const string SlotId = "codec-slot";

        /// <summary>
        /// How long the do-after runs before it is written. Long enough that its start is seconds
        /// behind the clock, so the offset the codec writes cannot be mistaken for the engine's zero.
        /// </summary>
        private const int DoAfterAgeTicks = 150;

        /// <summary>How long the clock runs before a test that must tell an absolute time from an offset.</summary>
        private const int ClockRunTicks = 300;

        /// <summary>
        /// The ladder's own recipe, 37 Blunt on a damageable entity.
        /// <c>DamageableComponent.Damage</c> is <c>readOnly</c>
        /// (<c>Content.Shared/Damage/Components/DamageableComponent.cs:47</c>) over
        /// <c>DamageSpecifier.DamageDict</c>, which is <c>IncludeDataField(readOnly)</c> carrying a
        /// serializer that reads and cannot write
        /// (<c>Content.Shared/Damage/DamageSpecifier.cs:39</c>,
        /// <c>Content.Shared/Damage/DamageSpecifierDictionarySerializer.cs:13-14</c>), so the value
        /// is written under the key that serializer reads from, per the codec manifest.
        /// </summary>
        [Test]
        public async Task DamageSurvivesTheCodec()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var timing = server.ResolveDependency<IGameTiming>();
            var damage = server.System<DamageableSystem>();

            var codec = Codec(serialization, entMan, timing);
            var map = await pair.CreateTestMap();

            var written = new MappingDataNode();
            var bare = new MappingDataNode();
            var restored = FixedPoint2.Zero;
            var applied = FixedPoint2.Zero;

            await server.WaitPost(() =>
            {
                var uid = entMan.SpawnEntity("DrydockCodecDamageDummy", new EntityCoordinates(map.MapUid, default));
                damage.TryChangeDamage(uid,
                    new DamageSpecifier(protoMan.Index<DamageTypePrototype>(Blunt), FixedPoint2.New(37)),
                    ignoreResistances: true);

                var component = entMan.GetComponent<DamageableComponent>(uid);
                applied = component.Damage.GetTotal();

                written = codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), component);

                // The control: the same component through the engine's own whole-component writer,
                // under the same context, with the pass skipped.
                bare = serialization.WriteValueAs<MappingDataNode>(
                    typeof(DamageableComponent), component, alwaysWrite: true, context: codec.Context);

                restored = codec.Read<DamageableComponent>(written).Damage.GetTotal();
            });

            Assert.Multiple(() =>
            {
                Assert.That(applied, Is.EqualTo(FixedPoint2.New(37)),
                    "The control: the entity has to be damaged before anything is measured about writing its damage.");

                Assert.That(Blunted(bare), Is.Null,
                    "The control: the engine's own write carries no damage at all, which is the hole this pass fills.");

                Assert.That(Blunted(written), Is.EqualTo(FixedPoint2.New(37)),
                    "The codec's write must carry the damage under the key DamageSpecifier's own reader consumes.");

                Assert.That(restored, Is.EqualTo(FixedPoint2.New(37)),
                    "The engine's generated reader must read that key back into DamageDict: a readOnly field is skipped by the writer, never by the reader.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The other key the same reader consumes. A specifier's <c>groups</c> is distributed across
        /// that group's own damage types and added to the dictionary the reader just filled from
        /// <c>types</c> (<c>Content.Shared/Damage/DamageSpecifierDictionarySerializer.cs:58-72</c>),
        /// and the live dictionary is already that flattened total. The prototype's authored groups
        /// survive in the specifier's private field, which the generated writer emits whenever it is
        /// non-null, so a row carrying both grows by the group's damage on every single re-read.
        /// </summary>
        [Test]
        public async Task AuthoredDamageGroupsAreNotCountedTwicePerRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var codec = Codec(serialization, entMan, timing);
            var map = await pair.CreateTestMap();

            var specifier = new MappingDataNode();
            var written = new MappingDataNode();
            var live = FixedPoint2.Zero;
            var once = FixedPoint2.Zero;
            var twice = FixedPoint2.Zero;

            await server.WaitPost(() =>
            {
                var uid = entMan.SpawnEntity("DrydockCodecGroupDamageDummy", new EntityCoordinates(map.MapUid, default));
                var component = entMan.GetComponent<DamageableComponent>(uid);
                var entity = (uid, entMan.GetComponent<MetaDataComponent>(uid));

                live = component.Damage.GetTotal();

                // The control, and it is the specifier rather than the component: the component's
                // Damage is readOnly, so the engine's whole-component writer skips it and there is
                // nothing there to compare. What the pass actually starts from is this node, the
                // engine's own write of the specifier, and it is where the authored group survives.
                specifier = serialization.WriteValueAs<MappingDataNode>(
                    typeof(DamageSpecifier), component.Damage, alwaysWrite: true, context: codec.Context);

                written = codec.Write(entity, component);

                // Two round trips, because one is not enough to see it: the double count compounds,
                // so a second pass is what tells a wrong constant from a growing one.
                var first = codec.Read<DamageableComponent>(written);
                once = first.Damage.GetTotal();

                var second = codec.Read<DamageableComponent>(codec.Write(entity, first));
                twice = second.Damage.GetTotal();
            });

            Assert.Multiple(() =>
            {
                Assert.That(live, Is.EqualTo(FixedPoint2.New(30)),
                    "The control: the entity has to carry the authored group damage before anything is measured about writing it.");

                Assert.That(SumIn(specifier, "groups"), Is.Not.Null,
                    "The control: the node the pass starts from carries the authored group, which is the key that would be read a second time.");

                Assert.That(Groups(written), Is.Null,
                    "The pass must remove it beside the flattened total it writes.");

                Assert.That(Types(written), Is.EqualTo(live),
                    "And what it writes must be the live dictionary.");

                Assert.That(once, Is.EqualTo(live), "One round trip must not change the total.");
                Assert.That(twice, Is.EqualTo(live), "And neither must the next: a double count compounds rather than settling.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Reach, not depth. <c>MeleeWeaponComponent.Damage</c> is an ordinary
        /// <c>[DataField(required: true)]</c> (<c>Content.Shared/Weapons/Melee/MeleeWeaponComponent.cs:79-81</c>),
        /// so nothing on the path from the component down to <c>DamageDict</c> is readOnly until
        /// <c>DamageDict</c> itself. A pass that only descended from readOnly members would write
        /// this specifier's private authored fields and nothing a player did to it, in silence.
        /// </summary>
        [Test]
        public async Task AWeaponsDamageIsWrittenThoughNothingAboveItIsReadOnly()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var codec = Codec(serialization, entMan, timing);
            var map = await pair.CreateTestMap();

            var written = new MappingDataNode();
            var bare = new MappingDataNode();
            var live = FixedPoint2.Zero;
            var restored = FixedPoint2.Zero;

            await server.WaitPost(() =>
            {
                var uid = entMan.SpawnEntity("DrydockCodecWeaponDummy", new EntityCoordinates(map.MapUid, default));
                var component = entMan.GetComponent<MeleeWeaponComponent>(uid);

                live = component.Damage.GetTotal();

                written = codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), component);

                bare = serialization.WriteValueAs<MappingDataNode>(
                    typeof(MeleeWeaponComponent), component, alwaysWrite: true, context: codec.Context);

                restored = codec.Read<MeleeWeaponComponent>(written).Damage.GetTotal();
            });

            Assert.Multiple(() =>
            {
                Assert.That(live, Is.EqualTo(FixedPoint2.New(9)),
                    "The control: the weapon has to carry the authored damage before anything is measured about writing it.");

                Assert.That(Types(bare), Is.Null,
                    "The control: the engine's own write carries no flattened damage, only what the prototype authored.");

                Assert.That(Groups(bare), Is.Not.Null,
                    "The control: what it carries instead is the authored group.");

                Assert.That(Types(written), Is.EqualTo(live),
                    "The codec must write the live dictionary, reached through a member that is not readOnly.");

                Assert.That(Groups(written), Is.Null,
                    "And must not leave the authored group beside it, or the next read counts it again.");

                Assert.That(restored, Is.EqualTo(live), "The round trip must keep the weapon's damage.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The depth case. <c>ItemSlotsComponent.Slots</c> is a <c>readOnly</c>
        /// <c>Dictionary&lt;string, ItemSlot&gt;</c>
        /// (<c>Content.Shared/Containers/ItemSlot/ItemSlotsComponent.cs:23</c>) and
        /// <see cref="ItemSlot"/> carries three <c>readOnly</c> members of its own (<c>:89</c>,
        /// <c>:101</c>, <c>:113</c>). <c>Locked</c> is live state, whether the slot can currently be
        /// ejected from, so a row that carries the dictionary and drops the member hands a stored
        /// cabinet back unlocked. No member's declared type names <see cref="ItemSlot"/>, so this is
        /// only reachable by walking the value against its node.
        /// </summary>
        [Test]
        public async Task ALockedSlotSurvivesTheCodec()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var slotSys = server.System<ItemSlotsSystem>();

            var codec = Codec(serialization, entMan, timing);
            var map = await pair.CreateTestMap();

            var written = new MappingDataNode();
            var bare = new MappingDataNode();
            var element = new MappingDataNode();
            var restored = new Dictionary<string, ItemSlot>();

            await server.WaitPost(() =>
            {
                var uid = entMan.SpawnEntity(null, new EntityCoordinates(map.MapUid, default));

                // Through the owning system, because the component and the slot are both under
                // [Access] and the analyzer is right to refuse a test writing them directly.
                slotSys.AddItemSlot(uid, SlotId, new ItemSlot { Name = "codec" });
                slotSys.SetLock(uid, SlotId, true);

                var component = entMan.GetComponent<ItemSlotsComponent>(uid);
                written = codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), component);

                bare = serialization.WriteValueAs<MappingDataNode>(
                    typeof(ItemSlotsComponent), component, alwaysWrite: true, context: codec.Context);

                if (written.TryGet<MappingDataNode>("slots", out var slots)
                    && slots.TryGet<MappingDataNode>(SlotId, out var slot))
                {
                    element = slot;
                }

                restored = codec.Read<ItemSlotsComponent>(written).Slots;
            });

            Assert.Multiple(() =>
            {
                Assert.That(bare.Has("slots"), Is.False,
                    "The control: the engine's own write carries no slots at all, because the field is readOnly.");

                Assert.That(written.Has("slots"), Is.True,
                    "The dictionary itself must be written.");

                // The recursion's own receipt, separate from the dictionary's: without the walk the
                // slot is written by ItemSlot's generated writer, which skips both of these.
                Assert.That(element.Has("locked"), Is.True,
                    "The slot's own readOnly member must be written on the element, not only the dictionary around it.");

                Assert.That(element.Has("name"), Is.True,
                    "The slot's other readOnly member must be written on the element too.");

                Assert.That(restored.ContainsKey(SlotId), Is.True, "The slot must come back.");
                Assert.That(restored[SlotId].Locked, Is.True, "A locked slot must come back locked.");
                Assert.That(restored[SlotId].Name, Is.EqualTo("codec"), "And with the rest of its readOnly state.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The computed-field case, finding F9. A door's pending state change is a data field over a
        /// computed property, <c>SecondsUntilStateChange</c>, whose setter returns on null or on any
        /// positive value (<c>Content.Shared/Doors/Components/DoorComponent.cs:233-255</c>), so a
        /// change still in the future is written, read, and dropped. The value lives in
        /// <c>NextStateChange</c> (<c>:70</c>), which is not a data field at all, so the codec stores
        /// that instead, through the time-offset adapter because it is an absolute game time.
        ///
        /// <para>This is the one case the round trip cannot catch by shape: a computed field that
        /// round-trips wrongly looks exactly like a field that round-trips, which is why the engine's
        /// own result is asserted here beside ours.</para>
        /// </summary>
        [Test]
        public async Task ADoorsPendingStateChangeSurvivesTheCodec()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var codec = Codec(serialization, entMan, timing);
            var map = await pair.CreateTestMap();

            var written = new MappingDataNode();
            var bare = new MappingDataNode();
            var expected = TimeSpan.Zero;
            TimeSpan? restored = null;
            TimeSpan? bareRestored = null;
            var refusedTheEnginesRow = false;

            await server.WaitPost(() =>
            {
                // On the grid, not the bare map: an airlock anchors itself at spawn, and anchoring
                // to a map with no grid logs an error the pool refuses the pair over.
                var uid = entMan.SpawnEntity("Airlock", map.GridCoords);
                var door = entMan.GetComponent<DoorComponent>(uid);

                expected = timing.CurTime + TimeSpan.FromMinutes(5);
                door.NextStateChange = expected;

                written = codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), door);

                // The control: the engine's own write, which carries the getter's answer under the
                // computed field's own key.
                bare = serialization.WriteValueAs<MappingDataNode>(
                    typeof(DoorComponent), door, alwaysWrite: true, context: codec.Context);

                restored = codec.Read<DoorComponent>(written).NextStateChange;

                bareRestored = ((DoorComponent) serialization.Read(
                    typeof(DoorComponent), bare, context: codec.Context, notNullableOverride: true)!).NextStateChange;

                // A row carrying the computed key was not written by the codec, and reading it would
                // lose the value silently, so it is refused.
                try
                {
                    codec.Read<DoorComponent>(bare);
                }
                catch (FormatException)
                {
                    refusedTheEnginesRow = true;
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(bare.Has("secondsUntilStateChange"), Is.True,
                    "The control: the engine writes the computed field, which is what makes it the thing to replace.");

                Assert.That(bareRestored, Is.Null,
                    "The control: the engine's own round trip drops a change still in the future, because the setter refuses a positive value.");

                Assert.That(written.Has("secondsUntilStateChange"), Is.False,
                    "The codec must not write the computed field at all.");

                Assert.That(written.Has("nextStateChange"), Is.True,
                    "The codec must write the backing member in its place.");

                Assert.That(restored, Is.Not.Null, "The backing member must come back.");
                Assert.That(restored!.Value, Is.EqualTo(expected).Within(TimeSpan.FromSeconds(1)),
                    "And come back as the same deadline, measured against the clock at load.");

                Assert.That(refusedTheEnginesRow, Is.True,
                    "A row carrying the computed field is not one the codec wrote, and reading it would lose the value.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Finding F27. <c>DoAfter.StartTime</c> carries <c>TimeOffsetSerializer</c> and is not
        /// <c>readOnly</c> (<c>Content.Shared/DoAfter/DoAfter.cs:24-25</c>), inside a
        /// <c>[DataDefinition]</c> reached only through <c>DoAfterComponent.DoAfters</c>, a
        /// dictionary (<c>Content.Shared/DoAfter/DoAfterComponent.cs:14-15</c>). The engine writes it
        /// whole, through a serializer that answers zero to any caller that is not its own, so a
        /// do-after in progress comes back having started at the beginning of time.
        ///
        /// <para>The control is the bare engine write under the same context, asserted rather than
        /// described, because it is the whole finding: a zero that writes, reads and writes again is
        /// perfectly idempotent, so the corpus round trip cannot see it, and only a comparison against
        /// the live value can.</para>
        /// </summary>
        [Test]
        public async Task ADoAfterInProgressKeepsItsStartTime()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();
            var doAfters = server.System<SharedDoAfterSystem>();

            var codec = Codec(serialization, entMan, timing);
            var map = await pair.CreateTestMap();

            EntityUid uid = default;
            var begun = false;

            await server.WaitPost(() =>
            {
                // On an initialized map, so the entity is map-initialized: before map-init a time field
                // is still setup data and the adapter stores zero on purpose, which would hide the fix.
                uid = entMan.SpawnEntity("DrydockCodecDoAfterDummy", new EntityCoordinates(map.MapUid, default));

                // Any concrete event with no state of its own: a real content type rather than one
                // declared here, so reading it back does not depend on the test assembly being
                // visible to the reflection manager. Five minutes long, so it is still in progress.
                var args = new DoAfterArgs(entMan, uid, TimeSpan.FromMinutes(5), new StethoscopeDoAfterEvent(), null)
                {
                    Broadcast = true,
                };

                begun = doAfters.TryStartDoAfter(args);
            });

            // The clock runs on, so the start is seconds behind it and the offset is plainly not zero.
            await pair.RunTicksSync(DoAfterAgeTicks);

            var started = TimeSpan.Zero;
            var age = TimeSpan.Zero;
            var written = new MappingDataNode();
            var bare = new MappingDataNode();
            var restored = TimeSpan.MaxValue;
            var bareRestored = TimeSpan.MaxValue;
            var bareReadOfCodecRow = TimeSpan.MaxValue;

            await server.WaitPost(() =>
            {
                var component = entMan.GetComponent<DoAfterComponent>(uid);
                started = component.DoAfters.Values.Single().StartTime;
                age = timing.CurTime - started;

                written = codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), component);

                // The control: the engine's own write of the same component, under the same context.
                bare = serialization.WriteValueAs<MappingDataNode>(
                    typeof(DoAfterComponent), component, alwaysWrite: true, context: codec.Context);

                restored = codec.Read<DoAfterComponent>(written).DoAfters.Values.Single().StartTime;

                bareRestored = ((DoAfterComponent) serialization.Read(
                        typeof(DoAfterComponent), bare, context: codec.Context, notNullableOverride: true)!)
                    .DoAfters.Values.Single().StartTime;

                // The read half's own control. The row above held a zero already, so reading it back
                // as zero proves nothing about the read; this is the codec's row, which holds a real
                // offset, read the engine's way.
                bareReadOfCodecRow = ((DoAfterComponent) serialization.Read(
                        typeof(DoAfterComponent), written, context: codec.Context, notNullableOverride: true)!)
                    .DoAfters.Values.Single().StartTime;
            });

            Assert.Multiple(() =>
            {
                Assert.That(begun, Is.True, "The control: a do-after has to be in progress before anything is measured about writing one.");

                Assert.That(age, Is.GreaterThan(TimeSpan.FromSeconds(2)),
                    "The control: the do-after must have started well before the write, or a zero offset would be right by accident.");

                Assert.That(StartTimeIn(bare), Is.EqualTo("0"),
                    "The control, and the finding: the engine writes a nested time field as a literal zero for any caller but its own.");

                Assert.That(bareRestored, Is.EqualTo(TimeSpan.Zero),
                    "The control, and the finding: and reads it back as the beginning of time.");

                Assert.That(bareReadOfCodecRow, Is.EqualTo(TimeSpan.Zero),
                    "The control, and the finding: the engine reads a nested time field as zero whatever the row holds, which is why the read half needs a walk of its own.");

                Assert.That(Seconds(StartTimeIn(written)), Is.EqualTo(-age.TotalSeconds).Within(1),
                    "The codec must write a nested start as its distance from the clock, as it does one on a component.");

                Assert.That(restored, Is.EqualTo(started).Within(TimeSpan.FromSeconds(1)),
                    "And read it back as the same moment, measured from the clock at load.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The same finding one step further in, where the old predicate could not reach.
        /// <c>RoboticsConsoleComponent.Cyborgs</c> is a <c>Dictionary&lt;string, CyborgControlData&gt;</c>
        /// (<c>Content.Shared/Robotics/Components/RoboticsConsoleComponent.cs:20-21</c>), and
        /// <c>CyborgControlData</c> is a <c>[DataRecord] record struct</c> whose <c>Timeout</c>, the
        /// moment the console drops a cyborg, carries <c>TimeOffsetSerializer</c>
        /// (<c>Content.Shared/Robotics/RoboticsConsoleUi.cs:60-61</c>, <c>:111-112</c>).
        ///
        /// <para>Two things the pass had wrong meet here. <c>[DataDefinition]</c> is not inherited and
        /// a record carries <c>[DataRecord]</c> instead, so a predicate that asked for the attribute
        /// never descended at all; and the element is a struct, so a correction made to it lands in a
        /// boxed copy and is lost unless it is written back into the dictionary.</para>
        ///
        /// <para>Built from a row rather than from a live console, because the component is under
        /// <c>[Access]</c> and its only runtime writer is a device-network packet handler. That covers
        /// both halves: the read restores the deadline into the struct in the dictionary, and the write
        /// of what was read stores it again.</para>
        /// </summary>
        [Test]
        public async Task ACyborgRecordKeepsItsTimeoutThroughAStructInADictionary()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var codec = Codec(serialization, entMan, timing);
            var map = await pair.CreateTestMap();

            // The clock runs on first. A raw absolute time and an offset from now differ by exactly the
            // clock, so at a clock near zero the two are inside the tolerance of each other and the test
            // could not tell a codec that applied the offset from one that did nothing.
            await pair.RunTicksSync(ClockRunTicks);

            const string address = "codec-borg";
            const double ahead = 300;

            var row = new MappingDataNode
            {
                ["cyborgs"] = new MappingDataNode
                {
                    [address] = new MappingDataNode
                    {
                        ["chassisSprite"] = ValueDataNode.Null(),
                        ["chassisName"] = new ValueDataNode("chassis"),
                        ["name"] = new ValueDataNode("borg"),
                        ["timeout"] = new ValueDataNode(ahead.ToString(CultureInfo.InvariantCulture)),
                    },
                },
            };

            var clock = TimeSpan.Zero;
            var expected = TimeSpan.Zero;
            var restored = TimeSpan.MaxValue;
            var bareRestored = TimeSpan.MaxValue;
            string? rewritten = null;
            string? bareRewritten = null;

            await server.WaitPost(() =>
            {
                clock = timing.CurTime;
                expected = clock + TimeSpan.FromSeconds(ahead);

                var component = codec.Read<RoboticsConsoleComponent>(row);
                var cyborgs = component.Cyborgs;
                restored = cyborgs[address].Timeout;

                // The control: the engine's own read of the same row under the same context.
                var bare = (RoboticsConsoleComponent) serialization.Read(
                    typeof(RoboticsConsoleComponent), row, context: codec.Context, notNullableOverride: true)!;
                var bareCyborgs = bare.Cyborgs;
                bareRestored = bareCyborgs[address].Timeout;

                // And the write half, from what the codec read, against a map-initialized owner.
                var owner = entMan.SpawnEntity(null, new EntityCoordinates(map.MapUid, default));
                var meta = entMan.GetComponent<MetaDataComponent>(owner);
                rewritten = TimeoutIn(codec.Write((owner, meta), component), address);

                bareRewritten = TimeoutIn(serialization.WriteValueAs<MappingDataNode>(
                    typeof(RoboticsConsoleComponent), component, alwaysWrite: true, context: codec.Context), address);
            });

            var timeoutAttribute = typeof(CyborgControlData).GetField(nameof(CyborgControlData.Timeout))!
                .GetCustomAttribute<DataFieldAttribute>();

            Assert.Multiple(() =>
            {
                Assert.That(typeof(CyborgControlData).GetCustomAttribute<DataDefinitionAttribute>(), Is.Null,
                    "The control: this has to be a type the attribute-only predicate missed, or the test proves nothing about the change.");

                Assert.That(clock, Is.GreaterThan(TimeSpan.FromSeconds(5)),
                    "The control: the clock has to be well away from zero, or a raw time and an offset are the same number.");

                Assert.That(timeoutAttribute?.CustomTypeSerializer, Is.EqualTo(typeof(TimeOffsetSerializer)),
                    "The control: the field declares the offset serializer, which is what the pass reads.");

                // The finding, asserted in place, and it is not F27's. The generator ignores the
                // declared serializer on this record and emits a plain TimeSpan read and write
                // (CyborgControlData.g.cs:97, :267), so the engine stores the deadline as an absolute
                // time on the previous server's clock rather than as zero.
                Assert.That(bareRestored, Is.EqualTo(TimeSpan.FromSeconds(ahead)),
                    "The engine reads the stored number back as an absolute time, ignoring the declared offset.");

                Assert.That(Seconds(bareRewritten), Is.EqualTo((clock + TimeSpan.FromSeconds(ahead)).TotalSeconds).Within(1),
                    "And writes the deadline as an absolute time, which means nothing next round.");

                Assert.That(restored, Is.EqualTo(expected).Within(TimeSpan.FromSeconds(1)),
                    "The codec must restore the deadline against the clock at load, into the struct in the dictionary rather than into a copy that is thrown away.");

                Assert.That(Seconds(rewritten), Is.EqualTo(ahead).Within(1),
                    "And store it again as its distance from the clock.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A reagent dispenser writes, the content the loop guard first refused: its storage slots
        /// share its <c>StorageWhitelist</c> instance (<c>ReagentDispenserSystem.cs:306</c>), and a
        /// guard over everything visited refused that on the first corpus run after the walk
        /// widened, 92 components across the dispensers' hulls.
        ///
        /// <para>The whitelist is no longer walked at all. It is sealed and carries nothing to
        /// correct, and the walk reached it only while a <c>ProtoId</c> of a prototype counted as
        /// holding the prototype. So this is a check that real content writes whole, not a test of
        /// the guard; <see cref="ASharedDefinitionIsAGraphNotALoop"/> is that.</para>
        /// </summary>
        [Test]
        public async Task AReagentDispenserWritesWhole()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var codec = Codec(serialization, entMan, timing);
            var map = await pair.CreateTestMap();

            var slots = 0;
            var shared = false;
            Exception? dispenserThrew = null;
            Exception? itemSlotsThrew = null;
            var writtenSlots = 0;

            await server.WaitPost(() =>
            {
                // On the grid: a dispenser anchors at spawn, and anchoring to a bare map logs an error.
                var uid = entMan.SpawnEntity("ChemDispenserEmpty", map.GridCoords);
                var meta = entMan.GetComponent<MetaDataComponent>(uid);
                var dispenser = entMan.GetComponent<ReagentDispenserComponent>(uid);

                var storage = dispenser.StorageSlots;
                slots = storage.Count;
                shared = storage.Count > 0 && ReferenceEquals(storage[0].Whitelist, dispenser.StorageWhitelist);

                try
                {
                    var written = codec.Write((uid, meta), dispenser);
                    writtenSlots = written.TryGet<SequenceDataNode>("storageSlots", out var sequence) ? sequence.Count : 0;
                }
                catch (Exception e)
                {
                    dispenserThrew = e;
                }

                try
                {
                    codec.Write((uid, meta), entMan.GetComponent<ItemSlotsComponent>(uid));
                }
                catch (Exception e)
                {
                    itemSlotsThrew = e;
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(slots, Is.GreaterThan(0), "The control: the dispenser has to have storage slots for the walk to reach.");
                Assert.That(shared, Is.True,
                    "The control: the slots still share the dispenser's whitelist instance, the content that was refused.");

                Assert.That(dispenserThrew, Is.Null, "The dispenser must write.");
                Assert.That(itemSlotsThrew, Is.Null, "And so must the item slots that hold the same slots.");
                Assert.That(writtenSlots, Is.EqualTo(slots), "Every slot must be written, including the ones that share the whitelist.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The walk's loop guard against a graph that is not a loop: one definition the pass
        /// corrects, held in two places in the same component. Shared by hand, because the sharing
        /// content does, a dispenser's slots holding its whitelist, is of a definition with nothing
        /// to correct, which the walk no longer enters. A guard over everything visited would refuse
        /// the second place; a guard over the current path walks both, since each has its own node
        /// to fill, and refuses only a real loop.
        ///
        /// <para>The control is that both places come out corrected rather than only that nothing
        /// throws: a guard that quietly skipped the second place would leave the engine's zero
        /// there.</para>
        ///
        /// <para>The sharing itself does not survive the round trip: the one instance writes into
        /// each place and reads back as that many separate instances. That is what the engine's own
        /// save does with it too, so a round trip here preserves every value and not the identity
        /// between them.</para>
        /// </summary>
        [Test]
        public async Task ASharedDefinitionIsAGraphNotALoop()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();
            var doAfters = server.System<SharedDoAfterSystem>();

            var codec = Codec(serialization, entMan, timing);
            var map = await pair.CreateTestMap();

            EntityUid uid = default;
            var begun = false;

            await server.WaitPost(() =>
            {
                uid = entMan.SpawnEntity("DrydockCodecDoAfterDummy", new EntityCoordinates(map.MapUid, default));
                var args = new DoAfterArgs(entMan, uid, TimeSpan.FromMinutes(5), new StethoscopeDoAfterEvent(), null)
                {
                    Broadcast = true,
                };

                begun = doAfters.TryStartDoAfter(args);
            });

            await pair.RunTicksSync(DoAfterAgeTicks);

            var shared = false;
            var age = TimeSpan.Zero;
            Exception? threw = null;
            var starts = new List<string?>();

            await server.WaitPost(() =>
            {
                var component = entMan.GetComponent<DoAfterComponent>(uid);
                var held = component.DoAfters;
                var doAfter = held.Values.Single();
                var second = (ushort) (doAfter.Index + 1);
                age = timing.CurTime - doAfter.StartTime;

                held[second] = doAfter;
                shared = ReferenceEquals(held[doAfter.Index], held[second]);

                try
                {
                    starts = StartTimesIn(codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), component));
                }
                catch (Exception e)
                {
                    threw = e;
                }
                finally
                {
                    // The system ticks this dictionary, so the hand-made second place goes before it does.
                    held.Remove(second);
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(begun, Is.True, "The control: a do-after has to be in progress before anything is measured about writing one.");
                Assert.That(shared, Is.True, "The control: both places have to hold the one instance, or this test is not about a shared reference.");
                Assert.That(age, Is.GreaterThan(TimeSpan.FromSeconds(2)),
                    "The control: the start must be well behind the clock, or the engine's zero would pass for a correction.");

                Assert.That(threw, Is.Null, "A shared instance is a graph, not a loop, so the component must write.");
                Assert.That(starts, Has.Count.EqualTo(2), "Both places must be written.");
                Assert.That(starts.Select(Seconds), Has.All.EqualTo(-age.TotalSeconds).Within(1),
                    "And both corrected: the walk has to enter the instance in each place it sits, not only the first.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Every written do-after's start, in row order.</summary>
        private static List<string?> StartTimesIn(MappingDataNode component)
        {
            var starts = new List<string?>();
            if (!component.TryGet<MappingDataNode>("doAfters", out var doAfters))
                return starts;

            foreach (var (_, element) in doAfters)
            {
                starts.Add(element is MappingDataNode doAfter && doAfter.TryGet<ValueDataNode>("startTime", out var start)
                    ? start.Value
                    : null);
            }

            return starts;
        }

        /// <summary>One cyborg's written timeout, or null when the row carries none.</summary>
        private static string? TimeoutIn(MappingDataNode component, string address)
        {
            return component.TryGet<MappingDataNode>("cyborgs", out var cyborgs)
                   && cyborgs.TryGet<MappingDataNode>(address, out var cyborg)
                   && cyborg.TryGet<ValueDataNode>("timeout", out var timeout)
                ? timeout.Value
                : null;
        }

        /// <summary>The one do-after's written start, or null when the row carries none.</summary>
        private static string? StartTimeIn(MappingDataNode component)
        {
            if (!component.TryGet<MappingDataNode>("doAfters", out var doAfters) || doAfters.Count != 1)
                return null;

            var (_, element) = doAfters[0];
            return element is MappingDataNode doAfter && doAfter.TryGet<ValueDataNode>("startTime", out var start)
                ? start.Value
                : null;
        }

        private static double Seconds(string? written) =>
            double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : double.NaN;

        /// <summary>
        /// The codec under a stable-id pair that answers rather than throws: neither fixture holds
        /// an entity reference today, and a fixture that grew one should fail on the assertion it
        /// broke rather than on the context.
        /// </summary>
        private static DrydockCodec Codec(ISerializationManager serialization, IEntityManager entMan, IGameTiming timing)
        {
            var next = 0L;
            return new DrydockCodec(serialization, entMan, timing, _ => ++next, _ => EntityUid.Invalid);
        }

        /// <summary>
        /// The Blunt entry of a written damage mapping, or null when the write carries no damage
        /// dictionary at all.
        /// </summary>
        private static FixedPoint2? Blunted(MappingDataNode component)
        {
            if (!component.TryGet<MappingDataNode>("damage", out var damage)
                || !damage.TryGet<MappingDataNode>("types", out var types)
                || !types.TryGet<ValueDataNode>(Blunt, out var value))
            {
                return null;
            }

            return Amount(value);
        }

        /// <summary>The sum under a written damage mapping's flattened key, or null when it has none.</summary>
        private static FixedPoint2? Types(MappingDataNode component) => Sum(component, "types");

        /// <summary>The sum under its authored group key, or null when it has none.</summary>
        private static FixedPoint2? Groups(MappingDataNode component) => Sum(component, "groups");

        private static FixedPoint2? Sum(MappingDataNode component, string key) =>
            component.TryGet<MappingDataNode>("damage", out var damage) ? SumIn(damage, key) : null;

        /// <summary>The same, on a written specifier rather than on the component holding one.</summary>
        private static FixedPoint2? SumIn(MappingDataNode specifier, string key)
        {
            if (!specifier.TryGet<MappingDataNode>(key, out var entries))
                return null;

            var total = FixedPoint2.Zero;
            foreach (var (_, node) in entries)
            {
                total += Amount((ValueDataNode) node);
            }

            return total;
        }

        private static FixedPoint2 Amount(ValueDataNode node) =>
            FixedPoint2.New(double.Parse(node.Value, CultureInfo.InvariantCulture));
    }
}
