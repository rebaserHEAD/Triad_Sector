#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Funkystation.Atmos.Components;
using Content.Server._Mono.FireControl;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server._NF.Market.Components;
using Content.Server.Atmos.Piping.Binary.Components;
using Content.Server.Atmos.Piping.Trinary.Components;
using Content.Server.Database;
using Content.Server.DeviceLinking.Systems;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Lathe.Components;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Wires;
using Content.Shared._Crescent.ShipShields;
using Content.Shared._Goobstation.Factory;
using Content.Shared._Mono.FireControl;
using Content.Shared._Mono.SpaceArtillery;
using Content.Shared._NF.Market;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ContrabandPermit;
using Content.Shared._Triad.Shipyard.Save.Contraband;
using Content.Shared._Triad.ShipSize;
using Content.Shared.ActionBlocker;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Lathe;
using Content.Shared.NodeContainer;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.SmartFridge;
using Content.Shared.Timing;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Content.Shared.Xenoarchaeology.Equipment.Components;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

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
        private const string SmartFridgeProtoId = "SmartFridge";
        private const string InteractorProtoId = "Interactor";
        private const string ArtifactProtoId = "ComplexXenoArtifactItem";
        private const string AudioProtoId = "Audio";
        private static readonly ProtoId<DamageTypePrototype> BluntDamage = "Blunt";
        private const string ShieldGeneratorProtoId = "ShieldGenerator";
        private const string GunneryServerProtoId = "GunneryServerUltra";
        private const string GunneryConsoleProtoId = "ComputerGunneryConsole";
        private const string TurretProtoId = "WeaponTurretFang";
        private const string ApcProtoId = "APCBasic";
        private const string PressurePumpProtoId = "GasPressurePump";
        private const string VolumePumpProtoId = "GasVolumePump";
        private const string FilterProtoId = "GasFilter";
        private const string MixerProtoId = "GasMixer";
        private const string AnalysisConsoleProtoId = "ComputerAnalysisConsole";
        private const string ArtifactAnalyzerProtoId = "MachineArtifactAnalyzer";
        private const string CrystallizerProtoId = "Crystallizer";

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
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), "The ship went in, so it has to come out.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<DrydockIdentityComponent>(retrieved.Grid!.Value, out var identity), Is.True,
                    "Identity is the one piece of state nothing else on the grid can reconstruct.");
                Assert.That(identity!.ShipId, Is.EqualTo(shipId!.Value),
                    "A retrieve must return the same hull, not a new one that looks similar.");
            });

            var after = await CensusGrid(pair, retrieved.Grid!.Value);
            Assert.That(after, Is.EqualTo(before),
                "Every prototype aboard comes back, exactly once each. A difference is a drop, a duplicate or a substitution.");

            // The map-init boundary, made concrete. This is the assertion the whole census on the
            // wiki exists to justify.
            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved.Grid!.Value);
            Assert.That(retrievedAirlock, Is.Not.Null, "The airlock came back, or the census above would have failed.");

            var wiresAfter = await ReadWireCount(pair, retrievedAirlock!.Value);
            Assert.That(wiresAfter, Is.EqualTo(wiresBefore),
                "WiresList is not a data field, so this passes only because Revive rebuilt the layout by hand.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The shipyard builds its staging map on the first purchase of a round and tears it down
        /// at round end. A retrieve in a fresh round, before anyone had bought a ship, found no map
        /// and refused with the one sentence every refusal used to share, for a ship that was
        /// stored and berthed (2026-09-05). The retrieve now asks the shipyard for the map the way a
        /// purchase does. Deleting the map between the store and the retrieve is the shape the
        /// round-end cleanup leaves when the map still existed; the null it leaves otherwise goes
        /// through the same call.
        /// </summary>
        [Test]
        public async Task ARetrieveRestagesTheShipyardAfterARoundRestart()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var shipyard = server.System<ShipyardSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var staged = shipyard.ShipyardMap;
            Assert.That(staged, Is.Not.Null, "The store ran against a staged shipyard, or this test proves nothing.");
            await server.WaitPost(() => mapSys.DeleteMap(staged!.Value));
            await pair.RunTicksSync(1);
            Assert.That(mapSys.MapExists(staged!.Value), Is.False, "Control: the staging map is gone before the retrieve.");

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));

            Assert.Multiple(() =>
            {
                Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), "A retrieve with no staging map brings the map up itself.");
                Assert.That(shipyard.ShipyardMap, Is.Not.Null);
                Assert.That(mapSys.MapExists(shipyard.ShipyardMap!.Value), Is.True, "The shipyard was re-staged, not just the ship put somewhere.");
            });

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A refused retrieve names its reason. The console used to say "it may already be out"
        /// for every one of eight refusals, including a ship sitting stored in its berth, so each
        /// state the row can be in gets its own answer here, with a success at the end as the
        /// control that the fixture itself was never the reason.
        /// </summary>
        [Test]
        public async Task ARefusedRetrieveNamesItsReason()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            var unknown = await RunOnServer(pair, () => drydock.TryRetrieveShip(Guid.NewGuid(), owner, station, null));
            Assert.That(unknown.Result, Is.EqualTo(DrydockRetrieveResult.NotFound));

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var again = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(again.Result, Is.EqualTo(DrydockRetrieveResult.AlreadyOut), "A ship that is out says so.");

            var (back, _) = await RunOnServer(pair, () => drydock.TryStoreShip(retrieved.Grid!.Value, owner, null));
            Assert.That(back, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            Assert.That(await store.TrySetState(shipId!.Value, DrydockShipState.Stored, DrydockShipState.Held, DrydockAuditAction.Hold, null, null, "test"), Is.True);
            var held = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(held.Result, Is.EqualTo(DrydockRetrieveResult.Held));

            Assert.That(await store.TrySetState(shipId!.Value, DrydockShipState.Held, DrydockShipState.Stored, DrydockAuditAction.Release, null, null, "test"), Is.True);
            Assert.That(await store.SetInvestigating(shipId!.Value, true, null, null, "test"), Is.True);
            var flagged = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(flagged.Result, Is.EqualTo(DrydockRetrieveResult.Investigating));

            Assert.That(await store.SetInvestigating(shipId!.Value, false, null, null, "test"), Is.True);
            var cleared = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(cleared.Result, Is.EqualTo(DrydockRetrieveResult.Success), "Control: with every reason cleared the same call succeeds.");

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A ship shield is derived state: the emitter raises it whenever it has power, and the
        /// grid carries a marker pointing at it. Neither may ride the document. Before this, the
        /// marker was written without its fields and reloaded pointing at nothing, the emitter's
        /// "already shielded" check then refused to raise a shield for the rest of the ship's
        /// life, and the old shield reloaded as a ghost with a hard bullet fixture and no emitter
        /// behind it (2026-09-05, first ship stored on the test server). The shield prototype now
        /// opts out of saving and both linkage components are unsaved, so this is proven on the
        /// document and on what comes back.
        /// </summary>
        [Test]
        public async Task AShieldedShipComesBackWithAFreshShield()
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
                var emitter = entMan.SpawnEntity(ShieldGeneratorProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 2.5f)));
                // A test grid has no power net; a receiver that needs no power reads as powered.
                entMan.GetComponent<ApcPowerReceiverComponent>(emitter).NeedsPower = false;
            });

            // The emitter evaluates every 1.5 s; this covers two evaluations at 30 ticks a second.
            await pair.RunTicksSync(100);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<ShipShieldedComponent>(shipGrid, out var shielded), Is.True,
                    "Control: the emitter raised a shield on the live ship.");
                Assert.That(entMan.EntityExists(shielded!.Shield), Is.True);
                Assert.That(entMan.GetComponent<TransformComponent>(shielded.Shield).ParentUid, Is.EqualTo(shipGrid),
                    "The shield is a child of the grid, which is what put it into the document before.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var document = Encoding.UTF8.GetString(Decompress(await ReadBlobs(db, shipId!.Value)));
            Assert.Multiple(() =>
            {
                Assert.That(document, Does.Contain("type: ShipShieldEmitter"), "Control: the generator itself is in the document.");
                Assert.That(document, Does.Not.Contain("proto: ShipShield\n").And.Not.Contain("proto: ShipShield\r"), "The shield entity opts out of saving.");
                Assert.That(document, Does.Not.Contain("ShipShielded"), "The grid's marker is an unsaved component.");
            });

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            var grid = retrieved.Grid!.Value;

            await server.WaitPost(() =>
            {
                foreach (var receiver in ChildrenWith<ApcPowerReceiverComponent>(entMan, grid))
                    entMan.GetComponent<ApcPowerReceiverComponent>(receiver).NeedsPower = false;
            });
            await pair.RunTicksSync(100);

            await server.WaitAssertion(() =>
            {
                var shields = ChildrenWith<ShipShieldComponent>(entMan, grid).ToList();
                Assert.That(shields, Has.Count.EqualTo(1), "Exactly one shield came up: a fresh one, no ghost.");
                var shield = shields[0];
                var newEmitter = ChildrenWith<ShipShieldEmitterComponent>(entMan, grid).Single();

                Assert.That(entMan.TryGetComponent<ShipShieldedComponent>(grid, out var marker), Is.True);
                Assert.That(marker!.Shield, Is.EqualTo(shield), "The marker points at the live shield.");
                Assert.That(entMan.GetComponent<ShipShieldComponent>(shield).Source, Is.EqualTo(newEmitter), "The shield knows its emitter.");
                Assert.That(entMan.GetComponent<ShipShieldEmitterComponent>(newEmitter).Shield, Is.EqualTo(shield), "The emitter knows its shield.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Ship guns fire only while registered with a gunnery server, and the server only links
        /// its grid, its guns and its console on a power edge. A retrieved ship arrives with the
        /// receivers unpowered and the net comes up a tick later, so the edge should fire; players
        /// reported the guns dead after a retrieve all the same (2026-09-06). This spells out every
        /// link before the store as the control and demands the same links on what comes back.
        /// </summary>
        [Test]
        public async Task AGunneryServerComesBackWithItsGunsRegistered()
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

            // Real power, not a forced flag: a basic APC ships a full battery and feeds every
            // receiver within cable range on its own, so the edge the fire-control system needs
            // comes from the power net exactly as it does aboard a ship.
            //
            // The console and the turret come up BEFORE the server, on purpose. A console registers
            // only on its own power edge, so with no server yet that edge lands on nothing; the
            // server connecting afterwards has to pick it up, or a ship whose net comes alive in one
            // tick (a purchase, a retrieve) is left with a console that says it has no server
            // whenever the order falls that way. The harness makes the losing order certain.
            await server.WaitPost(() =>
            {
                entMan.SpawnEntity(ApcProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 2.5f)));
                entMan.SpawnEntity(GunneryConsoleProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 0.5f)));
                entMan.SpawnEntity(TurretProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 2.5f)));
            });
            await pair.RunTicksSync(30);

            await server.WaitAssertion(() =>
            {
                var consoleUid = ChildrenWith<FireControlConsoleComponent>(entMan, shipGrid).Single();
                Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(consoleUid).Powered, Is.True, "Control: the console is powered before any server exists.");
                Assert.That(entMan.GetComponent<FireControlConsoleComponent>(consoleUid).ConnectedServer, Is.Null, "Control: with no server yet, the console's power edge registered it against nothing.");
            });

            await server.WaitPost(() => entMan.SpawnEntity(GunneryServerProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f))));
            await pair.RunTicksSync(30);

            await server.WaitAssertion(() => AssertGunneryLinked(entMan, shipGrid, "before the store"));
            Assert.That(await FireOnceAndCountProjectiles(pair, shipGrid, "Control, before the store"), Is.GreaterThan(0), "Control: a shot through the server spawns a projectile before the store.");

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            var grid = retrieved.Grid!.Value;

            // Nothing is touched after the retrieve: the ship has to come back armed by itself.
            await pair.RunTicksSync(60);

            await server.WaitAssertion(() => AssertGunneryLinked(entMan, grid, "after the retrieve"));

            // Each gate on the per-shot path, named, so a refusal says which one.
            await server.WaitAssertion(() =>
            {
                var fireControl = server.System<FireControlSystem>();
                var timing = server.ResolveDependency<IGameTiming>();
                var turret = ChildrenWith<FireControllableComponent>(entMan, grid).Single();
                var controllable = entMan.GetComponent<FireControllableComponent>(turret);
                var gun = entMan.GetComponent<GunComponent>(turret);
                var ammo = entMan.GetComponent<ProjectileBatteryAmmoProviderComponent>(turret);
                var battery = entMan.GetComponent<BatteryComponent>(turret);
                var xform = entMan.GetComponent<TransformComponent>(turret);
                Assert.Multiple(() =>
                {
                    Assert.That(fireControl.CanFireWeapons(grid), Is.True, "The grid-level gate (FTL, disabled marker, expedition map).");
                    Assert.That(xform.Anchored, Is.True, "The turret is anchored.");
                    Assert.That(xform.GridUid, Is.EqualTo(grid), "The turret is on the retrieved grid.");
                    Assert.That(controllable.NextFire, Is.LessThanOrEqualTo(timing.CurTime), $"Controllable cooldown {controllable.NextFire} against now {timing.CurTime}.");
                    Assert.That(gun.NextFire, Is.LessThanOrEqualTo(timing.CurTime), $"Gun cooldown {gun.NextFire} against now {timing.CurTime}.");
                    Assert.That(battery.CurrentCharge, Is.GreaterThan(0), "The turret battery has charge.");
                    Assert.That(ammo.Shots, Is.GreaterThan(0), "The battery ammo provider counts shots.");
                });
            });

            Assert.That(await FireOnceAndCountProjectiles(pair, grid, "After the retrieve"), Is.GreaterThan(0), "After the retrieve a shot through the server still spawns a projectile.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Fires the grid's one turret through its gunnery server at a point well clear of the hull,
        /// exactly as the console does, and returns how many new projectiles exist a few ticks later.
        /// </summary>
        private static async Task<int> FireOnceAndCountProjectiles(TestPair pair, EntityUid grid, string when)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var fireControl = server.System<FireControlSystem>();
            var xform = server.System<SharedTransformSystem>();

            var before = 0;
            var attempted = false;
            await server.WaitPost(() =>
            {
                before = entMan.Count<ShipWeaponProjectileComponent>();
                var turretUid = ChildrenWith<FireControllableComponent>(entMan, grid).Single();
                var mapUid = entMan.GetComponent<TransformComponent>(grid).MapUid!.Value;
                // Aim outward from the hull's corner, in whatever direction the ship happens to
                // face: the line-of-sight check refuses a shot through the ship's own machines,
                // and a retrieved ship comes back at the angle proximity placement chose.
                var turretPos = xform.GetWorldPosition(turretUid);
                var outward = Vector2.Normalize(turretPos - xform.GetWorldPosition(grid));
                var target = new EntityCoordinates(mapUid, turretPos + outward * 40f);
                // The console path minus the console: AttemptFire is what FireWeapons calls per
                // weapon, and its return says whether the gun itself was reached.
                attempted = fireControl.AttemptFire(turretUid, turretUid, target);
            });
            Assert.That(attempted, Is.True, $"{when}: AttemptFire reached the gun: power, server link, cooldown and line of sight all passed.");

            // The gun itself fires from the auto-shoot loop on the next tick, not inside
            // AttemptFire, so the state one tick later says what that loop did with it.
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var timing = server.ResolveDependency<IGameTiming>();
                var turretUid = ChildrenWith<FireControllableComponent>(entMan, grid).Single();
                var gun = entMan.GetComponent<GunComponent>(turretUid);
                var auto = entMan.GetComponent<AutoShootGunComponent>(turretUid);
                var ammo = entMan.GetComponent<ProjectileBatteryAmmoProviderComponent>(turretUid);
                var battery = entMan.GetComponent<BatteryComponent>(turretUid);
                var meta = entMan.GetComponent<MetaDataComponent>(turretUid);
                var blocker = server.System<ActionBlockerSystem>();
                Assert.Multiple(() =>
                {
                    Assert.That(meta.EntityPaused, Is.False, "The turret is not paused; the firing loop skips paused entities.");
                    Assert.That(entMan.GetComponent<MetaDataComponent>(grid).EntityPaused, Is.False, "The grid is not paused.");
                    Assert.That(auto.RemainingTime, Is.GreaterThan(TimeSpan.Zero).Or.EqualTo(TimeSpan.Zero), $"Auto-shoot remaining {auto.RemainingTime}, enabled {auto.Enabled}, on {auto.On}, can fire {auto.CanFire}, user {auto.User}.");
                    Assert.That(blocker.CanAttack(turretUid), Is.True, "The action blocker lets the turret attack.");
                    Assert.That(gun.ShootCoordinates, Is.Not.Null, "The gun holds its shoot coordinates.");
                    Assert.That(gun.FireRateModified, Is.GreaterThan(0f), $"Fire rate modified {gun.FireRateModified} against base {gun.FireRate}.");
                    Assert.That(gun.NextFire, Is.LessThan(timing.CurTime + TimeSpan.FromSeconds(1)), $"Gun NextFire {gun.NextFire} against now {timing.CurTime}: a jump of the burst cooldown means the empty-shot branch ran.");
                    Assert.That(ammo.Shots, Is.LessThan(800), $"The ammo provider gave up a shot (shots {ammo.Shots}, charge {battery.CurrentCharge}).");
                    Assert.That(entMan.Count<ShipWeaponProjectileComponent>() - before, Is.GreaterThan(0), "One tick after the shot a projectile exists.");
                });
            });
            await pair.RunTicksSync(4);

            var after = 0;
            await server.WaitPost(() => after = entMan.Count<ShipWeaponProjectileComponent>());
            return after - before;
        }

        private static void AssertGunneryLinked(IEntityManager entMan, EntityUid grid, string when)
        {
            var serverUid = ChildrenWith<FireControlServerComponent>(entMan, grid).Single();
            var consoleUid = ChildrenWith<FireControlConsoleComponent>(entMan, grid).Single();
            var turretUid = ChildrenWith<FireControllableComponent>(entMan, grid).Single();

            var serverComp = entMan.GetComponent<FireControlServerComponent>(serverUid);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(serverUid).Powered, Is.True, $"Control {when}: the server reads as powered.");
                Assert.That(serverComp.ConnectedGrid, Is.EqualTo(grid), $"{when}: the server connected to its grid.");
                Assert.That(entMan.TryGetComponent<FireControlGridComponent>(grid, out var gridComp) && gridComp.ControllingServer == serverUid, Is.True, $"{when}: the grid names the server.");
                Assert.That(serverComp.Controlled, Does.Contain(turretUid), $"{when}: the server controls the turret.");
                Assert.That(entMan.GetComponent<FireControllableComponent>(turretUid).ControllingServer, Is.EqualTo(serverUid), $"{when}: the turret knows its server.");
                Assert.That(entMan.GetComponent<FireControlConsoleComponent>(consoleUid).ConnectedServer, Is.EqualTo(serverUid), $"{when}: the console is linked to the server.");
                Assert.That(serverComp.Consoles, Does.Contain(consoleUid), $"{when}: the server lists the console.");
            });
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

            Assert.That(refused.Result, Is.EqualTo(DrydockRetrieveResult.NoReadableRevision), "A document that fails its checksum must not come back as a ship.");

            var afterRefusal = (await store.LoadCurrent(shipId.Value))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(afterRefusal.State, Is.EqualTo(DrydockShipState.Stored), "A failed retrieve releases the claim.");
                Assert.That(afterRefusal.BerthId, Is.EqualTo(berth), "A failed retrieve leaves the berth exactly as it was.");
            });

            // Mend it and bring it out for real.
            await WriteBlobs(db, shipId.Value, original);
            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
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
            var (again, sameShip) = await RunOnServer(pair, () => drydock.TryStoreShip(retrieved.Grid!.Value, owner, null));
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
            Assert.That(refused.Result, Is.EqualTo(DrydockRetrieveResult.NotOwned), "A ship that changed hands is not the previous owner's to retrieve.");

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, buyer, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var ownership = entMan.GetComponent<ShipOwnershipComponent>(retrieved.Grid!.Value);
                Assert.That(ownership.OwnerUserId.UserId, Is.EqualTo(buyer),
                    "The grid must carry the row's owner, or the console refuses the buyer's store and the seller's store files it back under them.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A ship whose grid is still in the world is not lost, and restoring its row would let it
        /// be retrieved into a second copy while the first flies. The guard is the system's, since
        /// only the entity world knows; the store cannot. Deleting the grid is what makes the same
        /// restore legitimate.
        /// </summary>
        [Test]
        public async Task AnAdminCannotRestoreAShipThatIsStillInTheWorld()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await InsertPlayer(db, admin);
            var berth = await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(drydock.IsShipLive(shipId!.Value), Is.True, "The control: the retrieved grid carries the hull's id.");
            });

            var refused = await RunOnServer(pair, () => drydock.TryAdminRestore(shipId!.Value, berth, admin, null, "player says it vanished"));
            Assert.That(refused, Is.EqualTo(DrydockBerthResult.WrongState), "A hull that is in the world cannot be restored: that would be a duplicate.");
            Assert.That((await store.LoadCurrent(shipId!.Value))!.Ship.State, Is.EqualTo(DrydockShipState.CheckedOut));

            // Now it really is gone.
            await server.WaitPost(() => entMan.DeleteEntity(retrieved.Grid!.Value));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(drydock.IsShipLive(shipId!.Value), Is.False);
            });

            var restored = await RunOnServer(pair, () => drydock.TryAdminRestore(shipId!.Value, berth, admin, null, "hull lost to a bug"));
            Assert.That(restored, Is.EqualTo(DrydockBerthResult.Success));

            var row = (await store.LoadCurrent(shipId!.Value))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(row.BerthId, Is.EqualTo(berth));
            });

            await pair.CleanReturnAsync();
        }

        private static IEnumerable<EntityUid> ChildrenWith<T>(IEntityManager entMan, EntityUid grid) where T : IComponent
        {
            var children = entMan.GetComponent<TransformComponent>(grid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                if (entMan.HasComponent<T>(child))
                    yield return child;
            }
        }

        /// <summary>The same zstd stream the pipeline writes with, so the test reads the document as filed.</summary>
        private static byte[] Decompress(byte[] blob)
        {
            using var decompress = new ZStdDecompressStream(new MemoryStream(blob));
            using var output = new MemoryStream();
            decompress.CopyTo(output);
            return output.ToArray();
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
                var blunt = protoMan.Index(BluntDamage);
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
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved.Grid!.Value);
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
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved.Grid!.Value);
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
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedLathe = await FindChildWithComponent<ResearchClientComponent>(pair, retrieved.Grid!.Value);
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

                // Mid-print. The marker has no data fields, so saved it reloaded empty, and a lathe
                // marked producing with no recipe is one the lathe loop never finishes and the
                // reboot pass never restarts (test server, 2026-09-06: lathes stuck in their running
                // animation for good). It opts out of saving now; this is the check that it stays out.
                entMan.EnsureComponent<LatheProducingComponent>(lathe);
            });

            await pair.RunTicksSync(5);

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success),
                "A populated queue must not fail the store. If the probe stopped recognising the gap this would come back SerializeFailed.");

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedLathe = await FindChildWithComponent<LatheComponent>(pair, retrieved.Grid!.Value);
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

                // Either it is not producing, or it is producing something. Never the marker alone,
                // which is the stuck state.
                var producing = entMan.HasComponent<LatheProducingComponent>(retrievedLathe.Value);
                var recipe = entMan.GetComponent<LatheComponent>(retrievedLathe.Value).CurrentRecipe;
                Assert.That(!producing || recipe != null, Is.True,
                    "A lathe must never come back marked producing with no recipe behind it: that lathe never finishes and never restarts.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Three things the first public play test (2026-09-06) found that only map init ever set up,
        /// in one round trip: a smart fridge's stock index, a robotic arm's declared hand, and the
        /// marker that stops the roundstart variation passes re-littering a ship on every retrieve.
        /// Each is a Revive step; each one missing is a machine that looks fine and does nothing.
        /// </summary>
        [Test]
        public async Task MapInitDerivedMachineStateComesBack()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var containers = server.System<SharedContainerSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var fridge = entMan.SpawnEntity(SmartFridgeProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
                var stock = entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
                var inventory = containers.GetContainer(fridge, entMan.GetComponent<SmartFridgeComponent>(fridge).Container);
                Assert.That(containers.Insert(stock, inventory), Is.True, "The control: the fridge has to hold something before the store.");

                entMan.SpawnEntity(InteractorProtoId, new EntityCoordinates(shipGrid, new Vector2(1f, 2f)));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var arm = FindChildWithComponentSync<InteractorComponent>(entMan, shipGrid);
                Assert.That(arm, Is.Not.Null);
                Assert.That(entMan.GetComponent<HandsComponent>(arm!.Value).Hands, Is.Not.Empty,
                    "The control: hand-fill gives the arm its hand on map init, so a fresh arm has one.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var grid = retrieved.Grid!.Value;
            var retrievedFridge = await FindChildWithComponent<SmartFridgeComponent>(pair, grid);
            var retrievedArm = await FindChildWithComponent<InteractorComponent>(pair, grid);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(retrievedFridge, Is.Not.Null, "The fridge came back with the ship.");
                    var entries = entMan.GetComponent<SmartFridgeComponent>(retrievedFridge!.Value).ContainedEntries;
                    Assert.That(entries.Values.Sum(v => v.Count), Is.EqualTo(1),
                        "The stock index is rebuilt only on map init, which a retrieve never fires; without the Revive step the fridge reports itself empty over a full container.");

                    Assert.That(retrievedArm, Is.Not.Null, "The arm came back with the ship.");
                    Assert.That(entMan.GetComponent<HandsComponent>(retrievedArm!.Value).Hands, Is.Not.Empty,
                        "Hands are not data fields and hand-fill only runs on map init; without the Revive step the arm has nothing to hold a tool with.");

                    Assert.That(entMan.HasComponent<StationVariationHasRunComponent>(grid), Is.True,
                        "The variation marker has to ride the grid, or the recreated station is varied again on every retrieve.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A xenoartifact is the one entity aboard whose whole structure is a NetEntity graph. The
        /// fidelity probe had no NetEntity writer, so it judged every such field unserializable and
        /// blanked it before the save: the vertex array went to null and the serializer refused the
        /// entire ship (test server, 2026-09-06: "Damascus cannot store"). The map serializer remaps
        /// NetEntity like EntityUid, so the probe must leave those fields alone; this proves the
        /// store goes through and the graph comes back pointing at real nodes.
        /// </summary>
        [Test]
        public async Task AnArtifactSurvivesTheRoundTrip()
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
                entMan.SpawnEntity(ArtifactProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
            });

            await pair.RunTicksSync(5);

            var nodesBefore = 0;
            await server.WaitAssertion(() =>
            {
                var artifact = FindChildWithComponentSync<XenoArtifactComponent>(entMan, shipGrid);
                Assert.That(artifact, Is.Not.Null);
                nodesBefore = entMan.GetComponent<XenoArtifactComponent>(artifact!.Value).NodeVertices.Count(v => v != null);
                Assert.That(nodesBefore, Is.GreaterThan(0), "The control: generation on map init has to have produced a graph to lose.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success),
                "A ship carrying an artifact must store. SerializeFailed here means the probe blanked a NetEntity field and the writer refused the null.");

            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedArtifact = await FindChildWithComponent<XenoArtifactComponent>(pair, retrieved.Grid!.Value);

            await server.WaitAssertion(() =>
            {
                Assert.That(retrievedArtifact, Is.Not.Null, "The artifact came back with the ship.");
                var comp = entMan.GetComponent<XenoArtifactComponent>(retrievedArtifact!.Value);

                Assert.That(comp.NodeVertices, Is.Not.Null);
                var resolved = comp.NodeVertices.Count(v => v != null && entMan.TryGetEntity(v.Value, out var node) && entMan.HasComponent<XenoArtifactNodeComponent>(node.Value));
                Assert.That(resolved, Is.EqualTo(nodesBefore),
                    "Every vertex has to be remapped to the reborn node entity; a stripped graph comes back empty and a stale one points at nothing.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Every pump, filter and mixer switches itself off when it leaves an atmosphere, and the
        /// engine raises a parent-changed message on every entity at startup that made the atmos
        /// device leave and rejoin the grid it had already joined on init. A loaded ship therefore
        /// came back with its distro off (test server, 2026-09-06: "turned on pump became off",
        /// "filters and pumps turn off"). The atmos device system now skips the rejoin for a device
        /// already in the atmosphere it sits in; this is the round trip that proves the switches
        /// hold, and it failed on all four before that change.
        /// </summary>
        [Test]
        public async Task AtmosSwitchesStayOnThroughTheRoundTrip()
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
                var pump = entMan.SpawnEntity(PressurePumpProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                var volumePump = entMan.SpawnEntity(VolumePumpProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 0.5f)));
                var filter = entMan.SpawnEntity(FilterProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 0.5f)));
                var mixer = entMan.SpawnEntity(MixerProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 2.5f)));

                // The switches are access-locked to their systems, whose only setters are UI
                // message handlers; the test throws them by hand.
#pragma warning disable RA0002
                entMan.GetComponent<GasPressurePumpComponent>(pump).Enabled = true;
                entMan.GetComponent<GasVolumePumpComponent>(volumePump).Enabled = true;
                entMan.GetComponent<GasFilterComponent>(filter).Enabled = true;
                entMan.GetComponent<GasMixerComponent>(mixer).Enabled = true;
#pragma warning restore RA0002
            });

            await pair.RunTicksSync(10);

            await server.WaitAssertion(() => AssertAtmosSwitches(entMan, shipGrid, "Control, before the store"));

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(10);

            await server.WaitAssertion(() => AssertAtmosSwitches(entMan, retrieved.Grid!.Value, "After the retrieve"));

            await pair.CleanReturnAsync();
        }

        private static void AssertAtmosSwitches(IEntityManager entMan, EntityUid grid, string when)
        {
            var pump = FindChildWithComponentSync<GasPressurePumpComponent>(entMan, grid);
            var volumePump = FindChildWithComponentSync<GasVolumePumpComponent>(entMan, grid);
            var filter = FindChildWithComponentSync<GasFilterComponent>(entMan, grid);
            var mixer = FindChildWithComponentSync<GasMixerComponent>(entMan, grid);
            Assert.Multiple(() =>
            {
                Assert.That(pump, Is.Not.Null, $"{when}: the pressure pump is aboard.");
                Assert.That(volumePump, Is.Not.Null, $"{when}: the volume pump is aboard.");
                Assert.That(filter, Is.Not.Null, $"{when}: the filter is aboard.");
                Assert.That(mixer, Is.Not.Null, $"{when}: the mixer is aboard.");
            });
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<GasPressurePumpComponent>(pump!.Value).Enabled, Is.True, $"{when}: the pressure pump is on.");
                Assert.That(entMan.GetComponent<GasVolumePumpComponent>(volumePump!.Value).Enabled, Is.True, $"{when}: the volume pump is on.");
                Assert.That(entMan.GetComponent<GasFilterComponent>(filter!.Value).Enabled, Is.True, $"{when}: the filter is on.");
                Assert.That(entMan.GetComponent<GasMixerComponent>(mixer!.Value).Enabled, Is.True, $"{when}: the mixer is on.");
            });
        }

        /// <summary>
        /// The analysis console holds its analyzer as a NetEntity and the analyzer holds its console
        /// as a view-variables field, so the pair is re-resolved from the device-link wire on the
        /// analyzer's map init and nowhere else. A retrieved pair came back linked on the wire and
        /// dead on the console (test server, 2026-09-06: "linked, but not working"; "analyzer still
        /// borked" on the next build). Both ends are asserted after the round trip.
        /// </summary>
        [Test]
        public async Task AnAnalysisConsoleStaysLinkedToItsAnalyzer()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var deviceLink = server.System<DeviceLinkSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var console = entMan.SpawnEntity(AnalysisConsoleProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                var analyzer = entMan.SpawnEntity(ArtifactAnalyzerProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 0.5f)));
                deviceLink.LinkDefaults(null, console, analyzer);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() => AssertAnalyzerLinked(entMan, shipGrid, "Control, before the store"));

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() => AssertAnalyzerLinked(entMan, retrieved.Grid!.Value, "After the retrieve"));

            await pair.CleanReturnAsync();
        }

        private static void AssertAnalyzerLinked(IEntityManager entMan, EntityUid grid, string when)
        {
            var console = FindChildWithComponentSync<AnalysisConsoleComponent>(entMan, grid);
            var analyzer = FindChildWithComponentSync<ArtifactAnalyzerComponent>(entMan, grid);
            Assert.That(console, Is.Not.Null, $"{when}: the console is aboard.");
            Assert.That(analyzer, Is.Not.Null, $"{when}: the analyzer is aboard.");

            var consoleComp = entMan.GetComponent<AnalysisConsoleComponent>(console!.Value);
            var analyzerComp = entMan.GetComponent<ArtifactAnalyzerComponent>(analyzer!.Value);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetEntity(consoleComp.AnalyzerEntity), Is.EqualTo(analyzer.Value), $"{when}: the console names the analyzer.");
                Assert.That(analyzerComp.Console, Is.EqualTo(console.Value), $"{when}: the analyzer names the console.");
            });
        }

        /// <summary>
        /// A use delay's end is an absolute game time. Written in one round and read in the next,
        /// where the clock started over, a half-second delay reads as hours (test server,
        /// 2026-09-06: "I cannot press E to open inventories" on anything that was aboard). The
        /// ship-load path re-arms every delay on load; retrieve does the same. A far-future end
        /// stands in for the previous round's larger clock.
        /// </summary>
        [Test]
        public async Task AStaleUseDelayIsRearmedOnRetrieve()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var useDelay = server.System<UseDelaySystem>();
            var timing = server.ResolveDependency<IGameTiming>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            var length = TimeSpan.FromSeconds(1);
            await server.WaitPost(() =>
            {
                var item = entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 1.5f)));
                useDelay.SetLength(item, length);
                var comp = entMan.GetComponent<UseDelayComponent>(item);
                // The component is access-locked to its system; the stale end has to be planted by hand.
#pragma warning disable RA0002
                var entry = comp.Delays.Values.Single();
                entry.StartTime = timing.CurTime;
                entry.EndTime = timing.CurTime + TimeSpan.FromHours(100);
#pragma warning restore RA0002
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var item = FindChildWithComponentSync<UseDelayComponent>(entMan, shipGrid);
                Assert.That(item, Is.Not.Null);
                Assert.That(useDelay.IsDelayed(item!.Value), Is.True, "The control: the planted end reads as an active delay.");
            });

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var item = FindChildWithComponentSync<UseDelayComponent>(entMan, retrieved.Grid!.Value);
                Assert.That(item, Is.Not.Null, "The item came back with the ship.");
#pragma warning disable RA0002
                var entry = entMan.GetComponent<UseDelayComponent>(item!.Value).Delays.Values.Single();
#pragma warning restore RA0002
                Assert.That(entry.EndTime, Is.LessThanOrEqualTo(timing.CurTime + length),
                    "A retrieved delay ends no later than one full length from now; the stale end from the store would still be hours out.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The ship-save path deletes anything marked as saving contraband unless it carries a
        /// contraband permit. The drydock kept everything, so ID cards and modular grenades rode
        /// along (test server, 2026-09-06: "ID CARDs save!! That is probably bad"). The store now
        /// purges by the same component rule; the permit exception and an ordinary item are the
        /// controls that the purge takes only what it should.
        /// </summary>
        [Test]
        public async Task SavingContrabandIsPurgedAtStoreUnlessPermitted()
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
                var contraband = entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                entMan.EnsureComponent<SavingContrabandComponent>(contraband);

                var permitted = entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 0.5f)));
                entMan.EnsureComponent<SavingContrabandComponent>(permitted);
                entMan.EnsureComponent<ContrabandPermitItemComponent>(permitted);

                entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 0.5f)));
            });

            await pair.RunTicksSync(5);

            var before = await CensusGrid(pair, shipGrid);
            Assert.That(before[MarketItemProtoId], Is.EqualTo(3), "The control: three sheets aboard before the store.");

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var after = await CensusGrid(pair, retrieved.Grid!.Value);
            Assert.That(after.GetValueOrDefault(MarketItemProtoId), Is.EqualTo(2), "The unpermitted contraband is gone; the permitted one and the plain sheet are not.");

            await server.WaitAssertion(() =>
            {
                var query = entMan.AllEntityQueryEnumerator<SavingContrabandComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out _, out var xform))
                {
                    if (xform.GridUid != retrieved.Grid!.Value)
                        continue;

                    Assert.That(entMan.HasComponent<ContrabandPermitItemComponent>(uid), Is.True,
                        "Every piece of saving contraband that came back carries a permit.");
                }
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A crystallizer's recipe and gas input were view-variables fields, so a retrieved one came
        /// back with no recipe and no input, and the regulator loop then ran against a reset machine
        /// (test server, 2026-09-06: "crystallizers reset their settings and superheat their inlet").
        /// Both are data fields now.
        /// </summary>
        [Test]
        public async Task ACrystallizerKeepsItsSettings()
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
                var crystallizer = entMan.SpawnEntity(CrystallizerProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                var comp = entMan.GetComponent<CrystallizerComponent>(crystallizer);
                comp.SelectedRecipeId = "roundtrip-recipe";
                comp.GasInput = 12.5f;
            });

            await pair.RunTicksSync(5);

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var crystallizer = FindChildWithComponentSync<CrystallizerComponent>(entMan, retrieved.Grid!.Value);
                Assert.That(crystallizer, Is.Not.Null, "The crystallizer came back with the ship.");
                var comp = entMan.GetComponent<CrystallizerComponent>(crystallizer!.Value);
                Assert.Multiple(() =>
                {
                    Assert.That(comp.SelectedRecipeId, Is.EqualTo("roundtrip-recipe"));
                    Assert.That(comp.GasInput, Is.EqualTo(12.5f));
                });
            });

            await pair.CleanReturnAsync();
        }

        private static EntityUid? FindChildWithComponentSync<T>(IEntityManager entMan, EntityUid grid) where T : IComponent
        {
            var query = entMan.AllEntityQueryEnumerator<T, TransformComponent>();
            while (query.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.GridUid == grid)
                    return uid;
            }

            return null;
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
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            // The merge waits for the first node-group rebuild after the load, which is later than
            // everything Revive does synchronously.
            await pair.RunTicksSync(15);

            var molesAfter = await TotalPipeMoles(pair, retrieved.Grid!.Value);

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
        /// A pump has an inlet node in one net and an outlet node in another. The first sidecar held
        /// one mixture per entity, so whichever net was written last won and the restore merged it
        /// into both nodes: gas crossed the pump, a mixer's two feeds leaked into each other, and a
        /// crystallizer's inlet dumped into its regulator loop (test server, 2026-09-06). A lone
        /// pump is the smallest device with two nets; its two nodes must come back holding exactly
        /// what each held, and nothing of the other.
        /// </summary>
        [Test]
        public async Task ATwoPortDeviceKeepsEachNetsGasSeparate()
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

            await server.WaitPost(() => entMan.SpawnEntity(PressurePumpProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 1.5f))));
            await pair.RunTicksSync(10);

            await server.WaitPost(() =>
            {
                var (inlet, outlet) = PumpNodes(entMan, shipGrid);
                inlet.Air.AdjustMoles(Gas.Oxygen, 30f);
                outlet.Air.AdjustMoles(Gas.Nitrogen, 20f);
            });
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() => AssertPumpGases(entMan, shipGrid, "Control, before the store"));

            var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(15);

            await server.WaitAssertion(() => AssertPumpGases(entMan, retrieved.Grid!.Value, "After the retrieve"));

            await pair.CleanReturnAsync();
        }

        private static (PipeNode Inlet, PipeNode Outlet) PumpNodes(IEntityManager entMan, EntityUid grid)
        {
            var pump = FindChildWithComponentSync<GasPressurePumpComponent>(entMan, grid);
            Assert.That(pump, Is.Not.Null, "The pump is aboard.");
            var comp = entMan.GetComponent<GasPressurePumpComponent>(pump!.Value);
            var nodes = entMan.GetComponent<NodeContainerComponent>(pump.Value).Nodes;
            return ((PipeNode)nodes[comp.InletName], (PipeNode)nodes[comp.OutletName]);
        }

        private static void AssertPumpGases(IEntityManager entMan, EntityUid grid, string when)
        {
            var (inlet, outlet) = PumpNodes(entMan, grid);
            Assert.Multiple(() =>
            {
                Assert.That(inlet.Air.GetMoles(Gas.Oxygen), Is.EqualTo(30f).Within(0.01f), $"{when}: the inlet holds its oxygen.");
                Assert.That(inlet.Air.GetMoles(Gas.Nitrogen), Is.EqualTo(0f).Within(0.01f), $"{when}: none of the outlet's nitrogen crossed into the inlet.");
                Assert.That(outlet.Air.GetMoles(Gas.Nitrogen), Is.EqualTo(20f).Within(0.01f), $"{when}: the outlet holds its nitrogen.");
                Assert.That(outlet.Air.GetMoles(Gas.Oxygen), Is.EqualTo(0f).Within(0.01f), $"{when}: none of the inlet's oxygen crossed into the outlet.");
            });
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
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<CargoMarketDataComponent>(retrieved.Grid!.Value, out var market), Is.True,
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
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), "Stored with a sound aloft, then would not come back.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var children = entMan.GetComponent<TransformComponent>(retrieved.Grid!.Value).ChildEnumerator;
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
