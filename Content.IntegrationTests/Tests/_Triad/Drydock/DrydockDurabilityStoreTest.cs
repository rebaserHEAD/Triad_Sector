#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The durability layer's store half against a real database: the two-document floor under
    /// keep-N, retention counting saves and not promotes, and what a promote carries. Every ship and
    /// player is freshly minted, so assertions are on this test's own ids and never on table-wide
    /// counts.
    /// </summary>
    [TestFixture]
    public sealed class DrydockDurabilityStoreTest
    {
        /// <summary>
        /// Keep one is floored at two: the current document and one step back for the retrieve ladder.
        /// </summary>
        [Test]
        public async Task KeepingOneIsFlooredAtTwoDocuments()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(1), keepImages: 1);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 1 }));

            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(2), keepImages: 1);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 1, 2 }), "Two exist, so two stay.");

            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(3), keepImages: 1);
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(4), keepImages: 1);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 3, 4 }), "Pruning still runs; it stops at two.");
            Assert.That(await store.ListRetrievableRevisions(ship), Is.EqualTo(new[] { 4, 3 }));

            // Keep three is unaffected by the floor: control that the floor raises only, never lowers.
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(5), keepImages: 3);
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(6), keepImages: 3);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 4, 5, 6 }));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A ship already down to one document, which is the shape the tight prune left before the
        /// floor existed, keeps it through the next store: the window counts the documents that exist,
        /// not revision numbers, so a gap below the current revision cannot pull the edge past it.
        /// </summary>
        [Test]
        public async Task AShipsOnlyRemainingDocumentIsNeverPruned()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            for (var i = 1; i <= 3; i++)
                await store.FileRevision(Request(ship, owner, "Kestrel"), Image(i), keepImages: 0);

            // Take revisions 1 and 2's images directly, leaving revision 3's as the only one.
            var images = db.DrydockImages;
            await db.RunTriadDbCommand((context, token) => images.Delete(context, ship, new[] { 1, 2 }, token), CancellationToken.None);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 3 }), "Control: the ship is down to one document.");

            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(4), keepImages: 1);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 3, 4 }),
                "The only document the ship had is kept alongside the new one.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Retention counts saves, not the revisions derived from them: a promote takes no place in keep-N, so it never
        /// prunes a save, and it lives as long as the save under it. Three saves at keep three, then a promote, keep all
        /// four images; the next store is a save, and prunes save 1 alone.
        /// </summary>
        [Test]
        public async Task APromoteNeverPrunesASave()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            for (var i = 1; i <= 3; i++)
                await store.FileRevision(Request(ship, owner, "Kestrel"), Image(i), keepImages: 3);
            var saved = await ImageRevisions(db, ship);

            var (outcome, promoted) = await store.TryPromoteRevision(ship, 2, null, null, null, keepImages: 3);
            var afterPromote = await ImageRevisions(db, ship);

            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(5), keepImages: 3);
            var afterStore = await ImageRevisions(db, ship);

            Assert.Multiple(() =>
            {
                Assert.That(saved, Is.EqualTo(new[] { 1, 2, 3 }), "The control: three saves, all inside the window.");
                Assert.That(outcome, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(promoted, Is.EqualTo(4));
                Assert.That(afterPromote, Is.EqualTo(new[] { 1, 2, 3, 4 }), "The promote prunes no save.");
                Assert.That(afterStore, Is.EqualTo(new[] { 2, 3, 4, 5 }),
                    "The next save prunes save 1 alone: the promote rides with the saves under it.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A promote files a copy with no live grid behind it, so the copy has to carry the source's
        /// appraisal or a sale or impound of the promoted ship quotes nothing.
        /// </summary>
        [Test]
        public async Task APromoteCarriesItsSourcesAppraisal()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(1), keepImages: 3);
            await store.FileRevision(Request(ship, owner, "Kestrel", appraisal: 5000), Image(2), keepImages: 3);

            var (outcome, promoted) = await store.TryPromoteRevision(ship, 1, null, null, null, keepImages: 3);
            Assert.That(outcome, Is.EqualTo(DrydockBerthResult.Success));

            var current = await store.LoadCurrentImage(ship);
            Assert.Multiple(() =>
            {
                Assert.That(current!.Revision.Revision, Is.EqualTo(promoted));
                Assert.That(current.Revision.AppraisedValue, Is.EqualTo(24000), "Revision 1's appraisal, not revision 2's and not null.");
            });

            await pair.CleanReturnAsync();
        }

        private static DrydockImage Image(int revision) => DrydockTestHelpers.SeedImage(revision);

        private static DrydockRevisionRequest Request(Guid shipId, Guid owner, string name, int appraisal = 24000) => new()
        {
            ShipGuid = shipId,
            OwnerUserId = owner,
            ShipName = name,
            VesselProto = "TestVessel",
            SizeClass = nameof(ShipSizeClass.Cutter),
            Kind = DrydockRevisionKind.PlayerStore,
            MarkStored = true,
            ActorUserId = owner,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1, 2, 3 },
            SizeBytes = 23,
            AppraisedValue = appraisal,
            Manifest = "{\"v\":1,\"e\":[]}",
        };

        /// <summary>The revisions of <paramref name="shipId"/> that hold an image, oldest first.</summary>
        private static async Task<int[]> ImageRevisions(IServerDbManager db, Guid shipId)
        {
            var images = db.DrydockImages;
            var revisions = await db.RunTriadDbCommand((context, token) => images.Revisions(context, shipId, token), CancellationToken.None);
            return revisions.Order().ToArray();
        }
    }
}
