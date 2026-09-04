#nullable enable

using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The store-layer half of the admin panel: what an admin can do to a hull's record, pinned
    /// against a real database. The live-grid guard on restore needs an entity world and lives in
    /// the round-trip tests.
    /// </summary>
    [TestFixture]
    public sealed class DrydockAdminTest
    {
        [Test]
        public async Task PromotingAnOlderRevisionCopiesItForwardAndNeverRewinds()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await InsertPlayer(db, admin);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            var good = Encoding.UTF8.GetBytes("the good document");
            var bad = Encoding.UTF8.GetBytes("the bad document");

            await store.FileRevision(Request(ship, owner, "Phoenix"), good, keepBlobs: 3);
            await store.FileRevision(Request(ship, owner, "Phoenix"), bad, keepBlobs: 3);

            var (outcome, promoted) = await store.TryPromoteRevision(ship, 1, admin, null, "revision 2 is corrupt", keepBlobs: 3);
            Assert.Multiple(() =>
            {
                Assert.That(outcome, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(promoted, Is.EqualTo(3), "A promotion is a new revision, never a rewind of the pointer.");
            });

            var current = await store.LoadCurrent(ship);
            Assert.Multiple(() =>
            {
                Assert.That(current!.Ship.CurrentRevision, Is.EqualTo(3));
                Assert.That(current.Blob, Is.EqualTo(good), "The promoted document is the one the admin chose.");
                Assert.That(current.Revision.Kind, Is.EqualTo(DrydockRevisionKind.AdminRestore));
                Assert.That(current.Revision.DerivedFromRevision, Is.EqualTo(1), "Provenance names what it was copied from.");
                Assert.That(current.Revision.ActorUserId, Is.EqualTo(admin));
            });

            var detail = await store.GetShipDetail(ship);
            Assert.That(detail!.Revisions.Select(r => r.Revision), Is.EqualTo(new[] { 3, 2, 1 }), "History is complete and newest first.");

            var (pruned, _) = await store.TryPromoteRevision(ship, 99, admin, null, "no such revision", keepBlobs: 3);
            Assert.That(pruned, Is.EqualTo(DrydockBerthResult.NotFound));

            var audit = await store.GetAudit(ship);
            var restore = audit.Single(a => a.Action == DrydockAuditAction.Restore);
            Assert.That(restore.Reason, Does.Contain("promoted revision 1"));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task DeletingAHullKeepsItsTimelineAndFreesItsBerth()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await InsertPlayer(db, admin);
            var berth = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Doomed"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);

            Assert.That(await store.TryDeleteShip(ship, admin, null, "abandoned by owner"), Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(await store.LoadCurrent(ship), Is.Null, "The record, its revisions and its documents are gone.");

            var slots = await store.GetBerths(owner);
            Assert.That(slots.Single(s => s.Berth.BerthId == berth).Occupant, Is.Null, "The berth is left empty, not removed.");

            var audit = await store.GetAudit(ship);
            Assert.Multiple(() =>
            {
                Assert.That(audit.Select(a => a.Action), Does.Contain(DrydockAuditAction.Delete), "The evidence outlives the thing deleted.");
                Assert.That(audit.Single(a => a.Action == DrydockAuditAction.Delete).ShipName, Is.EqualTo("Doomed"),
                    "The name is snapshotted on the row because the ship it names no longer exists.");
                Assert.That(audit.Single(a => a.Action == DrydockAuditAction.Delete).ActorUserId, Is.EqualTo(admin));
            });

            Assert.That(await store.TryDeleteShip(ship, admin, null, "again"), Is.EqualTo(DrydockBerthResult.NotFound));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task AnInvestigationIsOnTheTimelineAndTheListFindsStrandedShips()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await InsertPlayer(db, admin);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var home = Guid.NewGuid();
            var lost = Guid.NewGuid();
            await store.FileRevision(Request(home, owner, "Home"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);
            await store.FileRevision(Request(lost, owner, "Lost"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);

            Assert.That(await store.SetInvestigating(home, true, admin, null, "reported duped cargo"), Is.True);
            Assert.That(await store.SetInvestigating(home, true, admin, null, "again"), Is.False, "Flagging a flagged ship is not a change and logs nothing.");

            var audit = await store.GetAudit(home);
            Assert.That(audit.Count(a => a.Action == DrydockAuditAction.InvestigationOpened), Is.EqualTo(1));
            Assert.That((await store.LoadCurrent(home))!.Ship.Investigating, Is.True);

            // Out with no round to point at: that is the stranded shape a past round leaves behind.
            await store.TrySetState(lost, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, null, null);
            await store.VacateBerth(lost);

            var stranded = await store.QueryShips(new DrydockShipFilter(owner, null, null, null, StrandedOnly: true, CurrentRoundId: 12345), 0, 50);
            Assert.Multiple(() =>
            {
                Assert.That(stranded.Rows.Select(s => s.ShipGuid), Is.EquivalentTo(new[] { lost }), "The adjudication list is exactly the ships out in a round that is over.");
                Assert.That(stranded.Total, Is.EqualTo(1));
            });

            var byName = await store.QueryShips(new DrydockShipFilter(null, null, "hom", null, false, null), 0, 50);
            Assert.That(byName.Rows.Select(s => s.ShipGuid), Does.Contain(home), "Name filtering is case-insensitive on both providers.");

            var detail = await store.GetShipDetail(lost);
            Assert.That(detail!.Timeline.Select(a => a.Action), Is.EqualTo(new[] { DrydockAuditAction.Retrieve, DrydockAuditAction.Store }),
                "The detail's timeline is newest first.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Opening an investigation withdraws whatever offer was standing. A hull under question
        /// does not change hands while the question is open, and it refuses to be offered again
        /// until the flag comes off, so the two rules are asserted together.
        /// </summary>
        [Test]
        public async Task OpeningAnInvestigationWithdrawsTheStandingOffer()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await InsertPlayer(db, recipient);
            await InsertPlayer(db, admin);

            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            // The offer is refused outright unless the recipient has somewhere to put it.
            await store.AddBerth(recipient, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Contested"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);

            var (offered, transfer) = await store.TryOfferTransfer(ship, owner, recipient, TimeSpan.FromMinutes(30), null);
            Assert.Multiple(() =>
            {
                Assert.That(offered, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(transfer, Is.Not.Null);
                Assert.That(transfer!.ToUserId, Is.EqualTo(recipient));
            });
            Assert.That((await store.LoadCurrent(ship))!.Ship.State, Is.EqualTo(DrydockShipState.InEscrow));

            Assert.That(await store.SetInvestigating(ship, true, admin, null, "recipient reported for scamming"), Is.True);

            var standing = await store.GetPendingOfferForShip(ship);
            var after = (await store.LoadCurrent(ship))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(standing, Is.Null, "The offer is gone, so the recipient's alert is too.");
                Assert.That(after.State, Is.EqualTo(DrydockShipState.Stored), "Escrow releases back to the owner's own berth.");
                Assert.That(after.Investigating, Is.True);
                Assert.That(after.BerthId, Is.Not.Null, "A ship in escrow keeps its berth, so there is one to come back to.");
            });

            var audit = await store.GetAudit(ship);
            var cancelled = audit.Single(a => a.Action == DrydockAuditAction.TransferCancelled);
            Assert.Multiple(() =>
            {
                Assert.That(cancelled.Reason, Is.EqualTo("investigation opened"), "The timeline says why the offer died, not just that it did.");
                Assert.That(cancelled.ActorUserId, Is.EqualTo(admin));
                Assert.That(cancelled.SubjectUserId, Is.EqualTo(recipient), "Subject is who lost the offer.");
            });

            var (again, _) = await store.TryOfferTransfer(ship, owner, recipient, TimeSpan.FromMinutes(30), null);
            Assert.That(again, Is.EqualTo(DrydockBerthResult.WrongState), "An investigated ship refuses a fresh offer.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A rename does not hide a hull. The admin box searches every name a ship has carried,
        /// because the complaint is filed under the name it had at the time.
        /// </summary>
        [Test]
        public async Task APastNameIsStillSearchableAfterARename()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            // Pooled pairs share one database, so the needles carry a token no other test uses.
            var token = Guid.NewGuid().ToString("N")[..8];
            var oldName = $"Wanderer{token}";
            var newName = $"Vagrant{token}";

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, oldName), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);
            Assert.That(await store.TryRenameShip(ship, owner, newName, null), Is.EqualTo(DrydockBerthResult.Success));

            var byOldName = await store.QueryShips(Search(oldName), 0, 50);
            var byNewName = await store.QueryShips(Search(newName), 0, 50);
            var byNothing = await store.QueryShips(Search($"Nomad{token}"), 0, 50);

            Assert.Multiple(() =>
            {
                Assert.That(byNewName.Rows.Select(s => s.ShipGuid), Does.Contain(ship), "The name it wears now.");
                Assert.That(byOldName.Rows.Select(s => s.ShipGuid), Does.Contain(ship),
                    "The name it wore then, held on the audit snapshot rather than the row.");
                Assert.That(byNothing.Rows, Is.Empty, "A name it never had matches nothing, so the search is not matching everything.");
            });

            // The id boxes are the same box: a guid searches ship and owner, not text.
            var byId = await store.QueryShips(Search(ship.ToString()), 0, 50);
            Assert.That(byId.Rows.Select(s => s.ShipGuid), Is.EquivalentTo(new[] { ship }));

            await pair.CleanReturnAsync();
        }

        private static DrydockShipFilter Search(string needle)
            => new(null, null, null, null, StrandedOnly: false, CurrentRoundId: null, Search: needle);

        private static DrydockRevisionRequest Request(Guid shipId, Guid owner, string name) => new()
        {
            ShipGuid = shipId,
            OwnerUserId = owner,
            ShipName = name,
            VesselProto = "TestVessel",
            SizeClass = nameof(ShipSizeClass.Cutter),
            Kind = DrydockRevisionKind.PlayerStore,
            MarkStored = true,
            ActorUserId = owner,
            CreatedRoundId = null,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1, 2, 3 },
            CapturedKeyHash = new byte[] { 4, 5, 6 },
            Checksum = new byte[] { 7, 8, 9 },
            SizeBytes = 3,
            Manifest = "{\"v\":1,\"e\":[]}",
        };

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
    }
}
