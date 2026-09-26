#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// <see cref="DrydockStore"/>'s grid-image surface on a pooled SQLite pair, where the images live in the memory store:
    /// a filed image reads back, keep-N and the floor prune images and never revisions, a pin protects an image and is
    /// refused once the image is gone, a promote copies an image forward, and the admin detail and the retrieve's fallback
    /// list see image revisions. The image is a stand-in; the store does not read inside one.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockStore))]
    public sealed class DrydockImageFilingTest
    {
        [Test]
        public async Task ImagesAreFiledPrunedPinnedAndPromoted()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            Assert.That(db.DrydockImages, Is.InstanceOf<DrydockMemoryImageStore>(), "A SQLite pair keeps images in memory.");

            var owner = Guid.NewGuid();
            var ship = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var images = Enumerable.Range(1, 4).Select(Image).ToList();

            var first = await store.FileRevision(Request(ship, owner), images[0], keepBlobs: 2);
            Assert.That(first.Outcome, Is.EqualTo(DrydockBerthResult.Success));

            var loaded = await store.LoadCurrentImage(ship);
            Assert.Multiple(() =>
            {
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Revision.Revision, Is.EqualTo(1));
                Assert.That(loaded.Image, Is.SameAs(images[0]));
            });

            await store.FileRevision(Request(ship, owner), images[1], keepBlobs: 2);
            await store.FileRevision(Request(ship, owner), images[2], keepBlobs: 2);

            var retrievable = await store.ListRetrievableRevisions(ship);
            var pruned = await store.LoadRevisionImage(ship, 1);
            var kept = await store.LoadRevisionImage(ship, 2);
            Assert.Multiple(() =>
            {
                Assert.That(retrievable, Is.EqualTo(new[] { 3, 2 }), "Keep two: revision 1's image is pruned.");
                Assert.That(pruned, Is.Null, "A pruned image reads null.");
                Assert.That(kept?.Image, Is.SameAs(images[1]));
            });

            var detail = await store.GetShipDetail(ship);
            Assert.Multiple(() =>
            {
                Assert.That(detail!.Revisions.Select(r => r.Revision), Is.EquivalentTo(new[] { 1, 2, 3 }), "Pruning takes images, never history.");
                Assert.That(detail.RevisionsWithImage, Is.EquivalentTo(new[] { 2, 3 }), "The panel marks the revisions that hold an image.");
            });

            var pinPruned = await store.TryPinRevision(ship, 1, owner, null, null);
            var pinKept = await store.TryPinRevision(ship, 2, owner, null, null);
            Assert.Multiple(() =>
            {
                Assert.That(pinPruned, Is.EqualTo(DrydockPinResult.NotFound), "Nothing left to protect.");
                Assert.That(pinKept, Is.EqualTo(DrydockPinResult.Success));
            });

            await store.FileRevision(Request(ship, owner), images[3], keepBlobs: 2);
            Assert.That(await store.ListRetrievableRevisions(ship), Is.EqualTo(new[] { 4, 3, 2 }), "The pinned image survives outside the window.");

            var (outcome, promoted) = await store.TryPromoteRevision(ship, 2, owner, null, null, keepBlobs: 2);
            Assert.That(outcome, Is.EqualTo(DrydockBerthResult.Success));

            var current = await store.LoadCurrentImage(ship);
            var afterPromote = await store.ListRetrievableRevisions(ship);
            Assert.Multiple(() =>
            {
                Assert.That(current!.Revision.Revision, Is.EqualTo(promoted));
                Assert.That(current.Image, Is.SameAs(images[1]), "A promote copies the chosen image forward.");
                Assert.That(afterPromote, Is.EqualTo(new[] { promoted, 4, 2 }), "Revision 3 falls out; the pin still holds 2.");
            });

            await pair.CleanReturnAsync();
        }

        private static DrydockImage Image(int n)
        {
            var rows = new Dictionary<string, string> { ["Transform"] = $"{{\"pos\":\"{n},0\"}}" };
            var entity = new DrydockImageEntity(1, "TestGrid", true, rows);
            return new DrydockImage(1, new[] { entity }, "{}", 0, rows["Transform"].Length + 2);
        }

        private static DrydockRevisionRequest Request(Guid ship, Guid owner) => new()
        {
            ShipGuid = ship,
            OwnerUserId = owner,
            ShipName = "Imaged",
            VesselProto = "TestVessel",
            SizeClass = nameof(ShipSizeClass.Cutter),
            Kind = DrydockRevisionKind.PlayerStore,
            MarkStored = true,
            ActorUserId = owner,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1 },
            SizeBytes = 0,
            Manifest = "[]",
        };
    }
}
