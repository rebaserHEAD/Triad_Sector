#nullable enable

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The durability layer's retrieve half against a real round trip: the drift gate, and the pin a
    /// fallback leaves on the revision it stepped past. Each case files a doctored image in place of
    /// a stored one, so the case reaches the gate it is about.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRetrieveDurabilityTest
    {
        private const string ItemProtoId = "SheetSteel1";
        private const string PhantomProtoId = "DrydockRetrieveTestPhantomPrototype";

        /// <summary>
        /// A current image naming a prototype that does not exist is refused with its own reason,
        /// before anything is materialized, and the row goes back to stored.
        /// </summary>
        [Test]
        public async Task ACurrentImageNamingMissingContentIsRefused()
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

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);
            await server.WaitPost(() => entMan.SpawnEntity(ItemProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f))));
            await pair.RunTicksSync(2);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var ship = shipId!.Value;
            var revision = (await store.GetShipHeader(ship))!.CurrentRevision;
            var original = await ReadImage(db, ship, revision);
            Assert.That(original.Entities.Any(e => e.Prototype == ItemProtoId), Is.True,
                "Control: the item is an entity in the image, or the doctoring below changes nothing.");

            await WriteImage(db, ship, revision, Rename(original, ItemProtoId, PhantomProtoId));

            var stagingBefore = 0;
            await server.WaitPost(() => stagingBefore = DrydockRoundTripTest.CountStagingMaps(entMan));
            var refusalsBefore = DrydockMetrics.DriftRefusals.Value;

            var refused = await DrydockTestHelpers.Quietly(pair, () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null)));

            var header = await store.GetShipHeader(ship);
            var audit = await store.GetAudit(ship);
            var driftRows = audit.Where(a => a.Action == DrydockAuditAction.DriftRefused).ToList();
            var liveCopies = 0;
            var stagingAfter = 0;
            await server.WaitPost(() =>
            {
                liveCopies = CountLiveCopies(entMan, ship);
                stagingAfter = DrydockRoundTripTest.CountStagingMaps(entMan);
            });

            Assert.Multiple(() =>
            {
                Assert.That(refused.Result, Is.EqualTo(DrydockRetrieveResult.ContentDrift));
                Assert.That(refused.Grid, Is.Null);
                Assert.That(liveCopies, Is.Zero, "Refused before the load: no grid carries this hull.");
                Assert.That(stagingAfter, Is.EqualTo(stagingBefore), "Nor was a staging map made and left behind.");
                Assert.That(header!.State, Is.EqualTo(DrydockShipState.Stored), "The claim went back.");
                Assert.That(audit.Any(a => a.Action == DrydockAuditAction.ClaimReleased), Is.True, "Through the wrapper's release, like any refusal.");
                Assert.That(driftRows, Has.Count.EqualTo(1));
                Assert.That(driftRows.Single().Revision, Is.EqualTo(revision), "The current revision is the one refused.");
                Assert.That(driftRows.Single().ActorUserId, Is.EqualTo(owner));
                Assert.That(driftRows.Single().Reason, Does.Contain(PhantomProtoId), "The row names the id that did not resolve.");
                Assert.That(audit.Any(a => a.Action is DrydockAuditAction.Fallback or DrydockAuditAction.RevisionPinned), Is.False,
                    "A drifted current image is never answered with an older one.");
                Assert.That(DrydockMetrics.DriftRefusals.Value, Is.GreaterThan(refusalsBefore));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A current image that passes the drift gate and still will not load falls back to the
        /// revision before it, and is pinned so the stores that follow cannot prune the newer state.
        /// </summary>
        [Test]
        public async Task ACurrentImageThatWillNotLoadIsPinnedAndTheOlderOneRetrieves()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);
            var (current, older, ship) = await StoreTwice(pair, drydock, store, shipGrid, owner, station);

            await WriteImage(db, ship, current, Unloadable(await ReadImage(db, ship, current)));

            var fallbacksBefore = DrydockMetrics.RetrieveFallbacks.Value;
            var retrieved = await DrydockTestHelpers.Quietly(pair, () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null)));
            await pair.RunTicksSync(5);

            var pinned = await PinnedRevisions(db, ship);
            var audit = await store.GetAudit(ship);
            var fallback = audit.Where(a => a.Action == DrydockAuditAction.Fallback).ToList();
            var pinRows = audit.Where(a => a.Action == DrydockAuditAction.RevisionPinned).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), "The older revision is a ship.");
                Assert.That(pinned, Is.EqualTo(new[] { current }), "The stepped-past revision is pinned, and only it.");
                Assert.That(pinRows, Has.Count.EqualTo(1));
                Assert.That(pinRows.Single().Revision, Is.EqualTo(current));
                Assert.That(pinRows.Single().ActorUserId, Is.Null, "The system pinned it.");
                Assert.That(pinRows.Single().Reason, Does.Contain("a retrieve stepped past it"));
                Assert.That(fallback, Has.Count.EqualTo(1));
                Assert.That(fallback.Single().Revision, Is.EqualTo(older), "The fallback row names the revision that came back.");
                Assert.That(DrydockMetrics.RetrieveFallbacks.Value, Is.GreaterThan(fallbacksBefore));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An older image reached only because the current one will not load, and refused for drift,
        /// is stepped past and pinned rather than ending the walk; when nothing older loads either, the
        /// refusal names drift, not an unreadable ship.
        /// </summary>
        [Test]
        public async Task ALadderThatRunsOutOnADriftedOlderImageRefusesForDrift()
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

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);
            await server.WaitPost(() => entMan.SpawnEntity(ItemProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f))));
            await pair.RunTicksSync(2);

            var (current, older, ship) = await StoreTwice(pair, drydock, store, shipGrid, owner, station);

            var olderImage = await ReadImage(db, ship, older);
            Assert.That(olderImage.Entities.Any(e => e.Prototype == ItemProtoId), Is.True, "Control: the older image carries the item.");
            await WriteImage(db, ship, older, Rename(olderImage, ItemProtoId, PhantomProtoId));
            await WriteImage(db, ship, current, Unloadable(await ReadImage(db, ship, current)));

            var refused = await DrydockTestHelpers.Quietly(pair, () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null)));

            var header = await store.GetShipHeader(ship);
            var audit = await store.GetAudit(ship);
            var driftRows = audit.Where(a => a.Action == DrydockAuditAction.DriftRefused).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(refused.Result, Is.EqualTo(DrydockRetrieveResult.ContentDrift));
                Assert.That(header!.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(driftRows, Has.Count.EqualTo(1));
                Assert.That(driftRows.Single().Revision, Is.EqualTo(older), "The refused image is the older one.");
                Assert.That(driftRows.Single().Reason, Does.Contain(PhantomProtoId));
            });

            Assert.That(await PinnedRevisions(db, ship), Is.EqualTo(new[] { older, current }),
                "Both were stepped past and pinned: the current one that would not load and the older one that drifted.");

            await pair.CleanReturnAsync();
        }

        /// <summary>Stores the ship, retrieves it and stores it again, for a current revision and an older one that both hold images.</summary>
        private static async Task<(int Current, int Older, Guid Ship)> StoreTwice(
            TestPair pair, DrydockSystem drydock, DrydockStore store, EntityUid shipGrid, Guid owner, EntityUid station)
        {
            var (first, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(first, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);
            var ship = shipId!.Value;

            var out1 = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null));
            Assert.That(out1.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var (second, _) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(out1.Grid!.Value, owner, null));
            Assert.That(second, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var current = (await store.GetShipHeader(ship))!.CurrentRevision;
            var older = (await store.ListRetrievableRevisions(ship)).First(r => r < current);
            return (current, older, ship);
        }

        private static int CountLiveCopies(IEntityManager entMan, Guid ship)
        {
            var count = 0;
            var query = entMan.AllEntityQueryEnumerator<DrydockIdentityComponent>();
            while (query.MoveNext(out _, out var identity))
            {
                if (identity.ShipId == ship)
                    count++;
            }

            return count;
        }

        private static async Task<DrydockImage> ReadImage(IServerDbManager db, Guid ship, int revision)
        {
            var image = await db.RunTriadDbCommand(
                (context, token) => db.DrydockImages.Get(context, new DrydockImageKey(ship, revision), token),
                CancellationToken.None);

            return image!;
        }

        private static Task WriteImage(IServerDbManager db, Guid ship, int revision, DrydockImage image)
        {
            return db.RunTriadDbCommand(
                (context, token) => db.DrydockImages.Put(context, new DrydockImageKey(ship, revision), image, token),
                CancellationToken.None);
        }

        /// <summary>The image with every entity of prototype <paramref name="from"/> recorded under <paramref name="to"/> instead.</summary>
        private static DrydockImage Rename(DrydockImage image, string from, string to)
        {
            return image with
            {
                Entities = image.Entities.Select(e => e.Prototype == from ? e with { Prototype = to } : e).ToList(),
            };
        }

        /// <summary>The image with a row on its grid entity naming a component no registration has, which the load refuses.</summary>
        private static DrydockImage Unloadable(DrydockImage image)
        {
            return image with
            {
                Entities = image.Entities
                    .Select(entity => entity.Id != image.GridId
                        ? entity
                        : entity with { Rows = new System.Collections.Generic.Dictionary<string, string>(entity.Rows) { ["DrydockTestNoSuchComponent"] = "{}" } })
                    .ToList(),
            };
        }

        private static Task<int[]> PinnedRevisions(IServerDbManager db, Guid ship)
        {
            return db.RunTriadDbCommand(async (context, token) => await context.DrydockRevision.AsNoTracking()
                .Where(r => r.ShipGuid == ship && r.Pinned)
                .Select(r => r.Revision)
                .OrderBy(r => r)
                .ToArrayAsync(token), CancellationToken.None);
        }
    }
}
