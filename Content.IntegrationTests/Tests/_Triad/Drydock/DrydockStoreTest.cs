#nullable enable

using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Exercises the drydock's persistence against a real database, because the guarantees being
    /// tested are transactional and a unit test with a fake would prove nothing about them: that a
    /// revision is filed with its blob and its audit row together, that pruning takes blobs and
    /// never history, and that the current revision's blob survives pruning whatever the keep count
    /// says.
    ///
    /// <para>Everything is scoped to a freshly minted ship id, so the rows this leaves behind in a
    /// pooled server's database cannot be seen by any other test.</para>
    ///
    /// <para>Round ids are null throughout, which is the between-rounds case the re-bake ladder
    /// runs in. The foreign key to the round table is exercised by the migration rather than
    /// here.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockStoreTest
    {
        [Test]
        public async Task RevisionsAndPruning()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var shipId = Guid.NewGuid();
            await InsertPlayer(db, owner);

            var firstBlob = Encoding.UTF8.GetBytes("first revision document");
            var secondBlob = Encoding.UTF8.GetBytes("second revision document");

            // Keep two blobs, so the third store below is what proves pruning happens at all.
            var firstRevision = await store.FileRevision(Request(shipId, owner, "Kestrel"), firstBlob, keepBlobs: 2);
            Assert.That(firstRevision, Is.EqualTo(1), "The first revision of a ship is 1.");

            var loaded = await store.LoadCurrent(shipId);
            Assert.That(loaded, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(loaded!.Ship.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(loaded.Ship.CurrentRevision, Is.EqualTo(1));
                Assert.That(loaded.Ship.OwnerUserId, Is.EqualTo(owner));
                Assert.That(loaded.Revision.Kind, Is.EqualTo(DrydockRevisionKind.PlayerStore));
                Assert.That(loaded.Blob, Is.EqualTo(firstBlob));
            });

            // A second store lands as a new revision on the same hull rather than a second hull.
            var secondRevision = await store.FileRevision(Request(shipId, owner, "Kestrel II"), secondBlob, keepBlobs: 2);
            Assert.That(secondRevision, Is.EqualTo(2));

            loaded = await store.LoadCurrent(shipId);
            Assert.Multiple(() =>
            {
                Assert.That(loaded!.Ship.CurrentRevision, Is.EqualTo(2));
                Assert.That(loaded.Blob, Is.EqualTo(secondBlob));
                Assert.That(loaded.Ship.ShipName, Is.EqualTo("Kestrel II"), "The display cache refreshes on every store.");
            });

            // The ownership rule: a store never moves the ship to whoever filed it.
            var otherOwner = Guid.NewGuid();
            await InsertPlayer(db, otherOwner);
            await store.FileRevision(Request(shipId, otherOwner, "Kestrel III"), secondBlob, keepBlobs: 2);

            loaded = await store.LoadCurrent(shipId);
            Assert.That(loaded!.Ship.OwnerUserId, Is.EqualTo(owner),
                "A store must not transfer the ship. Ownership moves through a transfer, with its own audit row.");

            // Three revisions filed with keepBlobs 2, so revision 1's blob is gone and its history
            // is not. This is the guarantee that lets the design promise a hull's whole history.
            var (revisionCount, blobRevisions) = await ReadRevisionShape(db, shipId);
            Assert.Multiple(() =>
            {
                Assert.That(revisionCount, Is.EqualTo(3), "Revision history is kept indefinitely.");
                Assert.That(blobRevisions, Is.EquivalentTo(new[] { 2, 3 }), "Pruning takes blobs, oldest first, and never history.");
            });

            // Keep exactly one, which is the tightest setting that prunes: everything below the
            // revision just filed goes, and the one a retrieve is about to read stays. This is the
            // floor, and it is the case where an off-by-one would delete the live document.
            await store.FileRevision(Request(shipId, owner, "Kestrel IV"), firstBlob, keepBlobs: 1);
            loaded = await store.LoadCurrent(shipId);
            Assert.That(loaded, Is.Not.Null, "Pruning must never take the blob the current revision points at.");
            Assert.That(loaded!.Blob, Is.EqualTo(firstBlob));

            var (_, afterTightPrune) = await ReadRevisionShape(db, shipId);
            Assert.That(afterTightPrune, Is.EquivalentTo(new[] { 4 }), "Keeping one leaves exactly the current blob.");

            // Zero or less means no pruning at all rather than keep nothing, which is the only
            // reading that is safe to misconfigure: the wrong guess costs disk, not ships.
            await store.FileRevision(Request(shipId, owner, "Kestrel V"), secondBlob, keepBlobs: 0);
            var (_, afterNoPrune) = await ReadRevisionShape(db, shipId);
            Assert.That(afterNoPrune, Is.EquivalentTo(new[] { 4, 5 }), "A keep count of zero prunes nothing.");

            var audit = await store.GetAudit(shipId);
            Assert.That(audit.Select(a => a.Action), Is.All.EqualTo(DrydockAuditAction.Store));
            Assert.That(audit, Has.Count.EqualTo(5), "Every store is on the timeline, not just the exceptional ones.");

            var owned = await store.GetShipsByOwner(owner);
            Assert.That(owned.Select(s => s.ShipGuid), Does.Contain(shipId));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task StateChangesCarryTheirAuditRow()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var shipId = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await store.FileRevision(Request(shipId, owner, "Harrier"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 2);

            var moved = await store.SetState(shipId, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, null, null);
            Assert.That(moved, Is.True);

            var again = await store.SetState(shipId, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, null, null);
            Assert.That(again, Is.False, "Moving to the state a ship is already in is not a state change and must not log one.");

            var held = await store.SetState(shipId, DrydockShipState.Held, DrydockAuditAction.Hold, null, null, "under investigation");
            Assert.That(held, Is.True);

            var audit = await store.GetAudit(shipId);
            Assert.Multiple(() =>
            {
                Assert.That(audit.Select(a => a.Action), Is.EqualTo(new[]
                {
                    DrydockAuditAction.Store,
                    DrydockAuditAction.Retrieve,
                    DrydockAuditAction.Hold,
                }), "The timeline is ordered and holds one row per accepted change.");

                Assert.That(audit[^1].Reason, Is.EqualTo("under investigation"),
                    "An adjudication's reasoning is the whole reason the row exists.");
            });

            await pair.CleanReturnAsync();
        }

        private static DrydockRevisionRequest Request(Guid shipId, Guid owner, string name) => new()
        {
            ShipGuid = shipId,
            OwnerUserId = owner,
            ShipName = name,
            VesselProto = "TestVessel",
            SizeClass = "Small",
            Kind = DrydockRevisionKind.PlayerStore,
            ActorUserId = owner,
            CreatedRoundId = null,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1, 2, 3 },
            CapturedKeyHash = new byte[] { 4, 5, 6 },
            Checksum = new byte[] { 7, 8, 9 },
            SizeBytes = 23,
            Manifest = "{\"v\":1,\"e\":[]}",
        };

        /// <summary>
        /// The owner column is a real foreign key, so a ship cannot be filed for a player who does
        /// not exist. That is the intended behaviour, and it means this test has to supply one.
        /// </summary>
        private static Task InsertPlayer(IServerDbManager db, Guid userId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                context.Player.Add(new Player
                {
                    UserId = userId,
                    LastSeenUserName = $"drydock-test-{userId:N}",
                    FirstSeenTime = DateTime.UtcNow,
                    LastSeenTime = DateTime.UtcNow,
                    LastSeenAddress = IPAddress.Loopback,
                });

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }

        private static Task<(int RevisionCount, int[] BlobRevisions)> ReadRevisionShape(IServerDbManager db, Guid shipId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                var revisions = await context.DrydockRevision.AsNoTracking()
                    .CountAsync(r => r.ShipGuid == shipId, token);

                var blobs = await context.DrydockBlob.AsNoTracking()
                    .Where(b => b.ShipGuid == shipId)
                    .Select(b => b.Revision)
                    .OrderBy(r => r)
                    .ToArrayAsync(token);

                return (revisions, blobs);
            }, CancellationToken.None);
        }
    }
}
