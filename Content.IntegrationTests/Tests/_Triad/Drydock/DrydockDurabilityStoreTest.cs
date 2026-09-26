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
    /// The durability layer's store half against a real database: what a pin protects from pruning,
    /// the two-document floor under keep-N, and what a promote carries. Every ship and player is
    /// freshly minted, so assertions are on this test's own ids and never on table-wide counts.
    /// </summary>
    [TestFixture]
    public sealed class DrydockDurabilityStoreTest
    {
        /// <summary>
        /// A pinned document survives any number of stores past keep-N, the pin and its clearing each
        /// write one timeline row, and once cleared the next store prunes it. A sibling ship filed the
        /// same way without a pin is the control that pruning ran at all.
        /// </summary>
        [Test]
        public async Task APinnedDocumentOutlivesKeepNUntilItIsUnpinned()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var pinnedShip = Guid.NewGuid();
            var control = Guid.NewGuid();
            await store.FileRevision(Request(pinnedShip, owner, "Kestrel"), Image(1), keepBlobs: 2);
            await store.FileRevision(Request(control, owner, "Harrier"), Image(1), keepBlobs: 2);

            Assert.That(await store.TryPinRevision(pinnedShip, 1, admin, null, "stepped past by a fallback"), Is.EqualTo(DrydockPinResult.Success));
            Assert.That(await store.TryPinRevision(pinnedShip, 1, admin, null, "again"), Is.EqualTo(DrydockPinResult.AlreadyInState),
                "Pinning a pinned revision is not a change and must not log one.");

            var pinRows = (await store.GetAudit(pinnedShip)).Where(a => a.Action == DrydockAuditAction.RevisionPinned).ToList();
            Assert.That(pinRows, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(pinRows[0].Revision, Is.EqualTo(1));
                Assert.That(pinRows[0].ActorUserId, Is.EqualTo(admin));
                Assert.That(pinRows[0].SubjectUserId, Is.EqualTo(owner));
                Assert.That(pinRows[0].ShipName, Is.EqualTo("Kestrel"));
                Assert.That(pinRows[0].Reason, Is.EqualTo("pinned revision 1: stepped past by a fallback"));
            });

            // Five stores past it with keep-2: revisions 2 to 6.
            for (var i = 2; i <= 6; i++)
            {
                await store.FileRevision(Request(pinnedShip, owner, "Kestrel"), Image(i), keepBlobs: 2);
                await store.FileRevision(Request(control, owner, "Harrier"), Image(i), keepBlobs: 2);
            }

            var pinnedImages = await ImageRevisions(db, pinnedShip);
            var controlImages = await ImageRevisions(db, control);
            var retrievable = await store.ListRetrievableRevisions(pinnedShip);
            Assert.Multiple(() =>
            {
                Assert.That(pinnedImages, Is.EqualTo(new[] { 1, 5, 6 }),
                    "Keep-N took everything between the pin and the window, and never the pin.");
                Assert.That(controlImages, Is.EqualTo(new[] { 5, 6 }),
                    "Control: the same five stores without a pin prune revision 1.");
                Assert.That(retrievable, Is.EqualTo(new[] { 6, 5, 1 }),
                    "What a retrieve can fall back to is every document that exists, newest first, the pin included.");
            });

            var pinnedLoad = await store.LoadRevisionImage(pinnedShip, 1);
            Assert.That(pinnedLoad, Is.Not.Null);
            Assert.That(DrydockImageComparer.Differences(Image(1), pinnedLoad!.Image), Is.Empty, "The pinned image is intact, not just present.");

            Assert.That(await store.TryUnpinRevision(pinnedShip, 1, admin, null, "re-baked"), Is.EqualTo(DrydockPinResult.Success));
            Assert.That(await store.TryUnpinRevision(pinnedShip, 1, admin, null, "again"), Is.EqualTo(DrydockPinResult.AlreadyInState));

            var unpinRows = (await store.GetAudit(pinnedShip)).Where(a => a.Action == DrydockAuditAction.RevisionUnpinned).ToList();
            Assert.That(unpinRows, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(unpinRows[0].Revision, Is.EqualTo(1));
                Assert.That(unpinRows[0].ActorUserId, Is.EqualTo(admin));
                Assert.That(unpinRows[0].Reason, Is.EqualTo("unpinned revision 1: re-baked"));
            });

            Assert.That(await ImageRevisions(db, pinnedShip), Is.EqualTo(new[] { 1, 5, 6 }), "Unpinning deletes nothing by itself.");

            await store.FileRevision(Request(pinnedShip, owner, "Kestrel"), Image(7), keepBlobs: 2);
            Assert.That(await ImageRevisions(db, pinnedShip), Is.EqualTo(new[] { 6, 7 }), "The next store after the unpin prunes it.");

            // The refusals, none of which writes a row.
            Assert.That(await store.TryPinRevision(pinnedShip, 1, admin, null, null), Is.EqualTo(DrydockPinResult.NotFound),
                "A revision whose document is gone has nothing left to protect.");
            Assert.That(await store.TryPinRevision(pinnedShip, 99, admin, null, null), Is.EqualTo(DrydockPinResult.NotFound));
            Assert.That(await store.TryPinRevision(Guid.NewGuid(), 1, admin, null, null), Is.EqualTo(DrydockPinResult.NotFound));
            Assert.That(await store.TryUnpinRevision(pinnedShip, 99, admin, null, null), Is.EqualTo(DrydockPinResult.NotFound));

            Assert.That((await store.GetAudit(pinnedShip)).Count(a => a.Action is DrydockAuditAction.RevisionPinned or DrydockAuditAction.RevisionUnpinned),
                Is.EqualTo(2), "One pin and one unpin, and no row for any refusal.");

            await pair.CleanReturnAsync();
        }

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
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(1), keepBlobs: 1);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 1 }));

            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(2), keepBlobs: 1);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 1, 2 }), "Two exist, so two stay.");

            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(3), keepBlobs: 1);
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(4), keepBlobs: 1);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 3, 4 }), "Pruning still runs; it stops at two.");
            Assert.That(await store.ListRetrievableRevisions(ship), Is.EqualTo(new[] { 4, 3 }));

            // Keep three is unaffected by the floor: control that the floor raises only, never lowers.
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(5), keepBlobs: 3);
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(6), keepBlobs: 3);
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
                await store.FileRevision(Request(ship, owner, "Kestrel"), Image(i), keepBlobs: 0);

            // Take revisions 1 and 2's images directly, leaving revision 3's as the only one.
            var images = db.DrydockImages;
            await db.RunTriadDbCommand((context, token) => images.Delete(context, ship, new[] { 1, 2 }, token), CancellationToken.None);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 3 }), "Control: the ship is down to one document.");

            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(4), keepBlobs: 1);
            Assert.That(await ImageRevisions(db, ship), Is.EqualTo(new[] { 3, 4 }),
                "The only document the ship had is kept alongside the new one.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The filing path refuses the <see cref="DrydockRevisionKind.SystemRebake"/> kind outright, so
        /// the unconditional pointer read it does cannot file a revision of that kind by mistake.
        /// </summary>
        [Test]
        public async Task TheOrdinaryFilingPathRefusesARebake()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();

            var request = new DrydockRevisionRequest
            {
                ShipGuid = Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                ShipName = "Kestrel",
                Kind = DrydockRevisionKind.SystemRebake,
                EngineFormatVer = 7,
                ProtoFingerprint = new byte[] { 1 },
                SizeBytes = 1,
                Manifest = "{}",
            };

            Assert.ThrowsAsync<ArgumentException>(async () => await store.FileRevision(request, Image(1), keepBlobs: 2));

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
            await store.FileRevision(Request(ship, owner, "Kestrel"), Image(1), keepBlobs: 3);
            await store.FileRevision(Request(ship, owner, "Kestrel", appraisal: 5000), Image(2), keepBlobs: 3);

            var (outcome, promoted) = await store.TryPromoteRevision(ship, 1, null, null, null, keepBlobs: 3);
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
