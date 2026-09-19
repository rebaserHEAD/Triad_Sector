#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Codec;
using Content.Server._NF.Market.Components;
using Content.Shared._NF.Market;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The two types in <see cref="DrydockSerializationGap.CapturedTypes"/>. Both are state a player
    /// made and neither can be written by the engine at all, so each gets a serializer of ours,
    /// registered on the codec's context rather than as the engine's default: the gap is ours, and
    /// the serializability audit has to keep measuring the engine without us.
    ///
    /// <para>Each test carries that as its control, in the same test and through the same code the
    /// store path asks: the type written with no context throws, and
    /// <see cref="DrydockSerializationGap.IsNoCoverage"/> says the throw means no coverage rather
    /// than a bad sample.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockCapturedSerializerTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: DrydockCodecLatheDummy
  name: DrydockCodecLatheDummy
  components:
  - type: Lathe
";

        private const string Recipe = "SheetSteel";

        /// <summary>
        /// Through a component, because that is the path the store uses: the queue is an ordinary
        /// <c>[DataField]</c> list (<c>Content.Shared/Lathe/LatheComponent.cs:29-30</c>), so the
        /// generated writer reaches our serializer once per element through the context.
        /// </summary>
        [Test]
        public async Task ALatheQueueSurvivesTheCodec()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var ids = new StableIds();
            var codec = new DrydockCodec(serialization, entMan, timing, ids.Allocate, ids.Resolve);
            var map = await pair.CreateTestMap();

            LatheRecipeBatch? queued = null;
            var restored = new List<LatheRecipeBatch>();
            var engineRefused = false;
            NetEntity actorNet = default;

            await server.WaitPost(() =>
            {
                var actor = entMan.SpawnEntity(null, new EntityCoordinates(map.MapUid, default));
                actorNet = entMan.GetNetEntity(actor);

                var uid = entMan.SpawnEntity("DrydockCodecLatheDummy", new EntityCoordinates(map.MapUid, default));
                var component = entMan.GetComponent<LatheComponent>(uid);

                queued = new LatheRecipeBatch(protoMan.Index<LatheRecipePrototype>(Recipe), 1, 5, actorNet);
                component.Queue.Add(queued);

                var written = codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), component);
                restored = codec.Read<LatheComponent>(written).Queue;

                // The control: the same value, written the way the engine writes one, with no
                // context to reach our serializer through.
                try
                {
                    serialization.WriteValue(typeof(LatheRecipeBatch), queued, alwaysWrite: true);
                }
                catch (Exception e)
                {
                    engineRefused = DrydockSerializationGap.IsNoCoverage(e);
                }
            });

            Assert.That(engineRefused, Is.True,
                "The control: the engine must still have no way to write a lathe batch, which is the whole reason this serializer exists.");

            Assert.That(restored, Has.Count.EqualTo(1), "The queue must come back.");

            var batch = restored[0];
            Assert.Multiple(() =>
            {
                Assert.That(batch.Recipe.ID, Is.EqualTo(Recipe), "the recipe");
                Assert.That(batch.ItemsPrinted, Is.EqualTo(1), "what it has printed");
                Assert.That(batch.ItemsRequested, Is.EqualTo(5), "what was asked for");
                Assert.That(batch.Index, Is.EqualTo(queued!.Index),
                    "and its index, which the queue de-queues by and the constructor would otherwise hand out fresh");
                Assert.That(batch.Actor, Is.EqualTo(actorNet),
                    "the actor, which stores as the entity's stable id and comes back through the same pair");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Through the manager rather than a component: every component holding a
        /// <see cref="MarketData"/> list is under <c>[Access]</c>, which is right and which a test
        /// should not work around. What matters here is the same mechanism either way, that the
        /// context is consulted for a regular type serializer on both halves.
        /// </summary>
        [Test]
        public async Task MarketDataSurvivesTheCodec()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var ids = new StableIds();
            var codec = new DrydockCodec(serialization, entMan, timing, ids.Allocate, ids.Resolve);

            var data = new MarketData(new EntProtoId("SheetSteel1"), new ProtoId<StackPrototype>("Steel"), 7, 1234.5);
            MarketData? restored = null;
            var engineRefused = false;

            await server.WaitPost(() =>
            {
                var written = (MappingDataNode) serialization.WriteValue(
                    typeof(MarketData), data, alwaysWrite: true, context: codec.Context);

                restored = (MarketData) serialization.Read(
                    typeof(MarketData), written, context: codec.Context, notNullableOverride: true)!;

                try
                {
                    serialization.WriteValue(typeof(MarketData), data, alwaysWrite: true);
                }
                catch (Exception e)
                {
                    engineRefused = DrydockSerializationGap.IsNoCoverage(e);
                }
            });

            Assert.That(engineRefused, Is.True,
                "The control: the engine must still have no way to write market data.");

            Assert.That(restored, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(restored!.Prototype, Is.EqualTo(data.Prototype), "the prototype sold");
                Assert.That(restored.StackPrototype, Is.EqualTo(data.StackPrototype), "its stack type");
                Assert.That(restored.Quantity, Is.EqualTo(data.Quantity), "how many");
                Assert.That(restored.Price, Is.EqualTo(data.Price), "and the price, to the digit");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The load's copy, which the two tests above never make: a read component copied into the one its prototype
        /// added, as the loader does. The engine copies a list's elements through a delegate that never consults a
        /// context, and a batch has no empty constructor, so without <see cref="DrydockLatheQueueCopier"/> this
        /// throws (the ladder met it at rung 22, 2026-09-18). The control is that same copy with no context.
        /// </summary>
        [Test]
        public async Task ALatheQueueSurvivesTheLoadsCopy()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var ids = new StableIds();
            var codec = new DrydockCodec(serialization, entMan, timing, ids.Allocate, ids.Resolve);
            var map = await pair.CreateTestMap();

            LatheRecipeBatch? queued = null;
            List<LatheRecipeBatch>? copied = null;
            var engineRefused = false;

            await server.WaitPost(() =>
            {
                var stored = entMan.SpawnEntity("DrydockCodecLatheDummy", new EntityCoordinates(map.MapUid, default));
                var storedComponent = entMan.GetComponent<LatheComponent>(stored);
                queued = new LatheRecipeBatch(protoMan.Index<LatheRecipePrototype>(Recipe), 2, 4, null);
                storedComponent.Queue.Add(queued);

                var read = codec.Read<LatheComponent>(codec.Write((stored, entMan.GetComponent<MetaDataComponent>(stored)), storedComponent));

                // What the prototype put there, which is what a load copies into.
                var loaded = entMan.SpawnEntity("DrydockCodecLatheDummy", new EntityCoordinates(map.MapUid, default));
                var target = entMan.GetComponent<LatheComponent>(loaded);
                serialization.CopyTo(read, ref target, codec.Context, notNullableOverride: true);
                copied = target.Queue;

                try
                {
                    var bare = entMan.GetComponent<LatheComponent>(entMan.SpawnEntity("DrydockCodecLatheDummy", new EntityCoordinates(map.MapUid, default)));
                    serialization.CopyTo(read, ref bare, notNullableOverride: true);
                }
                catch (ArgumentException)
                {
                    engineRefused = true;
                }
            });

            Assert.That(engineRefused, Is.True,
                "The control: the engine's own copy of a queued lathe must still throw, which is the whole reason the copier exists.");

            Assert.That(copied, Has.Count.EqualTo(1), "The copy must carry the queue.");
            Assert.Multiple(() =>
            {
                Assert.That(copied![0], Is.Not.SameAs(queued), "as a copy, not the stored batch itself");
                Assert.That(copied[0].Recipe.ID, Is.EqualTo(Recipe), "the recipe");
                Assert.That(copied[0].ItemsPrinted, Is.EqualTo(2), "what it has printed");
                Assert.That(copied[0].ItemsRequested, Is.EqualTo(4), "what was asked for");
                Assert.That(copied[0].Index, Is.EqualTo(queued!.Index), "and its index, which the constructor would hand out fresh");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The market's copy, for the reason <see cref="ALatheQueueSurvivesTheLoadsCopy"/> gives. It has to go through a
        /// component: only a data definition's generated copy asks the context for a field's copier, and a list copied
        /// on its own goes to the engine's (<c>SerializationManager.Copying.cs:94</c>). The component is built by the
        /// codec's reader from a written row, so nothing here writes to a field its <c>[Access]</c> keeps for the
        /// market system.
        /// </summary>
        [Test]
        public async Task MarketDataSurvivesTheLoadsCopy()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var factory = server.ResolveDependency<IComponentFactory>();
            var timing = server.ResolveDependency<IGameTiming>();

            var ids = new StableIds();
            var codec = new DrydockCodec(serialization, entMan, timing, ids.Allocate, ids.Resolve);

            var data = new MarketData(new EntProtoId("SheetSteel1"), new ProtoId<StackPrototype>("Steel"), 7, 1234.5);
            List<MarketData>? copied = null;
            MarketData? read = null;
            var engineRefused = false;

            await server.WaitPost(() =>
            {
                var row = new MappingDataNode
                {
                    ["marketDataList"] = new SequenceDataNode
                    {
                        serialization.WriteValue(typeof(MarketData), data, alwaysWrite: true, context: codec.Context),
                    },
                };

                var readComponent = codec.Read<CargoMarketDataComponent>(row);
                read = readComponent.MarketDataList[0];

                var target = factory.GetComponent<CargoMarketDataComponent>();
                serialization.CopyTo(readComponent, ref target, codec.Context, notNullableOverride: true);
                copied = target.MarketDataList;

                try
                {
                    var bare = factory.GetComponent<CargoMarketDataComponent>();
                    serialization.CopyTo(readComponent, ref bare, notNullableOverride: true);
                }
                catch (ArgumentException)
                {
                    engineRefused = true;
                }
            });

            Assert.That(engineRefused, Is.True,
                "The control: the engine's own copy of a market list must still throw.");

            Assert.That(copied, Has.Count.EqualTo(1), "The copy must carry the market.");
            Assert.Multiple(() =>
            {
                Assert.That(copied![0], Is.Not.SameAs(read), "as a copy, not the read entry itself");
                Assert.That(copied[0].Prototype, Is.EqualTo(data.Prototype), "the prototype sold");
                Assert.That(copied[0].StackPrototype, Is.EqualTo(data.StackPrototype), "its stack type");
                Assert.That(copied[0].Quantity, Is.EqualTo(data.Quantity), "how many");
                Assert.That(copied[0].Price, Is.EqualTo(data.Price), "and the price, to the digit");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The stable-id pair the codec writes references through, kept honest here rather than
        /// stubbed: a batch's actor is a real entity, and the read has to find it again.
        /// </summary>
        private sealed class StableIds
        {
            private readonly Dictionary<EntityUid, long> _ids = new();
            private readonly Dictionary<long, EntityUid> _entities = new();
            private long _next = 1;

            public long? Allocate(EntityUid uid)
            {
                if (_ids.TryGetValue(uid, out var id))
                    return id;

                id = _next++;
                _ids[uid] = id;
                _entities[id] = uid;
                return id;
            }

            public EntityUid Resolve(long id) =>
                _entities.TryGetValue(id, out var uid) ? uid : EntityUid.Invalid;
        }
    }
}
