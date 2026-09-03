#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server._NF.Market.Components;
using Content.Server.Database;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Wires;
using Content.Shared._NF.Market;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Atmos;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Lathe;
using Content.Shared.NodeContainer;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The first thing that ever moves a ship through the drydock. Everything either pipeline half
    /// does is unproven until this runs: the six Revive steps, both sidecars, the validation
    /// backstop, the manifest, and the claim.
    ///
    /// <para>It builds a ship rather than loading a roster vessel on purpose. A hand-built grid
    /// fails for one reason at a time, which is what you want from the test that establishes the
    /// round trip at all. The roster sweep is the separate test that answers whether real content
    /// survives, and breadth is its job rather than this one's.</para>
    ///
    /// <para>The airlock is not decoration. Its wire layout is the sharpest available probe of the
    /// map-init boundary: <c>WiresComponent.WiresList</c> is not a data field and the only thing
    /// that ever builds it is the map-init handler, which never fires again for a restored entity.
    /// Without the Revive step every panel on a retrieved ship opens empty, and nothing else about
    /// the ship looks wrong.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRoundTripTest
    {
        private const string AirlockProtoId = "Airlock";
        private const string ResearchServerProtoId = "ResearchAndDevelopmentServer";
        private const string LatheProtoId = "Protolathe";
        private const string LatheRecipeId = "SheetSteel";
        private const string PipeProtoId = "GasPipeStraight";
        private const string MarketItemProtoId = "SheetSteel1";
        private const string AudioProtoId = "Audio";

        [Test]
        public async Task AShipStoredComesBackWithItsContentsAndItsWires()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            // What the ship is made of, before it goes anywhere. Compared against the same census
            // afterwards this catches a drop, a duplicate, and a substitution that keeps the count
            // the same.
            var before = await CensusGrid(pair, shipGrid);
            Assert.That(before.Values.Sum(), Is.GreaterThan(0), "The test ship has to actually carry something.");

            var wiresBefore = await ReadWireCount(pair, airlock);
            Assert.That(wiresBefore, Is.GreaterThan(0),
                "A live airlock must have a populated wire layout, or this test cannot prove Revive rebuilt one.");

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
                Assert.That(shipId, Is.Not.Null, "A successful store names the hull it filed.");
            });

            await pair.RunTicksSync(5);
            Assert.That(entMan.Deleted(shipGrid), Is.True,
                "The grid is despawned only after the document is filed, so a live grid here means a half-committed store.");

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null, "The ship went in, so it has to come out.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<DrydockIdentityComponent>(retrieved!.Value, out var identity), Is.True,
                    "Identity is the one piece of state nothing else on the grid can reconstruct.");
                Assert.That(identity!.ShipId, Is.EqualTo(shipId!.Value),
                    "A retrieve must return the same hull, not a new one that looks similar.");
            });

            var after = await CensusGrid(pair, retrieved!.Value);
            Assert.That(after, Is.EqualTo(before),
                "Every prototype aboard comes back, exactly once each. A difference is a drop, a duplicate or a substitution.");

            // The map-init boundary, made concrete. This is the assertion the whole census on the
            // wiki exists to justify.
            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved.Value);
            Assert.That(retrievedAirlock, Is.Not.Null, "The airlock came back, or the census above would have failed.");

            var wiresAfter = await ReadWireCount(pair, retrievedAirlock!.Value);
            Assert.That(wiresAfter, Is.EqualTo(wiresBefore),
                "WiresList is not a data field, so this passes only because Revive rebuilt the layout by hand.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The berth is a parking spot: a successful retrieve empties it, and only once the ship is
        /// really out. A retrieve that fails after the claim leaves the berth exactly as it was, so
        /// the release never has to re-seat a berth another store may have taken in the meantime.
        /// The failure is induced by corrupting the stored document in place, which the ladder
        /// catches after the claim and before anything is materialized.
        /// </summary>
        [Test]
        public async Task ARetrieveVacatesTheBerthOnlyWhenItSucceeds()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            var berth = await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            var seated = (await store.LoadCurrent(shipId!.Value))!.Ship;
            Assert.That(seated.BerthId, Is.EqualTo(berth), "A stored ship sits in the berth the store found for it.");

            // Break the only document, so the retrieve claims the row, finds nothing that verifies,
            // and releases. The two error lines that produces are the ladder doing its job.
            var original = await ReadBlobs(db, shipId.Value);
            await WriteBlobs(db, shipId.Value, new byte[] { 1, 2, 3 });

            var failureLevel = pair.ServerLogHandler.FailureLevel;
            pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;
            var refused = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));
            pair.ServerLogHandler.FailureLevel = failureLevel;

            Assert.That(refused, Is.Null, "A document that fails its checksum must not come back as a ship.");

            var afterRefusal = (await store.LoadCurrent(shipId.Value))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(afterRefusal.State, Is.EqualTo(DrydockShipState.Stored), "A failed retrieve releases the claim.");
                Assert.That(afterRefusal.BerthId, Is.EqualTo(berth), "A failed retrieve leaves the berth exactly as it was.");
            });

            // Mend it and bring it out for real.
            await WriteBlobs(db, shipId.Value, original);
            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);
            await pair.RunTicksSync(5);

            var afterRetrieve = (await store.LoadCurrent(shipId.Value))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(afterRetrieve.State, Is.EqualTo(DrydockShipState.CheckedOut));
                Assert.That(afterRetrieve.BerthId, Is.Null, "The ship is out, so its slot is empty as far as the player can see.");
                Assert.That(afterRetrieve.LastBerthId, Is.EqualTo(berth), "The slot it came out of is remembered, so it goes back there.");
            });

            var slots = await store.GetBerths(owner);
            Assert.That(slots.Single(s => s.Berth.BerthId == berth).Occupant, Is.Null);

            // And back in, to the same slot.
            var (again, sameShip) = await RunOnServer(pair, () => drydock.TryStoreShip(retrieved!.Value, owner, null));
            Assert.Multiple(() =>
            {
                Assert.That(again, Is.EqualTo(DrydockStoreResult.Success));
                Assert.That(sameShip, Is.EqualTo(shipId), "A re-store files against the same hull.");
            });

            var reseated = (await store.LoadCurrent(shipId.Value))!.Ship;
            Assert.That(reseated.BerthId, Is.EqualTo(berth));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The row is authoritative for ownership and the grid learns it at retrieve. Without the
        /// re-stamp a transferred ship comes back carrying its previous owner, the console refuses
        /// the new owner's store as "not yours", and the old owner could file it back under
        /// themselves. This is the round trip that proves the loop is closed.
        /// </summary>
        [Test]
        public async Task ATransferredShipComesBackStampedForItsNewOwner()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var seller = Guid.NewGuid();
            var buyer = Guid.NewGuid();
            await InsertPlayer(db, seller);
            await InsertPlayer(db, buyer);
            await store.AddBerth(seller, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(buyer, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);
            await server.WaitPost(() => entMan.EnsureComponent<ShipOwnershipComponent>(shipGrid).OwnerUserId = new NetUserId(seller));

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, seller, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            var (moved, _) = await store.TryTransferShip(shipId!.Value, seller, buyer, null, "sale");
            Assert.That(moved, Is.EqualTo(DrydockBerthResult.Success));

            // The previous owner can no longer bring it out; the new one can.
            var refused = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, seller, station, null));
            Assert.That(refused, Is.Null, "A ship that changed hands is not the previous owner's to retrieve.");

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, buyer, station, null));
            Assert.That(retrieved, Is.Not.Null);
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var ownership = entMan.GetComponent<ShipOwnershipComponent>(retrieved!.Value);
                Assert.That(ownership.OwnerUserId.UserId, Is.EqualTo(buyer),
                    "The grid must carry the row's owner, or the console refuses the buyer's store and the seller's store files it back under them.");
            });

            await pair.CleanReturnAsync();
        }

        private static Task<byte[]> ReadBlobs(IServerDbManager db, Guid shipId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                var row = await context.DrydockBlob.AsNoTracking().SingleAsync(b => b.ShipGuid == shipId, token);
                return row.Blob;
            }, CancellationToken.None);
        }

        private static Task WriteBlobs(IServerDbManager db, Guid shipId, byte[] bytes)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                var rows = await context.DrydockBlob.Where(b => b.ShipGuid == shipId).ToListAsync(token);
                foreach (var row in rows)
                    row.Blob = bytes;

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }

        /// <summary>
        /// Damage is the second reason a ship needs a fidelity layer at all, and it is a different
        /// reason from the first. <c>DamageableComponent.Damage</c> is not unserializable, it is
        /// declared read-only to the serializer, so it is never written and a shot-up hull comes
        /// back pristine. That is a free repair on every combat vessel in a fork whose ships get
        /// shot at, which is why the sidecar carries the raw damage dictionary across.
        /// </summary>
        [Test]
        public async Task DamageSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var damageSys = server.System<DamageableSystem>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            FixedPoint2 damageBefore = default;

            await server.WaitPost(() =>
            {
                var blunt = protoMan.Index<DamageTypePrototype>("Blunt");
                var specifier = new DamageSpecifier(blunt, FixedPoint2.New(37));
                damageSys.TryChangeDamage(airlock, specifier, ignoreResistances: true);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                damageBefore = entMan.GetComponent<DamageableComponent>(airlock).TotalDamage;
                Assert.That(damageBefore, Is.GreaterThan(FixedPoint2.Zero),
                    "The control: the airlock has to actually be damaged, or the comparison after the round trip proves nothing.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved!.Value);
            Assert.That(retrievedAirlock, Is.Not.Null);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<DamageableComponent>(retrievedAirlock!.Value).TotalDamage,
                    Is.EqualTo(damageBefore),
                    "Damage is read-only to the serializer, so this passes only because the sidecar carried it and the rehydrate pass applied it.");

                Assert.That(entMan.HasComponent<DrydockDamageSidecarComponent>(retrievedAirlock.Value), Is.False,
                    "The sidecar is scaffolding for the crossing. Leaving it aboard would re-apply the same damage on the next retrieve.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The second of the three Revive steps the map-init census added. Device network
        /// membership is registration held by the network system rather than state on the device,
        /// and joining happens on map init, which never fires again for a restored entity. Without
        /// the step a retrieved ship's alarms, sensors and consoles come back present, powered, and
        /// deaf: nothing about them looks wrong from the outside.
        /// </summary>
        [Test]
        public async Task DeviceNetworkMembershipComesBack()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var deviceNet = server.System<DeviceNetworkSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            // The airlock the wires assertion uses carries DeviceNetwork too, so one entity covers
            // both steps.
            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            await server.WaitAssertion(() =>
            {
                var device = entMan.GetComponent<DeviceNetworkComponent>(airlock);
                Assert.That(deviceNet.IsDeviceConnected(airlock, device), Is.True,
                    "The control: a live airlock has to be on its network, or the check after the round trip means nothing.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved!.Value);
            Assert.That(retrievedAirlock, Is.Not.Null);

            await server.WaitAssertion(() =>
            {
                var device = entMan.GetComponent<DeviceNetworkComponent>(retrievedAirlock!.Value);
                Assert.That(deviceNet.IsDeviceConnected(retrievedAirlock.Value, device), Is.True,
                    "Membership is not serialized state, so this passes only because Revive re-ran the join by hand.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The third. A research client's link to its server is a plain property on one side and a
        /// view-variables list on the other, so neither end serializes, and the only thing that
        /// ever sets it is a map-init scan of the client's own grid. A retrieved ship carrying its
        /// own R&amp;D server would have every lathe disconnected from it until somebody opened the
        /// server-selection menu by hand.
        /// </summary>
        [Test]
        public async Task ResearchClientsComeBackRegistered()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            EntityUid lathe = default;

            await server.WaitPost(() =>
            {
                entMan.SpawnEntity(ResearchServerProtoId, new EntityCoordinates(shipGrid, new Vector2(0f, 0f)));
                lathe = entMan.SpawnEntity(LatheProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<ResearchClientComponent>(lathe).Server, Is.Not.Null,
                    "The control: the lathe has to find its server while the ship is live, which is the map-init scan doing its job.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            var retrievedLathe = await FindChildWithComponent<ResearchClientComponent>(pair, retrieved!.Value);
            Assert.That(retrievedLathe, Is.Not.Null, "The lathe came back with the ship.");

            await server.WaitAssertion(() =>
            {
                var client = entMan.GetComponent<ResearchClientComponent>(retrievedLathe!.Value);
                Assert.That(client.Server, Is.Not.Null,
                    "Neither end of the registration is a data field, so this passes only because Revive re-ran the grid scan.");

                Assert.That(entMan.HasComponent<ResearchServerComponent>(client.Server!.Value), Is.True,
                    "And it has to be pointed at a real server, not merely non-null.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The capture manifest, finally carrying something. Everything above tests state the
        /// serializer could write and something forgot to re-run; this tests state the serializer
        /// cannot write at all.
        ///
        /// <para>A lathe queue is a <c>[DataField]</c> whose element type has no serializer
        /// anywhere, which is the exact failure the fidelity probe exists to find. The field is
        /// captured into a sidecar, cleared so the map serializer does not choke on it, and put
        /// back on arrival. Without that the store does not merely lose the queue: the serializer
        /// aborts the whole grid, so this is also the difference between a ship that stores and one
        /// that refuses.</para>
        ///
        /// <para>The other manifest entry is market data, which needs a market to be meaningful.
        /// This covers the mechanism; that entry rides the same code path.</para>
        /// </summary>
        [Test]
        public async Task AQueuedLatheRecipeSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var lathe = entMan.SpawnEntity(LatheProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
                var recipe = protoMan.Index<LatheRecipePrototype>(LatheRecipeId);

                entMan.GetComponent<LatheComponent>(lathe).Queue.Add(
                    new LatheRecipeBatch(recipe, itemsPrinted: 1, itemsRequested: 5, actor: null));
            });

            await pair.RunTicksSync(5);

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success),
                "A populated queue must not fail the store. If the probe stopped recognising the gap this would come back SerializeFailed.");

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            var retrievedLathe = await FindChildWithComponent<LatheComponent>(pair, retrieved!.Value);
            Assert.That(retrievedLathe, Is.Not.Null, "The lathe came back with the ship.");

            await server.WaitAssertion(() =>
            {
                var queue = entMan.GetComponent<LatheComponent>(retrievedLathe!.Value).Queue;

                Assert.That(queue, Has.Count.EqualTo(1),
                    "The queue is carried by the capture sidecar, so an empty one here means it was stripped rather than captured, or never restored.");

                Assert.Multiple(() =>
                {
                    Assert.That(queue[0].Recipe.ID, Is.EqualTo(LatheRecipeId));
                    Assert.That(queue[0].ItemsRequested, Is.EqualTo(5));
                    Assert.That(queue[0].ItemsPrinted, Is.EqualTo(1),
                        "Progress through a batch is part of what a player would notice losing.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The last of the two sidecars, and the only piece of state here that does not live on an
        /// entity at all. A pipe net's air hangs off the node-group object graph, which the map
        /// serializer never visits, so it is not a serialization failure to detect: it is state
        /// attached to a structure that gets rebuilt from scratch on load. Without the sidecar a
        /// stored ship comes back with every pipe empty.
        ///
        /// <para>The restore is the odd one out too. It does not run in the Revive block; it waits
        /// for the reloaded grid's first node-group rebuild, because that is when there is a net to
        /// merge into. The sidecar's presence is the whole apply condition, and it removes itself
        /// immediately so that a player cutting a pipe later cannot re-fire the merge and duplicate
        /// the gas.</para>
        /// </summary>
        [Test]
        public async Task PipeNetGasSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var xformSys = server.System<SharedTransformSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                // Two adjacent pipes so there is a net rather than an isolated node. A pipe only
                // joins a net while anchored, and this prototype already spawns anchored onto a set
                // tile: anchoring it again trips a debug assert in the engine, because the entity
                // is already in that snap-grid cell.
                foreach (var pos in new[] { new Vector2(0.5f, 1.5f), new Vector2(1.5f, 1.5f) })
                {
                    var pipe = entMan.SpawnEntity(PipeProtoId, new EntityCoordinates(shipGrid, pos));
                    var xform = entMan.GetComponent<TransformComponent>(pipe);

                    if (!xform.Anchored)
                        xformSys.AnchorEntity(pipe);
                }
            });

            // Node groups are rebuilt on a deferred pass, so the net does not exist on the tick the
            // pipes were anchored.
            await pair.RunTicksSync(10);

            await server.WaitPost(() =>
            {
                foreach (var pipe in PipeNodesOn(entMan, shipGrid))
                    pipe.Air.AdjustMoles(Gas.Oxygen, 25f);
            });

            await pair.RunTicksSync(5);

            var molesBefore = await TotalPipeMoles(pair, shipGrid);
            Assert.That(molesBefore, Is.GreaterThan(0f),
                "The control: the pipes have to actually hold gas and be in a net, or nothing below is measuring the sidecar.");

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);

            // The merge waits for the first node-group rebuild after the load, which is later than
            // everything Revive does synchronously.
            await pair.RunTicksSync(15);

            var molesAfter = await TotalPipeMoles(pair, retrieved!.Value);

            Assert.That(molesAfter, Is.EqualTo(molesBefore).Within(0.01f),
                "Pipe gas is not on any entity, so this passes only because the sidecar carried each pipe's share and the rebuild merged it back.");

            await server.WaitAssertion(() =>
            {
                var query = entMan.AllEntityQueryEnumerator<DrydockPipeGasComponent>();
                Assert.That(query.MoveNext(out _, out _), Is.False,
                    "The sidecar removes itself on the merge. One left behind would re-merge on the next pipe a player cuts, which duplicates the gas.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The second and last entry on the capture manifest, so between this and the lathe queue
        /// the whole manifest is now exercised rather than half of it.
        ///
        /// <para>Cargo market data is the grid's own record of what it sells, which is player-built
        /// state accumulated over a round rather than anything a prototype provides. It sits on the
        /// grid itself, so unlike the lathe it needs no machine aboard.</para>
        /// </summary>
        [Test]
        public async Task CargoMarketDataSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var market = entMan.EnsureComponent<CargoMarketDataComponent>(shipGrid);

                // The component is access-locked to the market system, and this test is neither.
                // Same precedent as the other integration tests that have to seed restricted state.
#pragma warning disable RA0002
                market.MarketDataList.Add(new MarketData(MarketItemProtoId, null, quantity: 7, price: 42.5));
#pragma warning restore RA0002
            });

            await pair.RunTicksSync(5);

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<CargoMarketDataComponent>(retrieved!.Value, out var market), Is.True,
                    "The component rides the blob normally; it is the list inside it that needs carrying.");

#pragma warning disable RA0002
                var list = market!.MarketDataList;
#pragma warning restore RA0002

                Assert.That(list, Has.Count.EqualTo(1),
                    "MarketData has no serializer, so an empty list here means it was stripped rather than captured.");

                Assert.Multiple(() =>
                {
                    Assert.That(list[0].Prototype.Id, Is.EqualTo(MarketItemProtoId));
                    Assert.That(list[0].Quantity, Is.EqualTo(7));
                    Assert.That(list[0].Price, Is.EqualTo(42.5));
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The roster sweep's non-determinism, reproduced on demand. Two identical sweeps on
        /// 2026-08-26 refused different vessels, and the mechanism turned out to be sound effects:
        /// a sound played at grid coordinates is a real grid child until its despawn timer fires,
        /// but its prototype declares <c>save: false</c>, so the serializer never writes it. The
        /// validation counted it on the live side, never saw it on the scratch side, and refused
        /// the store - for whichever ship happened to have a sound in the air at that instant.
        ///
        /// <para>The sweep could only show the symptom, because whether a sound is aloft when the
        /// store runs is timing. This test plants one deliberately, which makes the refusal a
        /// certainty instead of a coin flip: before the validation learned the serializer's own
        /// exclusion, this failed every run.</para>
        /// </summary>
        [Test]
        public async Task ALiveSoundEffectDoesNotBlockTheStore()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            EntityUid sound = default;
            await server.WaitPost(() =>
            {
                // What SharedAudioSystem.SetupAudio spawns, planted as a direct grid child the way
                // a sound played at grid coordinates lands. No despawn timer rides it, so unlike
                // the real thing it is guaranteed to still be there when the store serializes.
                sound = entMan.SpawnEntity(AudioProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 1.5f)));
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<MetaDataComponent>(sound).EntityPrototype?.MapSavable, Is.False,
                    "The control: if the Audio prototype ever stops declaring save: false, this test is planting an ordinary entity and proves nothing.");
                Assert.That(entMan.GetComponent<TransformComponent>(sound).ParentUid, Is.EqualTo(shipGrid),
                    "The control: the sound has to be a direct grid child, because that is the population the validation counts.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success),
                "A sound in the air must not refuse the store. The serializer will not write it, and the validation has to count what the serializer writes, not what is live.");

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved, Is.Not.Null, "Stored with a sound aloft, then would not come back.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var children = entMan.GetComponent<TransformComponent>(retrieved!.Value).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    Assert.That(entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID, Is.Not.EqualTo(AudioProtoId),
                        "The sound is ephemera and the serializer refuses it; one aboard the retrieved ship means it rode the document after all.");
                }
            });

            await pair.CleanReturnAsync();
        }

        private static IEnumerable<PipeNode> PipeNodesOn(IEntityManager entMan, EntityUid grid)
        {
            var query = entMan.AllEntityQueryEnumerator<NodeContainerComponent, TransformComponent>();
            while (query.MoveNext(out _, out var nodeContainer, out var xform))
            {
                if (xform.GridUid != grid)
                    continue;

                foreach (var node in nodeContainer.Nodes.Values)
                {
                    if (node is PipeNode pipe)
                        yield return pipe;
                }
            }
        }

        private static async Task<float> TotalPipeMoles(TestPair pair, EntityUid grid)
        {
            var total = 0f;
            await pair.Server.WaitPost(() =>
            {
                foreach (var pipe in PipeNodesOn(pair.Server.EntMan, grid))
                    total += pipe.Air.TotalMoles;
            });
            return total;
        }

        /// <summary>
        /// A three-by-three plated grid carrying one airlock, plus a station to dock it at. The
        /// tiles are laid before anything is spawned on them: a spawn at grid-local coordinates
        /// that are not on a set tile silently reparents to the map, and a grid census then reads
        /// the wrong parent.
        /// </summary>
        private static async Task<(EntityUid Station, EntityUid ShipGrid, EntityUid Airlock)> BuildShipAndStation(TestPair pair)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var map = await pair.CreateTestMap();

            EntityUid station = default;
            EntityUid shipGrid = default;
            EntityUid airlock = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);

                // Retrieve refuses without a staging map, and nothing in a test pair creates one.
                shipyard.SetupShipyardIfNeeded();

                // The dock target. As far as the retrieve gate is concerned a station is a
                // StationData component with a grid in it, which is what GetLargestGrid reads.
                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);

                // The ship, on the same map but its own grid, so storing it cannot disturb the
                // dock target.
                var ship = mapSys.CreateGridEntity(map.MapId);
                shipGrid = ship.Owner;

                var tile = new Tile(1);
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        mapSys.SetTile(ship.Owner, ship.Comp, new Vector2i(x, y), tile);
                    }
                }

                entMan.EnsureComponent<ShuttleComponent>(shipGrid);
                entMan.System<MetaDataSystem>().SetEntityName(shipGrid, "Kestrel");

                airlock = entMan.SpawnEntity(AirlockProtoId, new EntityCoordinates(shipGrid, new Vector2(1f, 1f)));
            });

            await pair.RunTicksSync(5);

            return (station, shipGrid, airlock);
        }

        /// <summary>
        /// Starts a server-side async operation on the game thread and pumps the pair until it
        /// finishes. Both pipelines await database work, so the continuation has to come back to a
        /// ticking server; awaiting the task from the test thread alone would never let it resume.
        /// </summary>
        private static async Task<T> RunOnServer<T>(TestPair pair, Func<Task<T>> start)
        {
            Task<T>? task = null;
            await pair.Server.WaitPost(() => task = start());

            for (var i = 0; i < 600 && !task!.IsCompleted; i++)
            {
                await pair.RunTicksSync(1);
            }

            Assert.That(task!.IsCompleted, Is.True,
                "The drydock operation never completed: either it is blocked on the database, or a continuation never came back to the game thread.");

            return await task;
        }

        /// <summary>
        /// Every entity parented under the grid, counted per prototype. Recursive, because the
        /// interesting losses live inside containers rather than on the floor.
        /// </summary>
        private static async Task<Dictionary<string, int>> CensusGrid(TestPair pair, EntityUid grid)
        {
            var census = new Dictionary<string, int>();
            var entMan = pair.Server.EntMan;

            await pair.Server.WaitPost(() =>
            {
                var stack = new Stack<EntityUid>();
                stack.Push(grid);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();

                    var children = entMan.GetComponent<TransformComponent>(current).ChildEnumerator;
                    while (children.MoveNext(out var child))
                    {
                        var proto = entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID ?? "<no prototype>";
                        census[proto] = census.GetValueOrDefault(proto) + 1;
                        stack.Push(child);
                    }
                }
            });

            return census;
        }

        private static async Task<int> ReadWireCount(TestPair pair, EntityUid uid)
        {
            var count = -1;
            await pair.Server.WaitPost(() =>
            {
                count = pair.Server.EntMan.GetComponent<WiresComponent>(uid).WiresList.Count;
            });
            return count;
        }

        private static async Task<EntityUid?> FindChildWithComponent<T>(TestPair pair, EntityUid grid) where T : IComponent
        {
            EntityUid? found = null;
            var entMan = pair.Server.EntMan;

            await pair.Server.WaitPost(() =>
            {
                var children = entMan.GetComponent<TransformComponent>(grid).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    if (!entMan.HasComponent<T>(child))
                        continue;

                    found = child;
                    return;
                }
            });

            return found;
        }

        /// <summary>
        /// The owner column is a real foreign key, so a ship cannot be filed for a player who does
        /// not exist.
        /// </summary>
        private static Task InsertPlayer(IServerDbManager db, Guid userId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                context.Player.Add(new Player
                {
                    UserId = userId,
                    LastSeenUserName = $"drydock-roundtrip-{userId:N}",
                    FirstSeenTime = DateTime.UtcNow,
                    LastSeenTime = DateTime.UtcNow,
                    LastSeenAddress = IPAddress.Loopback,
                });

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }
    }
}
