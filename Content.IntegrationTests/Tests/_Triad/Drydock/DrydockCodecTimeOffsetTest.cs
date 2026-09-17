#nullable enable

using System;
using System.Globalization;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Codec;
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
    /// The arithmetic the codec owes a time-offset field, asserted through
    /// <see cref="DrydockCodec"/>'s component write and read rather than through a single field, so
    /// what is measured is the path the store will actually use. The fixture is
    /// <c>MaterialStorageMagnetPickupComponent.NextScan</c>, which carries
    /// <c>TimeOffsetSerializer</c> in content today and is a plain absolute time with no computed
    /// property and no second field to confuse the reading
    /// (<c>Content.Shared/Storage/Components/MaterialStorageMagnetPickupComponent.cs:16-18</c>).
    ///
    /// <para>The rule, from the engine and stated on the grid image design page. On write, for an
    /// entity that is map-initialized and not being written as a prototype, a paused entity stores
    /// <c>value - PauseTime</c> and a live one stores <c>value - CurTime</c>
    /// (<c>RobustToolbox/Robust.Shared/Serialization/TypeSerializers/Implementations/Custom/TimeOffsetSerializer.cs:77-80</c>).
    /// Every other caller stores the literal <c>0</c> (<c>:65-72</c>). On read there is one branch,
    /// <c>stored + CurTime</c>, clamped at <see cref="TimeSpan.MaxValue"/> on overflow
    /// (<c>:38-46</c>).</para>
    ///
    /// <para>Left to itself the engine does none of that here, and that is the whole reason the pass
    /// exists: the codec's rows are not an <c>EntitySerializer</c> document, so the engine takes its
    /// fallback branch, stores <c>0</c> for a deadline five minutes away and reads back
    /// <see cref="TimeSpan.Zero"/> for a stored offset. Every assertion below fails against the bare
    /// engine path.</para>
    ///
    /// <para>The last case is the one the two halves cannot prove separately. Write has a pause
    /// branch and read does not, so a ship stored while paused only keeps its remaining time if the
    /// loader puts the pause back: the shift that pays the difference is <c>[AutoPausedField]</c>'s,
    /// and it is raised on unpause.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockCodecTimeOffsetTest
    {
        private const string Field = "nextScan";
        private static readonly TimeSpan Ahead = TimeSpan.FromMinutes(5);

        /// <summary>Long enough that the live and paused branches cannot agree by accident.</summary>
        private const int SeparationTicks = 150;

        /// <summary>
        /// How long the restored entity spends paused. Comfortably above the assertion tolerance, so
        /// a restore that dropped the pause would land outside it rather than inside.
        /// </summary>
        private const int PausedTicks = 300;

        [Test]
        public async Task TimeOffsetSurvivesTheCodec()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();
            var maps = server.System<SharedMapSystem>();
            var metaData = server.System<MetaDataSystem>();

            // The fixture holds no entity references, so the context's stable-id pair is never
            // reached. Throwing rather than returning a placeholder keeps that a fact: if the codec
            // ever asks, this test says so instead of quietly measuring something else.
            var codec = new DrydockCodec(serialization, entMan, timing,
                _ => throw new InvalidOperationException("the time-offset fixture holds no entity references"),
                _ => throw new InvalidOperationException("the time-offset fixture holds no entity references"));

            var liveMap = await pair.CreateTestMap();
            var pausedMap = await pair.CreateTestMap();

            // The restored entity is built on an uninitialized map deliberately. A component added to
            // an entity that is already map-initialized has MapInitEvent re-raised for it
            // (RobustToolbox/Robust.Shared/GameObjects/EntityManager.Components.cs:433), and this
            // fixture's map-init handler seeds the very field under test
            // (Content.Shared/Storage/EntitySystems/MaterialStorageMagnetPickupSystem.cs:36-39),
            // so the restored deadline would be overwritten before anything could read it. A real
            // load does not re-raise it either: the engine restores the life stage the document
            // recorded rather than initializing the entity again.
            var restoreMap = await pair.CreateTestMap(initialized: false);

            EntityUid live = default;
            EntityUid paused = default;
            EntityUid restored = default;

            await server.WaitPost(() =>
            {
                var deadline = timing.CurTime + Ahead;

                // A live entity on an initialized map: map-initialized, unpaused.
                live = entMan.SpawnEntity(null, new EntityCoordinates(liveMap.MapUid, default));
                entMan.EnsureComponent<MaterialStorageMagnetPickupComponent>(live).NextScan = deadline;

                // The same on a paused map, so the write takes its pause branch.
                paused = entMan.SpawnEntity(null, new EntityCoordinates(pausedMap.MapUid, default));
                entMan.EnsureComponent<MaterialStorageMagnetPickupComponent>(paused).NextScan = deadline;
                maps.SetPaused(pausedMap.MapId, true);
            });

            // The clock runs on while one of them is paused. Without this the two branches would
            // agree by accident, since an entity paused this instant was paused at the clock's
            // current value, and a test that cannot tell them apart proves only one of them.
            await pair.RunTicksSync(SeparationTicks);

            var liveExpected = 0d;
            var pausedExpected = 0d;
            var readExpected = TimeSpan.Zero;
            var liveWrote = string.Empty;
            var pausedWrote = string.Empty;
            var read = TimeSpan.Zero;
            var readOverflow = TimeSpan.Zero;
            var restoredAtLoad = TimeSpan.Zero;

            await server.WaitPost(() =>
            {
                var liveComp = entMan.GetComponent<MaterialStorageMagnetPickupComponent>(live);
                var pausedComp = entMan.GetComponent<MaterialStorageMagnetPickupComponent>(paused);

                // The moment it paused, reconstructed from the public API: GetPauseTime is how long
                // it has been paused, and the write subtracts the timestamp underneath that.
                var pausedAt = timing.CurTime - metaData.GetPauseTime(paused);

                liveExpected = (liveComp.NextScan - timing.CurTime).TotalSeconds;
                pausedExpected = (pausedComp.NextScan - pausedAt).TotalSeconds;
                readExpected = timing.CurTime + Ahead;

                var liveMapping = codec.Write((live, entMan.GetComponent<MetaDataComponent>(live)), liveComp);
                var pausedMapping = codec.Write((paused, entMan.GetComponent<MetaDataComponent>(paused)), pausedComp);

                liveWrote = Rendered(liveMapping);
                pausedWrote = Rendered(pausedMapping);

                // Reading: a stored offset, and one that would overflow the clock.
                read = codec.Read<MaterialStorageMagnetPickupComponent>(Stored(Ahead.TotalSeconds)).NextScan;
                readOverflow = codec.Read<MaterialStorageMagnetPickupComponent>(Stored(TimeSpan.MaxValue.TotalSeconds)).NextScan;

                // The round trip of a ship stored while paused: read the component back, measure it
                // before it is attached to anything, then put it on an entity that is paused, which
                // is the loader restoring the pause state.
                restored = entMan.SpawnEntity(null, new EntityCoordinates(restoreMap.MapUid, default));
                var restoredComp = codec.Read<MaterialStorageMagnetPickupComponent>(pausedMapping);
                restoredAtLoad = restoredComp.NextScan - timing.CurTime;

                entMan.AddComponent(restored, restoredComp);
                if (!entMan.GetComponent<MetaDataComponent>(restored).EntityPaused)
                    metaData.SetEntityPaused(restored, true);
            });

            await pair.RunTicksSync(PausedTicks);

            var pausedWindow = TimeSpan.Zero;
            var afterUnpause = TimeSpan.Zero;

            await server.WaitPost(() =>
            {
                pausedWindow = metaData.GetPauseTime(restored);
                metaData.SetEntityPaused(restored, false);

                afterUnpause = entMan.GetComponent<MaterialStorageMagnetPickupComponent>(restored).NextScan - timing.CurTime;
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

                Assert.That(restoredAtLoad.TotalSeconds, Is.EqualTo(pausedExpected).Within(1),
                    "A ship stored while paused must come back with the time it had left when it paused.");

                Assert.That(pausedWindow, Is.GreaterThan(TimeSpan.FromSeconds(2)),
                    "The control: the restored entity must stay paused for longer than the tolerance below, or that assertion passes whether the pause was restored or not.");

                Assert.That(afterUnpause.TotalSeconds, Is.EqualTo(pausedExpected).Within(1),
                    "Time spent paused after the load must not eat into the deadline: unpausing pays back what the pause held.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>A stored row holding one offset, the shape the codec reads from.</summary>
        private static MappingDataNode Stored(double seconds) =>
            new() { [Field] = new ValueDataNode(seconds.ToString(CultureInfo.InvariantCulture)) };

        private static string Rendered(MappingDataNode mapping) =>
            mapping.TryGet<ValueDataNode>(Field, out var value) ? value.Value : "<absent>";

        private static double Seconds(string written) =>
            double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : double.NaN;
    }
}
