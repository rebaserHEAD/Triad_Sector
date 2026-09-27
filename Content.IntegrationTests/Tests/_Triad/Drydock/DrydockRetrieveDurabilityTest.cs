#nullable enable

using System;
using System.Collections.Generic;
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
using Robust.Shared.Map.Components;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The durability layer's retrieve half against a real round trip: the drift gate, the fallback to
    /// an older revision and the bound on failed loads, each on the timeline and none pinning a
    /// revision. Each case files a doctored image in place of a stored one, so the case reaches the
    /// gate it is about.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRetrieveDurabilityTest
    {
        private const string ItemProtoId = "SheetSteel1";
        private const string PhantomProtoId = "DrydockRetrieveTestPhantomPrototype";
        private const string LoadFailProbeId = "DrydockLoadFailProbeDummy";

        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: DrydockLoadFailProbeDummy
  components:
  - type: DrydockLoadFailProbe
";

        /// <summary>
        /// While armed, throws from the restore raise at an entity holding <see cref="DrydockLoadFailProbeComponent"/>, so a
        /// load of any image carrying one fails after the whole-tick core, and counts each throw.
        /// </summary>
        private sealed class DrydockLoadFailProbeSystem : EntitySystem
        {
            public bool Armed;
            public int Thrown;

            public override void Initialize()
            {
                base.Initialize();
                SubscribeLocalEvent<DrydockLoadFailProbeComponent, GridRestoredEvent>(OnRestored);
            }

            private void OnRestored(Entity<DrydockLoadFailProbeComponent> ent, ref GridRestoredEvent args)
            {
                if (!Armed)
                    return;

                Thrown++;
                throw new InvalidOperationException("The load-failure probe threw, as armed.");
            }
        }

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
        /// A current image carrying twelve components nothing registers, beside a prototype that no longer exists, is
        /// refused before the load with every one of them named, and the ship stays stored. The control is the same image
        /// before the doctoring, whose pre-flight carries none of those names.
        /// </summary>
        [Test]
        public async Task ACurrentImageCarryingUnregisteredComponentsIsRefusedNamingEveryOne()
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
            var components = Enumerable.Range(0, 12).Select(i => $"DrydockTestNoSuchComponent{i:D2}").ToList();

            var clean = await store.PreflightImage(ship, revision);
            Assert.That(clean!.ComponentNames.Intersect(components), Is.Empty, "Control: the stored image carries none of the names the doctoring adds.");

            var original = await ReadImage(db, ship, revision);
            var doctored = Rename(original, ItemProtoId, PhantomProtoId);
            doctored = doctored with
            {
                Entities = doctored.Entities
                    .Select(entity => entity.Id != doctored.GridId
                        ? entity
                        : entity with { Rows = entity.Rows.Concat(components.Select(name => KeyValuePair.Create(name, "{}"))).ToDictionary() })
                    .ToList(),
            };
            await WriteImage(db, ship, revision, doctored);

            var stagingBefore = 0;
            await server.WaitPost(() => stagingBefore = DrydockRoundTripTest.CountStagingMaps(entMan));

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
                Assert.That(liveCopies, Is.Zero, "Refused before the load: no grid carries this hull.");
                Assert.That(stagingAfter, Is.EqualTo(stagingBefore), "Nor was a staging map made and left behind.");
                Assert.That(header!.State, Is.EqualTo(DrydockShipState.Stored), "The ship stays stored.");
                Assert.That(driftRows, Has.Count.EqualTo(1));
                Assert.That(driftRows.Single().Reason, Does.Contain(PhantomProtoId), "The row names the prototype.");
                foreach (var name in components)
                    Assert.That(driftRows.Single().Reason, Does.Contain(name), $"The row names {name}.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A stored entity whose prototype id the real migration mappings delete passes the drift gate, which does not refuse
        /// a deletion, and the retrieve hands the mappings to the load: the entity is dropped and the ship comes back
        /// without it. The control is the image before the doctoring, which carries the item.
        /// </summary>
        [Test]
        public async Task AStoredEntityWhosePrototypeIsDeletedIsDroppedAndTheShipComesBack()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var deleted = drydock.MigrationTable.Deleted.Order(StringComparer.Ordinal).First();

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
            Assert.That(original.Entities.Count(e => e.Prototype == ItemProtoId), Is.EqualTo(1), "Control: the item is one entity in the image.");

            await WriteImage(db, ship, revision, Rename(original, ItemProtoId, deleted));

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null));

            var prototypes = new List<string?>();
            await server.WaitPost(() =>
            {
                var stack = new Stack<EntityUid>();
                stack.Push(retrieved.Grid!.Value);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    prototypes.Add(entMan.GetComponent<MetaDataComponent>(current).EntityPrototype?.ID);
                    var children = entMan.GetComponent<TransformComponent>(current).ChildEnumerator;
                    while (children.MoveNext(out var child))
                        stack.Push(child);
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), $"A deleted prototype ({deleted}) is not a refusal.");
                Assert.That(prototypes, Does.Not.Contain(ItemProtoId).And.Not.Contain(deleted), "The dropped entity is not on the ship.");
                Assert.That(prototypes, Is.SupersetOf(original.Entities.Select(e => e.Prototype).Where(p => p != ItemProtoId).Distinct()),
                    "Every other prototype the image held came back.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A current image that passes the drift gate and still will not load falls back to the
        /// revision before it, with a fallback row naming the revision that came back and no revision
        /// pinned.
        /// </summary>
        [Test]
        public async Task ACurrentImageThatWillNotLoadFallsBackToTheOlderOne()
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

            await WriteImage(db, ship, current, DrydockRoundTripTest.Unloadable(await ReadImage(db, ship, current)));

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
                Assert.That(pinned, Is.Empty, "The retrieve pins nothing.");
                Assert.That(pinRows, Is.Empty, "The retrieve pins nothing.");
                Assert.That(fallback, Has.Count.EqualTo(1));
                Assert.That(fallback.Single().Revision, Is.EqualTo(older), "The fallback row names the revision that came back.");
                Assert.That(DrydockMetrics.RetrieveFallbacks.Value, Is.GreaterThan(fallbacksBefore));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A current image holding a row that is not the image's encoding is a failed load before anything is allocated:
        /// the retrieve falls back to the revision before it with a fallback row, nothing escapes, and no map is left
        /// behind, staging or otherwise. Control: the older revision comes back as a ship.
        /// </summary>
        [Test]
        public async Task ACurrentImageThatWillNotDecodeFallsBackWithNoMapLeft()
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
            var (current, older, ship) = await StoreTwice(pair, drydock, store, shipGrid, owner, station);

            await WriteImage(db, ship, current, Undecodable(await ReadImage(db, ship, current)));

            var mapsBefore = 0;
            await server.WaitPost(() => mapsBefore = CountMaps(entMan));

            var retrieved = await DrydockTestHelpers.Quietly(pair, () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null)));
            await pair.RunTicksSync(5);

            var mapsAfter = 0;
            var staging = -1;
            await server.WaitPost(() =>
            {
                mapsAfter = CountMaps(entMan);
                staging = DrydockRoundTripTest.CountStagingMaps(entMan);
            });

            var fallback = (await store.GetAudit(ship)).Where(a => a.Action == DrydockAuditAction.Fallback).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), "The older revision is a ship.");
                Assert.That(fallback, Has.Count.EqualTo(1));
                Assert.That(fallback.Single().Revision, Is.EqualTo(older), "The fallback row names the revision that came back.");
                Assert.That(staging, Is.Zero, "No staging map is left.");
                Assert.That(mapsAfter, Is.EqualTo(mapsBefore), "No map is left behind by the revision that would not decode.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An older image reached only because the current one will not load, and refused for drift,
        /// is stepped past rather than ending the walk; when nothing older loads either, the refusal
        /// names drift, not an unreadable ship, and no revision is pinned.
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
            await WriteImage(db, ship, current, DrydockRoundTripTest.Unloadable(await ReadImage(db, ship, current)));

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

            Assert.That(await PinnedRevisions(db, ship), Is.Empty, "The retrieve pins nothing.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Three revisions whose images all fail in a restore handler, after the whole-tick core: the walk stops at the second
        /// failed load, never loads the third, and refuses with one row naming both, the ship stored, no copy left and no
        /// revision pinned. Controls: all three revisions hold images, and the probe threw once per load.
        /// </summary>
        [Test]
        public async Task TheWalkStopsAtTheSecondFailedLoad()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();
            var probe = server.System<DrydockLoadFailProbeSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);
            await server.WaitPost(() => entMan.SpawnEntity(LoadFailProbeId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f))));
            await pair.RunTicksSync(2);

            var (newest, ship) = await StoreThrice(pair, drydock, store, shipGrid, owner, station);

            DrydockRetrieve refused;
            probe.Thrown = 0;
            probe.Armed = true;
            try
            {
                refused = await DrydockTestHelpers.Quietly(pair, () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null)));
            }
            finally
            {
                probe.Armed = false;
            }

            var header = await store.GetShipHeader(ship);
            var pinned = await PinnedRevisions(db, ship);
            var refusals = (await store.GetAudit(ship)).Where(a => a.Action == DrydockAuditAction.LoadRefused).ToList();
            var copies = -1;
            await server.WaitPost(() => copies = CountLiveCopies(entMan, ship));

            Assert.Multiple(() =>
            {
                Assert.That(newest, Has.Length.EqualTo(3), "The control: three revisions hold images.");
                Assert.That(refused.Result, Is.EqualTo(DrydockRetrieveResult.NoReadableRevision));
                Assert.That(probe.Thrown, Is.EqualTo(2), "Two loads ran; the third revision never loaded.");
                Assert.That(refusals, Has.Count.EqualTo(1), "One row records the refusal.");
                Assert.That(refusals.Single().Revision, Is.EqualTo(newest[0]), "The row sits on the newer of the two.");
                Assert.That(refusals.Single().Reason, Does.Contain($"revisions {newest[0]} and {newest[1]}"), "The row names both.");
                Assert.That(refusals.Single().ActorUserId, Is.EqualTo(owner), "The retrieving owner is the actor.");
                Assert.That(pinned, Is.Empty, "The retrieve pins nothing.");
                Assert.That(header!.State, Is.EqualTo(DrydockShipState.Stored), "The claim went back; the ship is still stored.");
                Assert.That(copies, Is.Zero, "No copy of the hull is left.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Stores the ship and twice retrieves and stores it again, for three revisions that hold images, newest first.</summary>
        private static async Task<(int[] Newest, Guid Ship)> StoreThrice(
            TestPair pair, DrydockSystem drydock, DrydockStore store, EntityUid shipGrid, Guid owner, EntityUid station)
        {
            var (first, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(first, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);
            var ship = shipId!.Value;

            for (var i = 0; i < 2; i++)
            {
                var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null));
                Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
                await pair.RunTicksSync(5);

                var (again, _) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(retrieved.Grid!.Value, owner, null));
                Assert.That(again, Is.EqualTo(DrydockStoreResult.Success));
                await pair.RunTicksSync(5);
            }

            var newest = (await store.ListRetrievableRevisions(ship)).OrderByDescending(r => r).ToArray();
            return (newest, ship);
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

        /// <summary>The image with its grid's Transform row replaced by text that is not JSON.</summary>
        private static DrydockImage Undecodable(DrydockImage image)
        {
            return image with
            {
                Entities = image.Entities
                    .Select(e => e.Id == image.GridId
                        ? e with { Rows = new Dictionary<string, string>(e.Rows) { ["Transform"] = "{ this is not json" } }
                        : e)
                    .ToList(),
            };
        }

        private static int CountMaps(IEntityManager entMan)
        {
            var count = 0;
            var query = entMan.AllEntityQueryEnumerator<MapComponent>();
            while (query.MoveNext(out _))
                count++;

            return count;
        }

        /// <summary>The image with every entity of prototype <paramref name="from"/> recorded under <paramref name="to"/> instead.</summary>
        private static DrydockImage Rename(DrydockImage image, string from, string to)
        {
            return image with
            {
                Entities = image.Entities.Select(e => e.Prototype == from ? e with { Prototype = to } : e).ToList(),
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

    /// <summary>
    /// The load-failure probe's marker for <see cref="DrydockRetrieveDurabilityTest"/>. Registered because the integration test
    /// assembly is a content assembly (PoolManager.cs:101).
    /// </summary>
    [RegisterComponent]
    public sealed partial class DrydockLoadFailProbeComponent : Component;
}
