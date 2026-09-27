#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>The steps a load names to <see cref="DrydockLoadOptions.Mark"/>, which the retrieve's timing line carries.</summary>
    [TestFixture]
    [TestOf(typeof(DrydockLoadSession))]
    public sealed class DrydockLoadMarksTest
    {
        /// <summary>
        /// A load marks each step inside creation and the rows once, in order, the end of the engine's allocation among them,
        /// which the session finds by counting allocations against the image. Control: the hull holds more than its grid,
        /// so the count is not trivially one.
        /// </summary>
        [Test]
        public async Task ALoadMarksEveryStepInOrder()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var mapSys = server.System<SharedMapSystem>();
            var images = server.System<DrydockImageSystem>();

            var view = await pair.CreateTestMap();

            var marks = new List<string>();
            var entities = 0;
            await server.WaitPost(() =>
            {
                var grid = mapSys.CreateGridEntity(view.MapId);
                mapSys.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, view.Tile.Tile);
                for (var i = 0; i < 3; i++)
                    entMan.SpawnEntity(null, new EntityCoordinates(grid.Owner, 0.5f, 0.5f));

                var stored = images.Store(grid.Owner);
                Assert.That(stored.Whole, Is.True, "The control: the hull stores whole.");
                entities = stored.Image.Entities.Count;
                entMan.DeleteEntity(grid.Owner);

                var target = mapSys.CreateMap(out _);
                images.Load(stored.Image, target, new DrydockLoadOptions { Mark = marks.Add });
            });

            Assert.Multiple(() =>
            {
                Assert.That(entities, Is.GreaterThan(1), "The control: the hull holds more than its grid.");
                Assert.That(marks, Is.EqualTo(new[] { "skeleton", "process", "allocate", "populate", "dropped", "rows", "reparent" }));
            });

            await pair.CleanReturnAsync();
        }
    }
}
