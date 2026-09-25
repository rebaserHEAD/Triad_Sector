#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Codec;
using Content.Shared.Xenoarchaeology.Equipment.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The codec's context, which replaces the engine's two entity reference serializers. The engine's
    /// own contexts carry <see cref="EntityUid"/> and <see cref="NetEntity"/> as a pair, and a context
    /// carrying only the first leaves every component holding the second unwritable: finding F28,
    /// where the first corpus run refused 13 device-linked analysis consoles outright.
    /// </summary>
    [TestFixture]
    public sealed class DrydockCodecContextTest
    {
        private const string Key = "analyzerEntity";

        /// <summary>
        /// <c>AnalysisConsoleComponent.AnalyzerEntity</c> is a <c>[DataField] NetEntity?</c>
        /// (<c>Content.Shared/Xenoarchaeology/Equipment/Components/AnalysisConsoleComponent.cs:19-20</c>),
        /// set when the console links to its analyzer, so it is state no map file authored.
        ///
        /// <para>The control is the engine's own write of the same component with no context, and it
        /// has to throw. A null link short-circuits before any serializer is asked, so a throw is also
        /// the proof that the link is really set: without it, this test could pass by the field having
        /// quietly gone empty, which round-trips perfectly.</para>
        /// </summary>
        [Test]
        public async Task ALinkedAnalysisConsoleKeepsItsLink()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            var ids = new StableIds();
            var codec = new DrydockCodec(serialization, entMan, timing, ids.Allocate, ids.Resolve);
            var map = await pair.CreateTestMap();

            NetEntity? linked = null;
            NetEntity? restored = null;
            var stored = string.Empty;
            var expected = string.Empty;
            var engineRefused = false;

            await server.WaitPost(() =>
            {
                var analyzer = entMan.SpawnEntity(null, new EntityCoordinates(map.MapUid, default));
                var console = entMan.SpawnEntity(null, new EntityCoordinates(map.MapUid, default));

                var component = entMan.EnsureComponent<AnalysisConsoleComponent>(console);
                component.AnalyzerEntity = entMan.GetNetEntity(analyzer);
                linked = component.AnalyzerEntity;

                var written = codec.Write((console, entMan.GetComponent<MetaDataComponent>(console)), component);
                stored = written.TryGet<ValueDataNode>(Key, out var node) ? node.Value : "<absent>";
                expected = ids.Allocate(analyzer)!.Value.ToString(CultureInfo.InvariantCulture);

                restored = codec.Read<AnalysisConsoleComponent>(written).AnalyzerEntity;

                // The control: the engine's own write, with no context to reach a NetEntity writer
                // through.
                try
                {
                    serialization.WriteValueAs<MappingDataNode>(typeof(AnalysisConsoleComponent), component, alwaysWrite: true);
                }
                catch (Exception e)
                {
                    engineRefused = DrydockSerializationGap.IsNoCoverage(e);
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(engineRefused, Is.True,
                    "The control: the engine must still refuse this component with no context, which also proves the link is set rather than empty.");

                Assert.That(stored, Is.EqualTo(expected),
                    "The link must store as the analyzer's stable id, the same id its own row carries, and not as a network id that means nothing next round.");

                Assert.That(restored, Is.EqualTo(linked),
                    "And come back as the same analyzer.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A link to an entity the image does not hold. It is severed, as any reference off the image
        /// is: stored as invalid and read back as null, the member being a <c>NetEntity?</c>
        /// (<see cref="DrydockCodecFieldPass"/>), never as a network id standing for nothing.
        /// </summary>
        [Test]
        public async Task ALinkOffTheImageIsSevered()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();

            // Nothing is aboard, so every reference is off the image.
            var codec = new DrydockCodec(serialization, entMan, timing, _ => null, _ => EntityUid.Invalid);
            var map = await pair.CreateTestMap();

            var stored = string.Empty;
            NetEntity? restored = null;

            await server.WaitPost(() =>
            {
                var analyzer = entMan.SpawnEntity(null, new EntityCoordinates(map.MapUid, default));
                var console = entMan.SpawnEntity(null, new EntityCoordinates(map.MapUid, default));

                var component = entMan.EnsureComponent<AnalysisConsoleComponent>(console);
                component.AnalyzerEntity = entMan.GetNetEntity(analyzer);

                var written = codec.Write((console, entMan.GetComponent<MetaDataComponent>(console)), component);
                stored = written.TryGet<ValueDataNode>(Key, out var node) ? node.Value : "<absent>";
                restored = codec.Read<AnalysisConsoleComponent>(written).AnalyzerEntity;
            });

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.EqualTo(DrydockCodecContext.InvalidReference));
                Assert.That(restored, Is.Null);
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>The stable-id pair, kept honest: the link names a real entity and the read has to find it.</summary>
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
