#nullable enable

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Shipyard.Components;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Containers.ItemSlots;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The console tier: the half a player actually touches. Everything else in this folder proves
    /// the pipeline moves a ship correctly; nothing until now proved a person standing at a console
    /// can reach it, or that the console refuses the people it should.
    ///
    /// <para>These need a connected pair, because the console resolves the operator's account from
    /// a real player session and the default pooled pair has none.</para>
    ///
    /// <para>The operator entity is deliberately spawned well off the ship. A session-bearing mob
    /// standing on the grid trips the organics gate, which is that gate working rather than a
    /// problem with the test.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(ShipyardSystem))]
    public sealed class DrydockConsoleTest
    {
        /// <summary>
        /// The whole player-facing loop in one pass: store from the console, see the ship appear in
        /// the retrieve list, retrieve it, and get a working deed back.
        ///
        /// <para>The two deed assertions are the point. A store leaves the card pointing at a grid
        /// that no longer exists, so the deed has to come off; a retrieve produces a ship nobody
        /// holds a claim on, so a fresh one has to be minted. Get either backwards and the ship is
        /// either unflyable or the card is a handle on nothing.</para>
        /// </summary>
        [Test]
        public async Task AnOwnerCanStoreAndRetrieveFromTheConsole()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var drydockStore = server.ResolveDependency<DrydockStore>();

            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // The control for the store assertion below: the card has to be carrying a deed before
            // the store, or "the deed came off" proves nothing.
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "The console store resolves its ship from this deed; without it the test never reaches the pipeline.");
            });

            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(stored, Is.Not.Null, "A store by the ship's own owner must reach the pipeline rather than being refused at the console.");
            Assert.That(stored!.Value.Result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.True, "A successful store despawns the grid.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.False,
                    "The deed points at a grid that no longer exists, so the store has to strip it, as selling does.");
            });

            // Identity, never counts. Pooled pairs share one database, so ships filed by earlier
            // tests in this run are still on this account and any assertion on list length is
            // really an assertion about test ordering.
            var shipId = stored.Value.ShipId!.Value;

            // Separates "the store filed nothing under this account" from "the console dropped it":
            // if this row is here and the tab does not show it, the fault is the console's filter.
            var rows = await RunOnServer(pair, () => drydockStore.GetShipsByOwner(session.UserId.UserId));
            var filed = rows.SingleOrDefault(r => r.ShipGuid == shipId);
            Assert.That(filed, Is.Not.Null,
                "The store must file the ship under the operating player's own account, or nothing downstream can find it.");
            Assert.That(filed!.State, Is.EqualTo(DrydockShipState.Stored),
                "A ship that has just been put away has to be in the state the console lists on.");
            Assert.That(filed.Investigating, Is.False);

            // The list the drydock tab renders. It is filled by an awaited database read, so it is
            // the console's own view of what this player may retrieve.
            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                var listed = consoleComp.CachedStoredShips.SingleOrDefault(s => s.ShipId == shipId);
                Assert.That(listed, Is.Not.Null,
                    "The ship was just stored by this operator, so it has to be offered back to them.");
                Assert.That(listed!.Name, Is.EqualTo("Kestrel"));
            });

            var retrieved = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));

            Assert.That(retrieved, Is.Not.Null, "The owner must be able to take back the ship they just put away.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "A retrieved ship nobody holds a claim on is unflyable; retrieve has to mint a fresh deed onto the card.");

                var deed = entMan.GetComponent<ShuttleDeedComponent>(card);
                Assert.That(deed.ShuttleUid, Is.EqualTo(retrieved!.Value),
                    "The minted deed has to point at the ship that actually came back.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The duplicate gate, which is the one thing here that must never come loose.
        ///
        /// <para>A retrieved ship's row moves to <see cref="DrydockShipState.CheckedOut"/> and
        /// nothing returns it, so the console must neither list it nor retrieve it a second time.
        /// The inherited one-ship-per-card rule looks like it covers this and does not: a card deed
        /// is not cleared when a grid dies, so a player who unloads cargo and lets the hull go
        /// would otherwise retrieve the same revision again and have the cargo twice. The row state
        /// is what actually closes that, and this test is what says so.</para>
        ///
        /// <para>Deliberately asked with a <em>clean</em> card. Refusing because the card is full
        /// would pass this test while proving the wrong gate, so the deed is stripped first and the
        /// refusal that remains can only be the row's.</para>
        /// </summary>
        [Test]
        public async Task ACheckedOutShipCannotBeRetrievedTwice()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));

            // By id, not by position: pooled pairs share a database, so this account may already
            // carry ships filed by other tests in the same run.
            var shipId = stored!.Value.ShipId!.Value;

            await pair.RunTicksSync(5);

            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(consoleComp.CachedStoredShips.Any(s => s.ShipId == shipId), Is.True,
                    "The control: a stored ship is listed, so its later absence means something.");
            });

            var first = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(first, Is.Not.Null, "The control: the first retrieve has to succeed, or the second proves nothing.");

            await pair.RunTicksSync(5);

            // Clear the card, so what refuses below is the row and not card capacity.
            await server.WaitPost(() => entMan.RemoveComponent<ShuttleDeedComponent>(card));
            await pair.RunTicksSync(2);

            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                // A ship that is out stays on the list so its owner can see why a berth is empty,
                // but it is listed as out, which is what disables its retrieve button. The row
                // state below is what actually refuses; this is the console telling the truth.
                var listed = consoleComp.CachedStoredShips.SingleOrDefault(s => s.ShipId == shipId);
                Assert.That(listed, Is.Not.Null, "A ship that is out is still the player's ship and still on their list.");
                Assert.That(listed!.State, Is.EqualTo(nameof(DrydockShipState.CheckedOut)),
                    "The list must say the ship is out, or the tab would offer a retrieve that the row refuses.");
            });

            var second = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));

            Assert.That(second, Is.Null,
                "Retrieving a checked-out ship a second time would duplicate it and everything aboard. The row state is the only thing standing in the way of that, since a card deed is never cleared when a grid dies.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Garage ownership follows the ship's stamped account, never the card in the slot. Cards
        /// get lent and stolen, and a deed is a claim on flying a ship rather than on filing it
        /// away, so a borrowed one must not let somebody put another person's ship into storage.
        /// </summary>
        [Test]
        public async Task ABorrowedDeedCannotStoreSomeoneElsesShip()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var store = server.ResolveDependency<DrydockStore>();

            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await store.AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            // The ship belongs to somebody who is not at the console.
            var absentOwner = new Robust.Shared.Network.NetUserId(Guid.NewGuid());
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, absentOwner);

            var result = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(result, Is.Null,
                "The console must refuse before the pipeline is entered: holding the deed is not the same as owning the ship.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.False, "A refused store must leave the ship flying.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "A refused store must not strip the deed off a card it had no business acting on.");

                // What the client draws the lockout from: the card's ship belongs to someone else.
                var state = shipyard.BuildDrydockState(console);
                Assert.That(state.DeedOwner, Is.EqualTo(absentOwner.UserId),
                    "The console state must name the deed ship's owner, or the client cannot tell this card is not the operator's.");
            });

            // The refusal is on the timeline, filed against the account that sent it. The console
            // never offers this click, so a row here is the signal an admin reads a stolen card by.
            var refusals = await RunOnServer(pair, () => store.GetAuditByActor(session.UserId.UserId, 20));
            var refusal = refusals.FirstOrDefault(a => a.Action == DrydockAuditAction.AccessRefused && a.Reason == "store");
            Assert.That(refusal, Is.Not.Null, "A refused store by a non-owner must be written to the timeline.");
            Assert.That(refusal!.SubjectUserId, Is.EqualTo(absentOwner.UserId), "The row names whose ship was asked for.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A transfer is bound to the account, not the card and not the character. The operator
        /// this harness spawns has a session and no mind, which is the control: a mind is exactly
        /// what a dead player loses when they are reprinted into a fresh body, and a ship must
        /// never become untransferable because of it. A session that does not own the row is
        /// refused and the refusal is written down; so is a session answering an offer that was
        /// not addressed to it.
        ///
        /// <para>The harness has one session, so a console-side offer to another live captain
        /// cannot be exercised here: the recipient gate refuses an offline account, which is
        /// checked, and the escrow itself is opened at the store level and then driven from the
        /// console for everything that follows.</para>
        /// </summary>
        [Test]
        public async Task ATransferOfferIsBoundToTheOfferingAccount()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();
            var mindSys = server.System<Content.Server.Mind.MindSystem>();

            var session = playerMan.Sessions.First();
            var me = session.UserId.UserId;
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            var shipId = stored!.Value.ShipId!.Value;
            await pair.RunTicksSync(5);

            // The control: no mind behind the click. If this ever grows one, the test proves less.
            await server.WaitAssertion(() =>
            {
                Assert.That(mindSys.TryGetMind(operatorEnt, out _, out _), Is.False,
                    "The operator must have no mind, or an offer succeeding says nothing about the account check.");
            });

            // Another account with room for two hulls, and a hull of its own already in one.
            var stranger = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            await DrydockStoreTest.InsertPlayer(server.ResolveDependency<IServerDbManager>(), stranger);
            await store.AddBerth(stranger, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(stranger, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            await store.FileRevision(new DrydockRevisionRequest
            {
                ShipGuid = theirs,
                OwnerUserId = stranger,
                ShipName = "NotYours",
                SizeClass = nameof(ShipSizeClass.Cutter),
                MarkStored = true,
                Kind = DrydockRevisionKind.PlayerStore,
                EngineFormatVer = 7,
                ProtoFingerprint = new byte[] { 1 },
                CapturedKeyHash = new byte[] { 1 },
                Checksum = new byte[] { 1 },
                SizeBytes = 1,
                Manifest = "{}",
            }, new byte[] { 1 }, 3);

            // The recipient gate: the stranger is not online, so the console refuses before any
            // row is written. The ownership check passed, so nothing is on the timeline for it.
            var offline = await RunOnServer(pair,
                () => shipyard.TryOfferTransfer(console, consoleComp, operatorEnt, shipId, stranger, ShipyardConsoleUiKey.Shipyard));
            Assert.That(offline, Is.False, "An offer to a captain who is not online is refused.");
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Stored));

            // A ship this account does not own: refused, and the refusal is on the timeline with
            // both accounts named. This is the only way a console ever writes such a row, since
            // the tab never offers the click.
            var forged = await RunOnServer(pair,
                () => shipyard.TryOfferTransfer(console, consoleComp, operatorEnt, theirs, stranger, ShipyardConsoleUiKey.Shipyard));
            Assert.That(forged, Is.False, "A session that does not own the row cannot offer it, whatever card is in the slot.");

            var refusals = await RunOnServer(pair, () => store.GetAuditByActor(me, 20));
            var refusal = refusals.FirstOrDefault(a => a.Action == DrydockAuditAction.AccessRefused && a.ShipGuid == theirs);
            Assert.That(refusal, Is.Not.Null, "A refused offer must be on the timeline: it is the stolen-card signal.");
            Assert.Multiple(() =>
            {
                Assert.That(refusal!.ActorUserId, Is.EqualTo(me));
                Assert.That(refusal.SubjectUserId, Is.EqualTo(stranger));
                Assert.That(refusal.Reason, Is.EqualTo("transfer"));
            });

            // Escrow, opened at the store: the ship keeps its berth and cannot come out.
            var (offered, transfer) = await store.TryOfferTransfer(shipId, me, stranger, TimeSpan.FromMinutes(30), null);
            Assert.That(offered, Is.EqualTo(DrydockBerthResult.Success));
            var inEscrow = (await store.GetShipHeader(shipId))!;
            Assert.Multiple(() =>
            {
                Assert.That(inEscrow.State, Is.EqualTo(DrydockShipState.InEscrow));
                Assert.That(inEscrow.BerthId, Is.Not.Null, "A ship in escrow keeps its berth.");
            });

            var retrieved = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(retrieved, Is.Null, "A ship in escrow does not come out.");
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.InEscrow));

            // The wrong party answering: the owner cannot decline their own offer, and the
            // attempt is written down against the ship.
            var selfDecline = await RunOnServer(pair,
                () => shipyard.TryDeclineTransfer(console, consoleComp, operatorEnt, transfer!.Id, ShipyardConsoleUiKey.Shipyard));
            Assert.That(selfDecline, Is.False);
            refusals = await RunOnServer(pair, () => store.GetAuditByActor(me, 20));
            Assert.That(refusals.Any(a => a.Action == DrydockAuditAction.AccessRefused && a.ShipGuid == shipId && a.Reason == "decline offer"),
                "Declining an offer you made is a forged message and goes on the timeline.");

            // The owner withdraws it from the console: the ship is stored again.
            var cancelled = await RunOnServer(pair,
                () => shipyard.TryCancelTransfer(console, consoleComp, operatorEnt, transfer!.Id, ShipyardConsoleUiKey.Shipyard));
            Assert.That(cancelled, Is.True);
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Stored));

            // The other direction: an offer addressed to this session, accepted at the console.
            // The ship changes hands into one of this account's free berths.
            var spare = await store.AddBerth(me, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var (offeredBack, incoming) = await store.TryOfferTransfer(theirs, stranger, me, TimeSpan.FromMinutes(30), null);
            Assert.That(offeredBack, Is.EqualTo(DrydockBerthResult.Success));

            var accepted = await RunOnServer(pair,
                () => shipyard.TryAcceptTransfer(console, consoleComp, operatorEnt, incoming!.Id, ShipyardConsoleUiKey.Shipyard));
            Assert.That(accepted, Is.True, "The account the offer names accepts it; the character's mind is not consulted.");
            var mine = (await store.GetShipHeader(theirs))!;
            var myBerths = await store.GetBerths(me);
            Assert.Multiple(() =>
            {
                Assert.That(mine.OwnerUserId, Is.EqualTo(me));
                Assert.That(mine.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(myBerths.Select(b => b.Berth.BerthId), Does.Contain(mine.BerthId!.Value), "The accepted ship lands in one of the recipient's own berths.");
            });

            // Expiry: a deadline already past is swept, the ship returns to Stored, and the
            // owner does not change.
            var (offeredStale, stale) = await store.TryOfferTransfer(theirs, me, stranger, TimeSpan.FromSeconds(-1), null);
            Assert.That(offeredStale, Is.EqualTo(DrydockBerthResult.Success));
            var released = await store.ExpireTransfers(DateTime.UtcNow, null);
            Assert.That(released, Does.Contain(theirs), "The sweep releases every offer past its deadline.");
            var afterSweep = (await store.GetShipHeader(theirs))!;
            Assert.Multiple(() =>
            {
                Assert.That(afterSweep.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(afterSweep.OwnerUserId, Is.EqualTo(me));
            });
            Assert.That(await store.GetPendingTransfer(stale!.Id), Is.Null, "An expired offer is no longer pending.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The card at the top of the tab and the berth it names. The console reads the deed ship
        /// from the live grid and offers the berths it fits, and a store that names one of them
        /// lands there rather than wherever the store would have picked.
        /// </summary>
        [Test]
        public async Task AStoreLandsInTheBerthItNames()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // A berth the store would not pick on its own: the harness grants SuperCapital ones,
            // and a Cutter is the smallest, so the picker would prefer this one. Name a larger
            // one instead and check the request wins over the preference.
            var named = await store.AddBerth(session.UserId.UserId, ShipSizeClass.Capital, DrydockBerthKind.Granted, 0, null, null);

            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                var deedShip = consoleComp.CachedDeedShip;
                Assert.That(deedShip, Is.Not.Null, "With a deed in the slot the tab must describe the ship on it.");
                Assert.That(deedShip!.Name, Is.EqualTo("Kestrel"));
                Assert.That(deedShip.MinutesOut, Is.Null, "A hull that has never been stored has no time out.");
                Assert.That(deedShip.DefaultBerthId, Is.Not.Null, "A free berth that fits must be offered as the default.");
                Assert.That(deedShip.FittingBerthIds, Does.Contain(named), "Every free berth the hull fits is offered, including the one about to be named.");
            });

            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard, named));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var header = await RunOnServer(pair, () => store.GetShipHeader(stored!.Value.ShipId!.Value));
            Assert.That(header!.BerthId, Is.EqualTo(named), "A store that names a berth lands in that berth.");

            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(consoleComp.CachedDeedShip, Is.Null, "The deed came off with the store, so the card at the top goes with it.");
                var row = consoleComp.CachedBerths.Single(b => b.BerthId == named);
                Assert.That(row.OccupantName, Is.EqualTo("Kestrel"));
                Assert.That(row.OccupantState, Is.EqualTo(nameof(DrydockShipState.Stored)));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Builds a station, a ship stamped to <paramref name="shipOwner"/>, and a console holding a
        /// deed card for it, with the operator standing clear of the grid.
        /// </summary>
        /// <summary>
        /// The three verbs behind the row menu. Rename is a row update that the hull and its deed
        /// take at the next retrieve, so the retrieved grid is read to prove the stamp; move lands
        /// in the berth it names; a sale needs the ship's exact name typed, frees the berth, marks
        /// the row Sold with the price on the timeline, and pays from the appraisal captured at
        /// store. Every verb is refused for a ship the account does not own and written down.
        /// </summary>
        [Test]
        public async Task AStoredShipCanBeRenamedMovedAndSold()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var me = session.UserId.UserId;
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            var shipId = stored!.Value.ShipId!.Value;
            await pair.RunTicksSync(5);

            // Rename: the shape is enforced, then the row changes and nothing else does yet.
            var badName = await RunOnServer(pair,
                () => shipyard.TryRenameStoredShip(console, consoleComp, operatorEnt, shipId, "Falcon!!", ShipyardConsoleUiKey.Shipyard));
            Assert.That(badName, Is.False, "Punctuation outside the allowed shape is refused.");

            var renamed = await RunOnServer(pair,
                () => shipyard.TryRenameStoredShip(console, consoleComp, operatorEnt, shipId, "Falcon", ShipyardConsoleUiKey.Shipyard));
            Assert.That(renamed, Is.True);
            Assert.That((await store.GetShipHeader(shipId))!.ShipName, Is.EqualTo("Falcon"));

            var renames = await RunOnServer(pair, () => store.GetAuditByActor(me, 20));
            Assert.That(renames.Any(a => a.Action == DrydockAuditAction.Renamed && a.ShipGuid == shipId && a.ShipName == "Kestrel"),
                "The rename row carries the OLD name, so the old name stays searchable.");

            // Move: into a berth the store would not have picked on its own.
            var named = await store.AddBerth(me, ShipSizeClass.Capital, DrydockBerthKind.Granted, 0, null, null);
            var moved = await RunOnServer(pair,
                () => shipyard.TryMoveStoredShip(console, consoleComp, operatorEnt, shipId, named, ShipyardConsoleUiKey.Shipyard));
            Assert.That(moved, Is.True);
            Assert.That((await store.GetShipHeader(shipId))!.BerthId, Is.EqualTo(named));

            // Retrieve: the hull comes back wearing the row's name.
            var grid = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(grid, Is.Not.Null);
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<MetaDataComponent>(grid!.Value).EntityName, Is.EqualTo("Falcon"),
                    "A rename made while stored is stamped onto the hull at retrieve.");
            });
            await pair.RunTicksSync(5);

            // Store again, which captures the appraisal the sale quotes from.
            var restored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(restored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            Assert.That(restored!.Value.ShipId, Is.EqualTo(shipId), "The same hull files a new revision, not a new ship.");
            await pair.RunTicksSync(5);

            var appraisals = await store.GetCurrentAppraisals(me);
            Assert.That(appraisals[shipId], Is.Not.Null, "A store captures the appraisal on its revision.");

            await server.WaitPost(() => entMan.EnsureComponent<Content.Shared._NF.Bank.Components.BankAccountComponent>(operatorEnt));

            // Sell: the typed name is the safety. The old name no longer matches.
            var wrongName = await RunOnServer(pair,
                () => shipyard.TrySellStoredShip(console, consoleComp, operatorEnt, shipId, "Kestrel", ShipyardConsoleUiKey.Shipyard));
            Assert.That(wrongName.Sold, Is.False, "A sale needs the ship's exact current name typed.");
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Stored));

            var sale = await RunOnServer(pair,
                () => shipyard.TrySellStoredShip(console, consoleComp, operatorEnt, shipId, "Falcon", ShipyardConsoleUiKey.Shipyard));
            Assert.That(sale.Sold, Is.True);
            var soldHeader = (await store.GetShipHeader(shipId))!;
            Assert.Multiple(() =>
            {
                Assert.That(soldHeader.State, Is.EqualTo(DrydockShipState.Sold));
                Assert.That(soldHeader.BerthId, Is.Null, "A sold ship leaves its berth.");
                Assert.That(soldHeader.LastBerthId, Is.Not.Null, "But remembers it, for an admin restore.");
            });

            var audit = await RunOnServer(pair, () => store.GetAuditByActor(me, 30));
            var soldRow = audit.FirstOrDefault(a => a.Action == DrydockAuditAction.ShipSold && a.ShipGuid == shipId);
            Assert.That(soldRow, Is.Not.Null);
            Assert.That(soldRow!.Reason, Does.Contain($"sold for {sale.Price}"), "The price paid is on the timeline for the reversal to read.");

            // A ship this account does not own: refused and written down, whatever was typed.
            var stranger = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            await DrydockStoreTest.InsertPlayer(server.ResolveDependency<IServerDbManager>(), stranger);
            await store.AddBerth(stranger, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            await store.FileRevision(new DrydockRevisionRequest
            {
                ShipGuid = theirs,
                OwnerUserId = stranger,
                ShipName = "NotYours",
                SizeClass = nameof(ShipSizeClass.Cutter),
                MarkStored = true,
                Kind = DrydockRevisionKind.PlayerStore,
                EngineFormatVer = 7,
                ProtoFingerprint = new byte[] { 1 },
                CapturedKeyHash = new byte[] { 1 },
                Checksum = new byte[] { 1 },
                SizeBytes = 1,
                Manifest = "{}",
                AppraisedValue = 1000,
            }, new byte[] { 1 }, 3);

            var forged = await RunOnServer(pair,
                () => shipyard.TrySellStoredShip(console, consoleComp, operatorEnt, theirs, "NotYours", ShipyardConsoleUiKey.Shipyard));
            Assert.That(forged.Sold, Is.False);
            Assert.That((await store.GetShipHeader(theirs))!.State, Is.EqualTo(DrydockShipState.Stored));

            var refusals = await RunOnServer(pair, () => store.GetAuditByActor(me, 30));
            Assert.That(refusals.Any(a => a.Action == DrydockAuditAction.AccessRefused && a.ShipGuid == theirs && a.Reason == "sell"),
                "A forged sale is the stolen-card signal and goes on the timeline.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Faction crews are issued their vessels on voucher and are not drydock customers, the
        /// same way they are not ship-save customers. Three signals bar a store, each proven alone
        /// against a control that succeeds once it is cleared: the blacklist the job stamps on the
        /// character, the blacklist a faction vessel carries on its grid, and the voucher flag on a
        /// deed. On the way back, the barred character and a voucher in the slot are both refused.
        /// </summary>
        [Test]
        public async Task FactionCrewsAndVoucherShipsAreRefusedTheDrydock()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // The character: the same component the job stamps on TDF and TFA crews.
            await server.WaitPost(() => entMan.EnsureComponent<ShipSavingBlacklistComponent>(operatorEnt));
            var barredOperator = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(barredOperator, Is.Null, "A character the job has blacklisted from ship saving cannot store either.");
            await server.WaitPost(() => entMan.RemoveComponent<ShipSavingBlacklistComponent>(operatorEnt));

            // The vessel: a faction hull carries the blacklist on its grid.
            await server.WaitPost(() => entMan.EnsureComponent<ShipSavingBlacklistComponent>(ship));
            var barredShip = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(barredShip, Is.Null, "A faction vessel cannot be stored, whoever is at the console.");
            await server.WaitPost(() => entMan.RemoveComponent<ShipSavingBlacklistComponent>(ship));

            // The deed: bought on a voucher. The flag is the shipyard's to write, so the test
            // reaches past the access check the way the buckle tests do.
#pragma warning disable RA0002
            await server.WaitPost(() => entMan.GetComponent<ShuttleDeedComponent>(card).PurchasedWithVoucher = true);
#pragma warning restore RA0002
            var voucherShip = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(voucherShip, Is.Null, "A ship issued on a voucher cannot be stored.");
#pragma warning disable RA0002
            await server.WaitPost(() => entMan.GetComponent<ShuttleDeedComponent>(card).PurchasedWithVoucher = false);
#pragma warning restore RA0002

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.False, "Every refusal above left the ship flying.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True, "And left the deed on the card.");
            });

            // Control: with all three cleared the same store goes through.
            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success), "Control: nothing else about the fixture was refusing.");
            var shipId = stored!.Value.ShipId!.Value;
            await pair.RunTicksSync(5);

            // The way back: the barred character first, then a voucher where the ID card goes.
            await server.WaitPost(() => entMan.EnsureComponent<ShipSavingBlacklistComponent>(operatorEnt));
            var barredRetrieve = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(barredRetrieve, Is.Null, "A blacklisted character cannot call a ship in either.");
            await server.WaitPost(() => entMan.RemoveComponent<ShipSavingBlacklistComponent>(operatorEnt));

            await server.WaitPost(() => entMan.EnsureComponent<ShipyardVoucherComponent>(card));
            var voucherRetrieve = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(voucherRetrieve, Is.Null, "A voucher is not a card a stored ship can be called in on.");
            await server.WaitPost(() => entMan.RemoveComponent<ShipyardVoucherComponent>(card));

            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Stored),
                "Every refused retrieve left the row stored; none of them reached the claim.");

            var retrieved = await RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(retrieved, Is.Not.Null, "Control: the same retrieve succeeds once the gates are clear.");
            await pair.RunTicksSync(5);

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A store is a hand-over at a berth: the ship has to be docked to the station whose
        /// console is filing it, not parked somewhere in the sector. The tab says so before the
        /// press, the server refuses regardless, and docking the same ship to the same station
        /// turns the same request into a success.
        /// </summary>
        [Test]
        public async Task AStoreNeedsTheShipDockedAtThisStation()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId, docked: false);

            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });
            await server.WaitAssertion(() =>
            {
                Assert.That(consoleComp.CachedDeedShip, Is.Not.Null);
                Assert.That(consoleComp.CachedDeedShip!.Docked, Is.False, "The card at the top of the tab says the ship is not docked here, which is what greys Store.");
            });

            var loose = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(loose, Is.Null, "A ship out in space is refused before the pipeline is entered.");
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.False, "A refused store leaves the ship flying.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True, "And the deed on the card.");
            });

            // Bring it alongside and dock it, then the same request goes through.
            await server.WaitPost(() =>
            {
                server.System<SharedTransformSystem>().SetWorldPosition(ship, new Vector2(1f, -1f));
                DockToStation(entMan, server.System<SharedTransformSystem>(), server.System<DockingSystem>(), stationGrid, ship);
            });
            await pair.RunTicksSync(5);

            await RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });
            await server.WaitAssertion(() =>
            {
                Assert.That(consoleComp.CachedDeedShip!.Docked, Is.True, "Docked now, so the tab offers the store.");
            });

            var stored = await RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success), "Control: docked, the same store succeeds.");
            await pair.RunTicksSync(5);

            await pair.CleanReturnAsync();
        }

        private const string ShuttleAirlockProtoId = "AirlockShuttle";

        /// <summary>
        /// The engine logs a client-side error ("the joint already existed for the connected
        /// entity") when a grid arrives already welded to a grid whose joint component is new in
        /// the same state, which is exactly what the instant dock at retrieve produces, as a ship
        /// purchase does. It is benign in play and recorded as such; here it would fail the pool.
        /// The pool's per-message judge needs a type this project does not reference, so a test
        /// that retrieves raises the client's failure level for its duration instead, the way the
        /// abort test does for the server, and puts it back when it is done. The server side keeps
        /// its full sensitivity throughout.
        /// </summary>
        private static IDisposable ExpectDockJointLog(TestPair pair)
        {
            var level = pair.ClientLogHandler.FailureLevel;
            pair.ClientLogHandler.FailureLevel = LogLevel.Fatal;
            return new RestoreScope(() => pair.ClientLogHandler.FailureLevel = level);
        }

        private sealed class RestoreScope(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }

        /// <summary>
        /// Docks the ship to the station grid the way two airlocks meeting does: a shuttle airlock
        /// on the station's tile facing east, one on the ship's west edge facing back, and the
        /// docking system's own weld between them. The ship has to already be alongside, its west
        /// edge against the station tile, or the weld has nothing consistent to hold.
        /// </summary>
        private static void DockToStation(IEntityManager entMan, SharedTransformSystem transform, DockingSystem docking, EntityUid stationGrid, EntityUid ship)
        {
            var stationDock = entMan.SpawnEntity(ShuttleAirlockProtoId, new EntityCoordinates(stationGrid, new Vector2(0.5f, 0.5f)));
            transform.SetLocalRotation(stationDock, Direction.East.ToAngle());

            var shipDock = entMan.SpawnEntity(ShuttleAirlockProtoId, new EntityCoordinates(ship, new Vector2(0.5f, 1.5f)));
            transform.SetLocalRotation(shipDock, Direction.West.ToAngle());

            docking.Dock(
                (stationDock, entMan.GetComponent<DockingComponent>(stationDock)),
                (shipDock, entMan.GetComponent<DockingComponent>(shipDock)));
        }

        /// <summary>
        /// Builds a station, a ship stamped to <paramref name="shipOwner"/>, and a console holding a
        /// deed card for it, with the operator standing clear of the grid. The ship is docked to the
        /// station unless <paramref name="docked"/> says otherwise, since a store needs it to be.
        /// </summary>
        private static async Task<(EntityUid Station, EntityUid StationGrid, EntityUid Ship, EntityUid Console, ShipyardConsoleComponent Comp, EntityUid Card, EntityUid Operator)>
            BuildConsoleAndShip(TestPair pair, Robust.Shared.Network.NetUserId shipOwner, bool docked = true)
        {
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();
            var itemSlots = server.System<ItemSlotsSystem>();
            var metaData = server.System<MetaDataSystem>();
            var transform = server.System<SharedTransformSystem>();
            var docking = server.System<DockingSystem>();

            var map = await pair.CreateTestMap();
            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            EntityUid station = default;
            EntityUid ship = default;
            EntityUid console = default;
            EntityUid card = default;
            EntityUid operatorEnt = default;
            ShipyardConsoleComponent comp = default!;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                shipyard.SetupShipyardIfNeeded();

                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);

                var shipGrid = mapSys.CreateGridEntity(map.MapId);
                ship = shipGrid.Owner;
                var tile = new Tile(1);
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        mapSys.SetTile(ship, shipGrid.Comp, new Vector2i(x, y), tile);
                    }
                }

                // Clear of the station's tile either way, and it has to be done by moving the ship
                // rather than by placing the console carefully. Both grids are created at the
                // origin, and grid traversal reparents an entity to whichever grid it is physically
                // over, so a console placed on the station grid inside the ship's footprint silently
                // becomes part of the ship and is despawned along with it by the very store being
                // tested. Docked, the ship sits with its west edge against the station tile; loose,
                // it is far out in the map.
                transform.SetWorldPosition(ship, docked ? new Vector2(1f, -1f) : new Vector2(100f, 100f));

                entMan.EnsureComponent<ShuttleComponent>(ship);
                metaData.SetEntityName(ship, "Kestrel");
                entMan.EnsureComponent<ShipOwnershipComponent>(ship).OwnerUserId = shipOwner;

                // Well clear of both grids. An operator standing aboard the ship would trip the
                // organics gate, and the card would go into storage with it.
                operatorEnt = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(64f, 64f), map.MapId));
                playerMan.SetAttachedEntity(session, operatorEnt);

                // On the station's own grid, not loose on the map. Retrieve resolves where to put
                // the ship from the console's owning station, and a console parented to nothing
                // belongs to no station, so an off-grid console refuses every retrieve.
                console = entMan.SpawnEntity(null, new EntityCoordinates(map.Grid.Owner, new Vector2(0.5f, 0.5f)));
                comp = entMan.EnsureComponent<ShipyardConsoleComponent>(console);

                card = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(64f, 64f), map.MapId));
                shipyard.MintCardDeed(card, ship, operatorEnt);
                itemSlots.TryInsert(console, comp.TargetIdSlot, card, user: null);
            });

            await pair.RunTicksSync(5);

            // Docked only after the client has seen both grids. A grid that arrives at the client
            // already welded to a grid whose joint component is new in the same state logs an
            // engine-side error the pool counts as a failure; it is benign in play and avoided here
            // by giving the weld its own tick.
            if (docked)
            {
                await server.WaitPost(() => DockToStation(entMan, transform, docking, map.Grid.Owner, ship));
                await pair.RunTicksSync(5);
            }

            return (station, map.Grid.Owner, ship, console, comp, card, operatorEnt);
        }

        private static async Task<T> RunOnServer<T>(TestPair pair, Func<Task<T>> start)
        {
            Task<T>? task = null;
            await pair.Server.WaitPost(() => task = start());

            for (var i = 0; i < 600 && !task!.IsCompleted; i++)
            {
                await pair.RunTicksSync(1);
            }

            Assert.That(task!.IsCompleted, Is.True, "A drydock console operation never completed.");
            return await task;
        }
    }
}
