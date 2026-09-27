#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The store's answer to an image that does not read back as written, which the PostgreSQL store reports by throwing
    /// <see cref="DrydockImageMismatchException"/> from <see cref="IDrydockImageStore.Put"/> inside the filing transaction.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockStoreValidationTest
    {
        /// <summary>An image store whose Put reports a read-back mismatch and files nothing; everything else is the real store's.</summary>
        private sealed class MismatchingImageStore(IDrydockImageStore inner) : IDrydockImageStore
        {
            public Task Put(ServerDbContext db, DrydockImageKey key, DrydockImage image, CancellationToken ct) =>
                throw new DrydockImageMismatchException(new[] { "entity 1: row Transform differs" });

            public Task<DrydockImage?> Get(ServerDbContext db, DrydockImageKey key, CancellationToken ct) => inner.Get(db, key, ct);
            public Task<List<int>> Revisions(ServerDbContext db, Guid ship, CancellationToken ct) => inner.Revisions(db, ship, ct);
            public Task<bool> Has(ServerDbContext db, DrydockImageKey key, CancellationToken ct) => inner.Has(db, key, ct);
            public Task<DrydockImagePreflight?> Preflight(ServerDbContext db, DrydockImageKey key, CancellationToken ct) => inner.Preflight(db, key, ct);
            public Task Delete(ServerDbContext db, Guid ship, IReadOnlyCollection<int> revisions, CancellationToken ct) => inner.Delete(db, ship, revisions, ct);
            public Task Copy(ServerDbContext db, DrydockImageKey from, DrydockImageKey to, CancellationToken ct) => inner.Copy(db, from, to, ct);
        }

        /// <summary>
        /// A store whose image does not read back as written is refused as <see cref="DrydockStoreResult.ValidationFailed"/>,
        /// counted in <see cref="DrydockMetrics.ValidationMismatches"/>, and files nothing: no ship id, and the hull is back,
        /// unpaused. Control: the same hull stores once the image store is the real one again.
        /// </summary>
        [Test]
        public async Task AnImageThatDoesNotReadBackIsRefusedAndCounted()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            // The station is passed to the store, so the unwind has somewhere to hand the hull back to.
            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);

            // The manager's image store has no test seam, so it is swapped through its private setter and always put back.
            var images = db.GetType().GetProperty(nameof(IServerDbManager.DrydockImages))!;
            var real = db.DrydockImages;
            var before = DrydockMetrics.ValidationMismatches.Value;

            images.SetValue(db, new MismatchingImageStore(real));
            DrydockStoreResult result;
            Guid? shipId;
            try
            {
                (result, shipId) = await DrydockTestHelpers.Quietly(pair,
                    () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null, stationUid: station)));
            }
            finally
            {
                images.SetValue(db, real);
            }

            await pair.RunTicksSync(5);

            var alive = false;
            var paused = true;
            await server.WaitPost(() =>
            {
                alive = entMan.EntityExists(shipGrid);
                paused = alive && entMan.GetComponent<MetaDataComponent>(shipGrid).EntityPaused;
            });

            var (control, _) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null, stationUid: station));

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(DrydockStoreResult.ValidationFailed));
                Assert.That(shipId, Is.Null, "Nothing was filed.");
                Assert.That(DrydockMetrics.ValidationMismatches.Value, Is.GreaterThan(before), "The refusal is counted.");
                Assert.That(alive, Is.True, "The hull was handed back.");
                Assert.That(paused, Is.False, "The unwind thawed it.");
                Assert.That(control, Is.EqualTo(DrydockStoreResult.Success), "The control: with the real store the same hull stores.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
