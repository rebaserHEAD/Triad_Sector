#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock.Loader;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// What a load raises once every entity has started, and in what order: <see cref="GridRestoringEvent"/> once, as a
    /// broadcast carrying every restored entity in ascending stable id, then <see cref="GridRestoredEvent"/> directed at each of
    /// those entities in the same order, then once as a broadcast. A per-grid rebuild rides the head event and a per-entity one
    /// the directed raise, so the head has to complete before the first directed raise starts.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockImageSystem))]
    public sealed class DrydockGridRestoreEventsTest
    {
        private sealed class RestoreRecorderSystem : EntitySystem
        {
            /// <summary>Each raise in the order it arrived: "head", "directed", "tail".</summary>
            public readonly List<string> Order = new();

            public readonly List<GridRestoringEvent> Heads = new();
            public readonly List<EntityUid> Directed = new();
            public readonly List<GridRestoredEvent> Tails = new();

            public override void Initialize()
            {
                base.Initialize();
                SubscribeLocalEvent<GridRestoringEvent>(OnHead);
                SubscribeLocalEvent<TransformComponent, GridRestoredEvent>(OnDirected);
                SubscribeLocalEvent<GridRestoredEvent>(OnTail);
            }

            public void Clear()
            {
                Order.Clear();
                Heads.Clear();
                Directed.Clear();
                Tails.Clear();
            }

            private void OnHead(ref GridRestoringEvent ev)
            {
                Order.Add("head");
                Heads.Add(ev);
            }

            private void OnDirected(Entity<TransformComponent> entity, ref GridRestoredEvent ev)
            {
                Order.Add("directed");
                Directed.Add(entity.Owner);
            }

            private void OnTail(ref GridRestoredEvent ev)
            {
                Order.Add("tail");
                Tails.Add(ev);
            }
        }

        [Test]
        public async Task TheHeadEventFiresOnceBeforeAnyDirectedRaiseWithTheFullOrderedList()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var system = server.System<DrydockImageSystem>();
            var recorder = server.System<RestoreRecorderSystem>();

            var grid = map.Grid.Owner;
            await server.WaitPost(() =>
            {
                entMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, 0.5f, 0.5f));
                entMan.SpawnEntity("APCBasic", new EntityCoordinates(grid, 0.5f, 0.5f));
                recorder.Clear();
            });

            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                var stored = system.Store(grid);
                system.Despawn(grid);
                result = system.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                var head = recorder.Heads.Single();
                var idsInOrder = head.Entities.Select(uid => result.Ids[uid]).ToList();

                Assert.Multiple(() =>
                {
                    Assert.That(recorder.Order.First(), Is.EqualTo("head"), "The head event has to come before any directed raise.");
                    Assert.That(recorder.Order.Count(step => step == "head"), Is.EqualTo(1), "The head event fires once.");
                    Assert.That(recorder.Order.Last(), Is.EqualTo("tail"), "The broadcast restored event closes the sequence.");
                    Assert.That(recorder.Tails, Has.Count.EqualTo(1));
                    Assert.That(head.Grid, Is.EqualTo(result.Grid));
                    Assert.That(recorder.Tails[0].Grid, Is.EqualTo(result.Grid));

                    Assert.That(head.Entities, Has.Count.EqualTo(result.Ids.Count), "The head event carries every restored entity.");
                    Assert.That(head.Entities, Is.EquivalentTo(result.Ids.Keys));
                    Assert.That(idsInOrder, Is.Ordered.Ascending, "In ascending stable id.");
                    Assert.That(head.Entities, Does.Contain(result.Grid));

                    Assert.That(recorder.Directed, Is.EqualTo(head.Entities), "The directed raise reaches the same entities in the same order.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}
