#nullable enable

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Advertise.Components;
using Content.Server.Explosion.Components;
using Content.Shared.Charges.Components;
using Content.Shared.Timing;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A loaded hull's times through the thaw. The load sits paused on a map of its own, and the move that unpauses it adds
    /// the time spent paused to every paused time, generated and hand-written handlers alike, with no guard at either end,
    /// and to nothing else. <see cref="DrydockImageSystem.Thaw"/> holds each time the load set to a sentinel through it and
    /// pays the pause to each deadline no handler shifted.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockImageSystem))]
    public sealed class DrydockThawTest
    {
        private const string DelayId = "default";
        private const string VendingId = "VendingMachineCigs";

        /// <summary>The distance from the clock each deadline <see cref="PlaceDeadlines"/> sets is stored at.</summary>
        internal static readonly TimeSpan Advertised = TimeSpan.FromSeconds(300);

        internal static readonly TimeSpan Charged = TimeSpan.FromSeconds(-5);
        internal static readonly TimeSpan Delayed = TimeSpan.FromSeconds(300);
        internal static readonly TimeSpan Triggered = TimeSpan.FromSeconds(300);

        private static readonly TimeSpan Exact = TimeSpan.FromMilliseconds(1);

        /// <summary>
        /// Two sentinels and a deadline on one paused hull through a real thaw: a maximum on a generated
        /// <c>[AutoPausedField]</c> (<c>RepeatingTriggerComponent.NextTrigger</c>), which the shift would overflow, and a
        /// zero inside a dictionary (<c>UseDelayComponent</c>'s delay), which a hand-written handler shifts. Both come back
        /// exact and nothing throws. Controls: the hull is paused before the thaw; a real deadline on the same hull is still
        /// shifted, so the thaw did unpause it; and a second hull thawed without the wrapper has its zero shifted.
        /// </summary>
        [Test]
        public async Task SentinelsComeThroughTheThawExactly()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var timing = server.ResolveDependency<IGameTiming>();
            var mapSys = server.System<SharedMapSystem>();
            var xforms = server.System<SharedTransformSystem>();
            var images = server.System<DrydockImageSystem>();

            var view = await pair.CreateTestMap();

            DrydockImage held = default!, bare = default!;
            var deadlineOffset = TimeSpan.FromSeconds(30);
            await server.WaitPost(() =>
            {
                held = StorePausedHull(entMan, mapSys, images, view.Tile.Tile, grid =>
                {
                    var sentinel = entMan.SpawnEntity(null, new EntityCoordinates(grid, 0.5f, 0.5f));
                    entMan.EnsureComponent<UseDelayComponent>(sentinel);
#pragma warning disable RA0002
                    entMan.EnsureComponent<RepeatingTriggerComponent>(sentinel).NextTrigger = TimeSpan.MaxValue;
                    var deadline = entMan.SpawnEntity(null, new EntityCoordinates(grid, 0.5f, 0.5f));
                    entMan.EnsureComponent<RepeatingTriggerComponent>(deadline).NextTrigger = timing.CurTime + deadlineOffset;
#pragma warning restore RA0002
                });

                bare = StorePausedHull(entMan, mapSys, images, view.Tile.Tile, grid =>
                {
                    var sentinel = entMan.SpawnEntity(null, new EntityCoordinates(grid, 0.5f, 0.5f));
                    entMan.EnsureComponent<UseDelayComponent>(sentinel);
                });
            });

            DrydockLoadResult heldLoad = default!, bareLoad = default!;
            await server.WaitPost(() =>
            {
                heldLoad = images.Load(held, PausedMap(mapSys));
                bareLoad = images.Load(bare, PausedMap(mapSys));
            });

            await pair.RunTicksSync(10);

            EntityUid sentinelUid = default, deadlineUid = default, bareUid = default;
            var pausedBefore = false;
            var deadlineBefore = TimeSpan.Zero;
            await server.WaitPost(() =>
            {
                sentinelUid = heldLoad.Ids.Keys.Single(uid => entMan.HasComponent<UseDelayComponent>(uid));
                deadlineUid = heldLoad.Ids.Keys.Single(uid => entMan.HasComponent<RepeatingTriggerComponent>(uid) && uid != sentinelUid);
                bareUid = bareLoad.Ids.Keys.Single(uid => entMan.HasComponent<UseDelayComponent>(uid));
                pausedBefore = entMan.GetComponent<MetaDataComponent>(sentinelUid).EntityPaused
                               && entMan.GetComponent<MetaDataComponent>(bareUid).EntityPaused;
                deadlineBefore = entMan.GetComponent<RepeatingTriggerComponent>(deadlineUid).NextTrigger;

                images.Thaw(heldLoad, () =>
                    xforms.SetCoordinates(heldLoad.Grid, new EntityCoordinates(view.MapUid, new Vector2(4f, 4f))));

                xforms.SetCoordinates(bareLoad.Grid, new EntityCoordinates(view.MapUid, new Vector2(-4f, -4f)));
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(pausedBefore, Is.True, "The control: both hulls sat paused before the thaw.");
                Assert.That(heldLoad.Times.Where(t => t.IsSentinel).Select(t => t.Member),
                    Has.Some.EndsWith(".NextTrigger").And.Some.Contains(".EndTime"),
                    "The load records the maximum and the nested zero.");

                Assert.That(entMan.GetComponent<RepeatingTriggerComponent>(sentinelUid).NextTrigger, Is.EqualTo(TimeSpan.MaxValue),
                    "The maximum comes through the thaw exactly, without overflowing inside it.");
                Assert.That(entMan.GetComponent<UseDelayComponent>(sentinelUid).Delays[DelayId].EndTime, Is.EqualTo(TimeSpan.Zero),
                    "The nested zero comes through the thaw exactly.");

                Assert.That(entMan.GetComponent<RepeatingTriggerComponent>(deadlineUid).NextTrigger, Is.GreaterThan(deadlineBefore),
                    "The control: a real deadline on the same hull is still shifted, so the thaw did unpause it.");
                Assert.That(entMan.GetComponent<UseDelayComponent>(bareUid).Delays[DelayId].EndTime, Is.Not.EqualTo(TimeSpan.Zero),
                    "The control: without the wrapper the thaw shifts a zero into a deadline.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Four deadlines on one hull through a real thaw after ten ticks paused (<see cref="PlaceDeadlines"/>): two that no
        /// handler shifts on unpause, time-offset fields without <c>[AutoPausedField]</c>
        /// (<c>AdvertiseComponent.NextAdvertisementTime</c>, <c>LimitedChargesComponent.LastUpdate</c>), one the generated
        /// handler shifts (<c>RepeatingTriggerComponent.NextTrigger</c>) and one a hand-written handler shifts
        /// (<c>UseDelayComponent</c>'s delay). All four come back at the distance from the clock they were stored at, none
        /// shifted twice. Control: a second hull thawed without the wrapper loses the time it sat paused on the first two
        /// only, and that time is more than nothing.
        /// </summary>
        [Test]
        public async Task DeadlinesComeThroughTheThawAsThoughPaused()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var timing = server.ResolveDependency<IGameTiming>();
            var mapSys = server.System<SharedMapSystem>();
            var xforms = server.System<SharedTransformSystem>();
            var meta = server.System<MetaDataSystem>();
            var images = server.System<DrydockImageSystem>();

            var view = await pair.CreateTestMap();

            DrydockImage held = default!, bare = default!;
            await server.WaitPost(() =>
            {
                held = StorePausedHull(entMan, mapSys, images, view.Tile.Tile, grid => PlaceDeadlines(entMan, grid));
                bare = StorePausedHull(entMan, mapSys, images, view.Tile.Tile, grid => PlaceDeadlines(entMan, grid));
            });

            DrydockLoadResult heldLoad = default!, bareLoad = default!;
            await server.WaitPost(() =>
            {
                heldLoad = images.Load(held, PausedMap(mapSys));
                bareLoad = images.Load(bare, PausedMap(mapSys));
            });

            await pair.RunTicksSync(10);

            Deadlines heldAfter = default, bareAfter = default;
            var paused = TimeSpan.Zero;
            await server.WaitPost(() =>
            {
                paused = meta.GetPauseTime(bareLoad.Grid);

                images.Thaw(heldLoad, () =>
                    xforms.SetCoordinates(heldLoad.Grid, new EntityCoordinates(view.MapUid, new Vector2(4f, 4f))));
                xforms.SetCoordinates(bareLoad.Grid, new EntityCoordinates(view.MapUid, new Vector2(-4f, -4f)));

                heldAfter = ReadDeadlines(entMan, timing, heldLoad);
                bareAfter = ReadDeadlines(entMan, timing, bareLoad);
            });

            Assert.Multiple(() =>
            {
                Assert.That(paused, Is.GreaterThan(TimeSpan.Zero), "The control: the hulls sat paused before the thaw.");

                Assert.That(heldAfter.Advertised, Is.EqualTo(Advertised).Within(Exact), "Nothing shifts it; the thaw pays the pause.");
                Assert.That(heldAfter.Charged, Is.EqualTo(Charged).Within(Exact), "Nothing shifts it; the thaw pays the pause.");
                Assert.That(heldAfter.Triggered, Is.EqualTo(Triggered).Within(Exact), "The generated shift pays it, once.");
                Assert.That(heldAfter.Delayed, Is.EqualTo(Delayed).Within(Exact), "The hand-written handler pays it, once.");

                Assert.That(bareAfter.Advertised, Is.EqualTo(Advertised - paused).Within(Exact), "The control: unpaid, it lost the pause.");
                Assert.That(bareAfter.Charged, Is.EqualTo(Charged - paused).Within(Exact), "The control: unpaid, it lost the pause.");
                Assert.That(bareAfter.Triggered, Is.EqualTo(Triggered).Within(Exact), "The control: its handler pays it without the wrapper.");
                Assert.That(bareAfter.Delayed, Is.EqualTo(Delayed).Within(Exact), "The control: its handler pays it without the wrapper.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The four deadlines of <see cref="DeadlinesComeThroughTheThawAsThoughPaused"/> onto <paramref name="grid"/>, each set
        /// its distance from the clock: an advertising machine's next advertisement, a charge's last update, a trigger's next
        /// trigger and a use delay's end, the last three on bare entities.
        /// </summary>
        internal static void PlaceDeadlines(IEntityManager entMan, EntityUid grid)
        {
            var at = new EntityCoordinates(grid, 0.5f, 0.5f);
            var now = entMan.EntitySysManager.DependencyCollection.Resolve<IGameTiming>().CurTime;

#pragma warning disable RA0002
            entMan.GetComponent<AdvertiseComponent>(entMan.SpawnEntity(VendingId, at)).NextAdvertisementTime = now + Advertised;
            entMan.EnsureComponent<LimitedChargesComponent>(entMan.SpawnEntity(null, at)).LastUpdate = now + Charged;
            entMan.EnsureComponent<RepeatingTriggerComponent>(entMan.SpawnEntity(null, at)).NextTrigger = now + Triggered;
#pragma warning restore RA0002

            var useDelay = entMan.System<UseDelaySystem>();
            var delayed = entMan.SpawnEntity(null, at);
            var delay = entMan.EnsureComponent<UseDelayComponent>(delayed);
            useDelay.SetLength((delayed, delay), Delayed);
            useDelay.TryResetDelay((delayed, delay));
        }

        /// <summary>The four deadlines on a loaded hull, each as its distance from the clock now.</summary>
        internal static Deadlines ReadDeadlines(IEntityManager entMan, IGameTiming timing, DrydockLoadResult load)
        {
            var now = timing.CurTime;
            return ReadDeadlines(entMan, load.Ids.Keys, now);
        }

        /// <inheritdoc cref="ReadDeadlines(IEntityManager, IGameTiming, DrydockLoadResult)"/>
        internal static Deadlines ReadDeadlines(IEntityManager entMan, System.Collections.Generic.IEnumerable<EntityUid> hull, TimeSpan now)
        {
            var aboard = hull.ToList();
            bool Bare(EntityUid uid) => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype == null;

            var advertiser = aboard.Single(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == VendingId);
            var charges = aboard.Single(uid => Bare(uid) && entMan.HasComponent<LimitedChargesComponent>(uid));
            var trigger = aboard.Single(uid => Bare(uid) && entMan.HasComponent<RepeatingTriggerComponent>(uid));
            var delay = aboard.Single(uid => Bare(uid) && entMan.HasComponent<UseDelayComponent>(uid));

            return new Deadlines(
                entMan.GetComponent<AdvertiseComponent>(advertiser).NextAdvertisementTime - now,
                entMan.GetComponent<LimitedChargesComponent>(charges).LastUpdate - now,
                entMan.GetComponent<RepeatingTriggerComponent>(trigger).NextTrigger - now,
                entMan.GetComponent<UseDelayComponent>(delay).Delays[DelayId].EndTime - now);
        }

        internal readonly record struct Deadlines(TimeSpan Advertised, TimeSpan Charged, TimeSpan Triggered, TimeSpan Delayed);

        /// <summary>A hull built on a paused map by <paramref name="build"/>, stored whole, and deleted.</summary>
        private static DrydockImage StorePausedHull(IEntityManager entMan, SharedMapSystem mapSys, DrydockImageSystem images, Tile tile, Action<EntityUid> build)
        {
            var map = PausedMap(mapSys, out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, tile);
            build(grid.Owner);

            var stored = images.Store(grid.Owner);
            Assert.That(stored.Whole, Is.True, "The control: the hull stores whole.");

            entMan.DeleteEntity(map);
            return stored.Image;
        }

        private static EntityUid PausedMap(SharedMapSystem mapSys) => PausedMap(mapSys, out _);

        private static EntityUid PausedMap(SharedMapSystem mapSys, out MapId mapId)
        {
            var map = mapSys.CreateMap(out mapId);
            mapSys.SetPaused(map, true);
            return map;
        }
    }
}
