#nullable enable

using System;
using System.Globalization;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Codec;
using Content.Server.Explosion.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A time field on an entity below MapInitialized, through the codec. Every hull built in the round holds one, its
    /// grid, because the engine never map-initialises a grid it creates at runtime (SharedMapSystem.Grid.cs:64-65), and
    /// that grid's deadlines are live. The row holds the deadline's distance from the clock and the read puts it back
    /// exactly, as for an entity at any other stage.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockTimeOffsetAdapter))]
    public sealed class DrydockTimeOffsetCodecTest
    {
        private const string Key = "nextTrigger";

        /// <summary>
        /// <c>RepeatingTriggerComponent.NextTrigger</c> is a plain time-offset data field, set here on a grid made the way
        /// a floor tile in open space makes one. The controls: the grid really is below MapInitialized, and the engine's
        /// own write of the same component stores a literal zero (TimeOffsetSerializer.cs:65-72), which is the value this
        /// case used to lose.
        /// </summary>
        [Test]
        public async Task ARuntimeGridsDeadlineRoundTrips()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();
            var mapSys = server.System<SharedMapSystem>();

            var codec = new DrydockCodec(serialization, entMan, timing, _ => null, _ => EntityUid.Invalid);
            var map = await pair.CreateTestMap();

            EntityLifeStage stage = default;
            TimeSpan deadline = default;
            TimeSpan restored = default;
            var stored = string.Empty;
            var engine = string.Empty;

            await server.WaitPost(() =>
            {
                var grid = mapSys.CreateGridEntity(map.MapId).Owner;
                var meta = entMan.GetComponent<MetaDataComponent>(grid);
                stage = meta.EntityLifeStage;

                var trigger = entMan.EnsureComponent<RepeatingTriggerComponent>(grid);
                deadline = timing.CurTime + TimeSpan.FromSeconds(30);
#pragma warning disable RA0002
                trigger.NextTrigger = deadline;
#pragma warning restore RA0002

                var written = codec.Write((grid, meta), trigger);
                stored = written.TryGet<ValueDataNode>(Key, out var node) ? node.Value : "<absent>";
                restored = codec.Read<RepeatingTriggerComponent>(written).NextTrigger;

                var engineWritten = serialization.WriteValueAs<MappingDataNode>(typeof(RepeatingTriggerComponent), trigger, alwaysWrite: true);
                engine = engineWritten.TryGet<ValueDataNode>(Key, out var engineNode) ? engineNode.Value : "<absent>";
            });

            Assert.Multiple(() =>
            {
                Assert.That(stage, Is.LessThan(EntityLifeStage.MapInitialized),
                    "The control on the case: a grid the engine creates at runtime is not map-initialised.");
                Assert.That(engine, Is.EqualTo("0"),
                    "The control on the loss: the engine's own write stores this deadline as a literal zero.");

                Assert.That(stored, Is.EqualTo(TimeSpan.FromSeconds(30).TotalSeconds.ToString(CultureInfo.InvariantCulture)),
                    "The row holds the deadline's distance from the clock, as it would at any stage.");
                Assert.That(restored, Is.EqualTo(deadline),
                    "And the read puts it back exactly, since the clock has not moved.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
