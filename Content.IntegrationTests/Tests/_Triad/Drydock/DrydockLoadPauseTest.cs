#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests._Triad.PostgresPair;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A load takes pause from the map it loads onto (<see cref="DrydockLoadSession"/>); the image keeps none. A retrieve
    /// loads onto a paused staging map of its own, so every restored entity is paused from the moment it starts until the
    /// dock moves the hull onto the station's map, and the same image loaded onto a running map comes up running. Both are
    /// read at the broadcast <see cref="GridRestoredEvent"/>, which a load raises once every entity has started and a
    /// retrieve raises before its dock.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockLoadSession))]
    public sealed class DrydockLoadPauseTest
    {
        /// <summary>Each load's restored entities as its broadcast restored event finds them.</summary>
        internal sealed class PauseRecorderSystem : EntitySystem
        {
            [Dependency] private readonly SharedMapSystem _maps = default!;

            public readonly List<(EntityUid Grid, int Restored, int Paused, bool MapPaused)> Loads = new();

            private IReadOnlyList<EntityUid> _restoring = Array.Empty<EntityUid>();

            public override void Initialize()
            {
                base.Initialize();
                SubscribeLocalEvent<GridRestoringEvent>(OnRestoring);
                SubscribeLocalEvent<GridRestoredEvent>(OnRestored);
            }

            private void OnRestoring(ref GridRestoringEvent ev) => _restoring = ev.Entities;

            private void OnRestored(ref GridRestoredEvent ev)
            {
                var paused = _restoring.Count(uid => TryComp(uid, out MetaDataComponent? meta) && meta.EntityPaused);
                var mapPaused = Transform(ev.Grid).MapUid is { } map && _maps.IsPaused(map);
                Loads.Add((ev.Grid, _restoring.Count, paused, mapPaused));
                _restoring = Array.Empty<EntityUid>();
            }
        }

        /// <summary>
        /// On PostgreSQL, whose store has no pause to read back: the retrieve's load comes up paused, every restored
        /// entity, on its paused staging map. The control loads the image as the store reads it back onto a running map,
        /// where every entity comes up running.
        /// </summary>
        [Test]
        [Category("Postgres")]
        public async Task ARetrieveLoadsPausedOntoItsStagingMapFromPostgres()
        {
            await using var postgres = await PostgresTestPair.Start();
            var pair = postgres.Pair;

            await LoadsPausedOnStagingAndRunningElsewhere(pair);

            await pair.CleanReturnAsync();
        }

        /// <summary>The same on the default pair, whose store hands back the image it was given.</summary>
        [Test]
        public async Task ARetrieveLoadsPausedOntoItsStagingMap()
        {
            await using var pair = await PoolManager.GetServerClient();

            await LoadsPausedOnStagingAndRunningElsewhere(pair);

            await pair.CleanReturnAsync();
        }

        private static async Task LoadsPausedOnStagingAndRunningElsewhere(TestPair pair)
        {
            var server = pair.Server;
            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var images = server.System<DrydockImageSystem>();
            var recorder = server.System<PauseRecorderSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);

            var (stored, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(stored, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitPost(recorder.Loads.Clear);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            var image = await DrydockRoundTripTest.ReadImage(db, shipId!.Value);

            EntityUid bareGrid = default;
            await server.WaitPost(() =>
            {
                var running = server.System<SharedMapSystem>().CreateMap(out _);
                bareGrid = images.Load(image, running).Grid;
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(recorder.Loads, Has.Count.EqualTo(2), "The retrieve's load and the control's.");
                var (grid, restored, paused, mapPaused) = recorder.Loads[0];
                var bare = recorder.Loads[1];

                Assert.Multiple(() =>
                {
                    Assert.That(grid, Is.EqualTo(retrieved.Grid), "The first load is the retrieve's.");
                    Assert.That(restored, Is.EqualTo(image.Entities.Count), "The load restored every entity in the image.");
                    Assert.That(mapPaused, Is.True, "The retrieve loads onto a paused map.");
                    Assert.That(paused, Is.EqualTo(restored), $"{paused} of {restored} restored entities were paused before the dock.");

                    Assert.That(bare.Grid, Is.EqualTo(bareGrid), "The second load is the control's.");
                    Assert.That(bare.Restored, Is.EqualTo(restored), "The control loads the same image.");
                    Assert.That(bare.MapPaused, Is.False, "The control loads onto a running map.");
                    Assert.That(bare.Paused, Is.Zero, "Loaded onto a running map, the same image comes up running.");
                });
            });
        }
    }
}
