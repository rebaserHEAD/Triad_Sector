#nullable enable

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Explosion.Components;
using Content.Shared.Timing;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A loaded hull's sentinel times through the thaw. The load sits paused on a map of its own, and the move that unpauses
    /// it adds the time spent paused to every paused time, generated and hand-written handlers alike, with no guard at
    /// either end. <see cref="DrydockImageSystem.PreserveSentinels"/> holds each time the load set to a sentinel through it.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockImageSystem))]
    public sealed class DrydockSentinelThawTest
    {
        private const string DelayId = "default";

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

                images.PreserveSentinels(heldLoad, () =>
                    xforms.SetCoordinates(heldLoad.Grid, new EntityCoordinates(view.MapUid, new Vector2(4f, 4f))));

                xforms.SetCoordinates(bareLoad.Grid, new EntityCoordinates(view.MapUid, new Vector2(-4f, -4f)));
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(pausedBefore, Is.True, "The control: both hulls sat paused before the thaw.");
                Assert.That(heldLoad.Sentinels.Select(s => s.Member),
                    Has.Some.EndsWith(".NextTrigger").And.Some.Contains(".EndTime"),
                    "The load records the maximum and the nested zero.");

                Assert.That(entMan.GetComponent<RepeatingTriggerComponent>(sentinelUid).NextTrigger, Is.EqualTo(TimeSpan.MaxValue),
                    "The maximum comes through the thaw exactly, without overflowing inside it.");
                Assert.That(entMan.GetComponent<UseDelayComponent>(sentinelUid).Delays[DelayId].EndTime, Is.EqualTo(TimeSpan.Zero),
                    "The nested zero comes through the thaw exactly.");

                Assert.That(entMan.GetComponent<RepeatingTriggerComponent>(deadlineUid).NextTrigger, Is.GreaterThan(deadlineBefore),
                    "The control: a real deadline on the same hull is shifted, so the thaw did unpause it.");
                Assert.That(entMan.GetComponent<UseDelayComponent>(bareUid).Delays[DelayId].EndTime, Is.Not.EqualTo(TimeSpan.Zero),
                    "The control: without the wrapper the thaw shifts a zero into a deadline.");
            });

            await pair.CleanReturnAsync();
        }

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
