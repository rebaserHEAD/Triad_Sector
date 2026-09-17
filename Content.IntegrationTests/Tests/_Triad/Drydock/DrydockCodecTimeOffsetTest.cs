#nullable enable

using System;
using System.Globalization;
using System.Threading.Tasks;
using Content.Server.Storage.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The arithmetic the codec's time-offset adapter owes, asserted against
    /// <c>MaterialStorageMagnetPickupComponent.NextScan</c>, which carries
    /// <c>TimeOffsetSerializer</c> in content today and is a plain absolute time with no computed
    /// property and no second field to confuse the reading
    /// (<c>Content.Shared/Storage/Components/MaterialStorageMagnetPickupComponent.cs:16-18</c>).
    ///
    /// <para>The rule, from the engine and stated on the grid image design page. On write, for an entity
    /// that is map-initialized and not being written as a prototype, a paused entity stores
    /// <c>value - PauseTime</c> and a live one stores <c>value - CurTime</c>
    /// (<c>RobustToolbox/Robust.Shared/Serialization/TypeSerializers/Implementations/Custom/TimeOffsetSerializer.cs:74-79</c>).
    /// Every other caller stores the literal <c>0</c> (<c>:63-72</c>). On read there is one branch,
    /// <c>stored + CurTime</c>, clamped at <see cref="TimeSpan.MaxValue"/> on overflow (<c>:38-46</c>),
    /// and a caller outside an <c>EntityDeserializer</c> on a post-init entity reads
    /// <see cref="TimeSpan.Zero"/> (<c>:32-36</c>).</para>
    ///
    /// <para><b>This test is red until the adapter exists, and that is the point.</b> It writes and reads
    /// through a context the engine does not recognise, which is exactly the codec's situation: the rows
    /// are not an <c>EntitySerializer</c> document. So the engine takes its fallback branch and stores
    /// <c>0</c> for a deadline five minutes away, and reads back <see cref="TimeSpan.Zero"/> for a stored
    /// offset. The failure text is the receipt that the adapter is needed rather than an opinion that it
    /// is. When the adapter lands, this test switches to our context and turns green with the same
    /// assertions.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockCodecTimeOffsetTest
    {
        private const string Field = "nextScan";
        private static readonly TimeSpan Ahead = TimeSpan.FromMinutes(5);

        [Test]
        public async Task TimeOffsetSurvivesANonEngineContext()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();
            var maps = server.System<SharedMapSystem>();
            var metaData = server.System<MetaDataSystem>();

            var liveMap = await pair.CreateTestMap();
            var pausedMap = await pair.CreateTestMap();

            EntityUid live = default;
            EntityUid paused = default;

            await server.WaitPost(() =>
            {
                var deadline = timing.CurTime + Ahead;

                // A live entity on an initialized map: map-initialized, unpaused.
                live = entMan.SpawnEntity(null, new EntityCoordinates(liveMap.MapUid, default));
                entMan.EnsureComponent<MaterialStorageMagnetPickupComponent>(live).NextScan = deadline;

                // The same on a paused map, so the engine's write takes its pause branch.
                paused = entMan.SpawnEntity(null, new EntityCoordinates(pausedMap.MapUid, default));
                entMan.EnsureComponent<MaterialStorageMagnetPickupComponent>(paused).NextScan = deadline;
                maps.SetPaused(pausedMap.MapId, true);
            });

            // The clock runs on while one of them is paused. Without this the two branches would agree by
            // accident, since an entity paused this instant was paused at the clock's current value, and a
            // test that cannot tell them apart proves only one of them.
            await pair.RunTicksSync(150);

            var liveExpected = 0d;
            var pausedExpected = 0d;
            var readExpected = TimeSpan.Zero;
            var liveWrote = string.Empty;
            var pausedWrote = string.Empty;
            var read = TimeSpan.Zero;
            var readOverflow = TimeSpan.Zero;

            await server.WaitPost(() =>
            {
                var liveComp = entMan.GetComponent<MaterialStorageMagnetPickupComponent>(live);
                var pausedComp = entMan.GetComponent<MaterialStorageMagnetPickupComponent>(paused);

                // The moment it paused, reconstructed from the public API: GetPauseTime is how long it has
                // been paused, and the engine's write subtracts the timestamp underneath that.
                var pausedAt = timing.CurTime - metaData.GetPauseTime(paused);

                liveExpected = (liveComp.NextScan - timing.CurTime).TotalSeconds;
                pausedExpected = (pausedComp.NextScan - pausedAt).TotalSeconds;
                readExpected = timing.CurTime + Ahead;

                liveWrote = Written(serialization, liveComp);
                pausedWrote = Written(serialization, pausedComp);

                // Reading: a stored offset, and one that would overflow the clock.
                read = ReadBack(serialization, Ahead.TotalSeconds.ToString(CultureInfo.InvariantCulture));
                readOverflow = ReadBack(serialization, TimeSpan.MaxValue.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            });

            Assert.Multiple(() =>
            {
                Assert.That(liveExpected, Is.LessThan(pausedExpected - 1),
                    "The control: the two branches must disagree, or neither assertion below is about the branch it names.");

                Assert.That(Seconds(liveWrote), Is.EqualTo(liveExpected).Within(1),
                    "A live entity's deadline must store as its distance from the clock, not as the engine's zero fallback.");

                Assert.That(Seconds(pausedWrote), Is.EqualTo(pausedExpected).Within(1),
                    "A paused entity's deadline must store as its distance from the moment it paused.");

                Assert.That(read, Is.EqualTo(readExpected).Within(TimeSpan.FromSeconds(1)),
                    "A stored offset must read back as that distance from the clock at load.");

                Assert.That(readOverflow, Is.EqualTo(TimeSpan.MaxValue),
                    "An offset that would overflow the clock must clamp rather than wrap.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The component written the way the codec writes one: every field, through the serialization
        /// manager, under no entity context at all. <c>alwaysWrite</c> is the engine's own choice for an
        /// entity with a prototype, and the codec makes the same one.
        /// </summary>
        private static string Written(ISerializationManager serialization, MaterialStorageMagnetPickupComponent component)
        {
            var mapping = serialization.WriteValueAs<MappingDataNode>(
                typeof(MaterialStorageMagnetPickupComponent), component, alwaysWrite: true, context: null);

            return mapping.TryGet<ValueDataNode>(Field, out var value) ? value.Value : "<absent>";
        }

        private static TimeSpan ReadBack(ISerializationManager serialization, string stored)
        {
            var mapping = new MappingDataNode { [Field] = new ValueDataNode(stored) };
            return serialization.Read<MaterialStorageMagnetPickupComponent>(mapping, context: null, notNullableOverride: true).NextScan;
        }

        private static double Seconds(string written) =>
            double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : double.NaN;
    }
}
