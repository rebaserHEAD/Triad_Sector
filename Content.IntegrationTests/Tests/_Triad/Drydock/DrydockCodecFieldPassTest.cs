#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Codec;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Doors.Components;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
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
";

        private const string Blunt = "Blunt";
        private const string SlotId = "codec-slot";

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
        /// The Blunt entry of a written damageable component, or null when the write carries no
        /// damage dictionary at all.
        /// </summary>
        private static FixedPoint2? Blunted(MappingDataNode component)
        {
            if (!component.TryGet<MappingDataNode>("damage", out var damage)
                || !damage.TryGet<MappingDataNode>("types", out var types)
                || !types.TryGet<Robust.Shared.Serialization.Markdown.Value.ValueDataNode>(Blunt, out var value))
            {
                return null;
            }

            return FixedPoint2.New(double.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
