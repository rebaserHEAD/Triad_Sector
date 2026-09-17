#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Codec;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
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
