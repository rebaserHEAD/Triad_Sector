#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server.Atmos;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Power.Components;
using Content.Server.Station.Components;
using Content.Server.Storage.Components;
using Content.Server.Storage.EntitySystems;
using Content.Server.VendingMachines;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Damage;
using Content.Shared.Atmos;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Lathe;
using Content.Shared.Power;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Content.Shared.VendingMachines;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Wires;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The fidelity ladder: one storable vessel per case, smallest hull first, compared with
    /// <see cref="DrydockFidelitySystem.DeepSnapshotGrid"/> across two round trips. It reports and
    /// does not assert on findings; the assertions are that both round trips completed and that the
    /// comparison saw something.
    ///
    /// <para>Before round trip 1 the hull is put into lived-in states (<see cref="ApplyLivedIn"/>), so the
    /// comparison covers damage, cargo, open doors and panels, queues and fired guns, not only a hull
    /// as its file describes it. Once atmos has settled, rooms are vented, pressurized and heated
    /// (<see cref="ApplyLivedInAtmos"/>).</para>
    ///
    /// <para>Members the detector cannot render at all are listed under "uncapturable"
    /// (<see cref="AppendUncapturable"/>): their round trip is unmeasured, not clean.</para>
    ///
    /// <list type="bullet">
    /// <item><b>Round trip 1</b> starts from the shuttle file. Its findings are state a first store
    /// loses. What the retrieve grants a hull on purpose (<see cref="RetrieveGrants"/>) is reported
    /// apart.</item>
    /// <item><b>Round trip 2</b> starts from the ship round trip 1 returned. A ship that has been
    /// through the drydock once must come back identical every time after, so every finding here is
    /// the drydock changing a ship it already produced.</item>
    /// </list>
    ///
    /// <para>Each round trip snapshots the live ship twice, <see cref="LiveWindowTicks"/> apart, before
    /// storing. A key that changed between those two is live simulation (power ramps, heat, atmos
    /// timers) and its round-trip difference is reported under "live" rather than as a finding.</para>
    ///
    /// <para>What remains is sorted against loose entities that settled onto a neighbouring tile ("moved"),
    /// <see cref="Registry"/> ("classified", or "below-floor" when only <see cref="LiveFloor"/> admits it), removals by rule: contraband, the mech strip, emptied AI cores,
    /// and the containers and components those removals reach (<see cref="PolicyConsequences"/>)
    /// ("policy"), <c>save: false</c> entities the store deleted ("unsaved", unless
    /// <see cref="TransientUnsaved"/> expects the loss), and the late snapshot below; only unexplained,
    /// unrecovered lines are findings.</para>
    ///
    /// <para>A third snapshot <see cref="LateSeconds"/> after the retrieve sorts each remaining
    /// difference: back at its stored value by then is "settled", a number at least halfway back is
    /// "settling", and only one still wrong is a "finding". Set <c>LADDER_DUMP</c> to a directory to
    /// get the full values of every finding and settling line.</para>
    ///
    /// <para>The clock advances <see cref="ClockGapSeconds"/> between store and retrieve, so an
    /// absolute time that is not re-based comes back off by at least that much.</para>
    ///
    /// <para>Set <c>LADDER_MODE=engine</c> to round-trip through the engine serializer instead of the drydock
    /// (<see cref="EngineMode"/>).</para>
    ///
    /// <para>Rungs are ordered by the <c>entityCount</c> in each vessel's shuttle file and exclude
    /// vessels whose prototype chain grants <c>ShipSavingBlacklist</c>.</para>
    ///
    /// <para>Run: <c>dotnet test Content.IntegrationTests --no-build --filter "FullyQualifiedName~DrydockFidelityLadderLocalTest&amp;Name~Rung001_" --logger "console;verbosity=detailed"</c>.
    /// <c>TestContext.Out</c> is hidden for a passing test without the detailed logger. Lines prefixed
    /// <c>[ladder-kind]</c> are one finding kind each, for aggregating rungs.</para>
    /// </summary>
    [TestFixture]
    [Explicit("Exploratory fidelity ladder. Run rungs deliberately and read their reports.")]
    [TestOf(typeof(DrydockFidelitySystem))]
    public sealed class DrydockFidelityLadderLocalTest
    {
        /// <summary>Long enough for a capital hull's power to come up from its file before the first snapshot.</summary>
        private const int PreSettleTicks = 600;
        private const int SettleTicks = 10;
        private const int LiveWindowTicks = 90;
        private const double ClockGapSeconds = 10;
        private const double TimeToleranceSeconds = 1;
        private const double LateSeconds = 10;
        private const double AtmosSettleSeconds = 5;

        /// <summary>The smallest sealed room a gas recipe takes, so a door frame or a window bay is not a room.</summary>
        private const int MinRoomTiles = 4;

        /// <summary>
        /// <c>LADDER_MODE=engine</c> round-trips through the engine serializer alone (<see cref="EngineRoundTrip"/>)
        /// instead of the drydock, which separates what the engine loses from what the drydock's own steps change.
        /// Nothing is granted on the way back, so no line is sorted under "grant".
        /// </summary>
        private static readonly bool EngineMode = Environment.GetEnvironmentVariable("LADDER_MODE") == "engine";

        /// <summary>
        /// Components the retrieve adds or rewrites on purpose because a purchase would have: the
        /// station join, ownership, repair baseline and console locks. Keyed <c>Component</c>,
        /// <c>Component.member</c>, or <c>Prototype:Component</c> for a grant only one prototype gets
        /// (<c>Prefix*:Component</c> for a family of prototypes).
        /// Round trip 1 only; on round trip 2 the ship already carries them and any difference is a finding.
        /// </summary>
        private static readonly HashSet<string> RetrieveGrants = new(StringComparer.Ordinal)
        {
            "NavMapComponent",
            "ShipOwnershipComponent",
            "ShipRepairDataComponent",
            "StationMemberComponent",
            "StationVariationHasRunComponent",
            "FaxMachineComponent.FaxName",
            "WarpPointComponent.Location",
            "ShipGridLockComponent",
            "ShuttleConsoleLockComponent",
            "ShipActivityComponent",
            "NFHolopadShip*:LabelComponent",
            "NFHolopadShip*:NameModifierComponent",
            "VesselInfoComponent",
            "JointComponent",
            "grid:MetaDataComponent",
        };

        private enum StateClass
        {
            /// <summary>Simulation state that moves on its own. Accepted while it is settling, settled or within
            /// <see cref="LiveTolerance"/> of its stored value; a jump past that stays a finding.</summary>
            Live,

            /// <summary>Runtime bookkeeping with no meaning across a reload (cursors, in-flight jobs).</summary>
            Volatile,

            /// <summary>Rebuilt from other state after the load. <see cref="TotalChecked"/> members are also
            /// compared as a sum over the grid.</summary>
            Derived,
        }

        private const double LiveTolerance = 0.05;

        /// <summary>
        /// The smallest difference a Live value may show beyond <see cref="LiveTolerance"/>: the resolution tile gas
        /// renders at (moles to two decimals), so a trace amount that only crossed a rounding boundary is not a finding.
        /// A line only this floor admits is reported under "below-floor", never as classified.
        /// </summary>
        private const double LiveFloor = 0.01;

        /// <summary>
        /// The ladder's classification registry, keyed <c>Component.member</c> or <c>Component.*</c>. Every
        /// entry is an explained difference; anything not here is a finding until explained. This is the seed
        /// of the census registry the grid image design calls for.
        /// </summary>
        private static readonly Dictionary<string, (StateClass Class, string Reason)> Registry = new(StringComparer.Ordinal)
        {
            ["HTNComponent.~PlanAccumulator"] = (StateClass.Volatile, "NPC planner tick accumulator"),
            ["HTNComponent.~PlanningJob"] = (StateClass.Volatile, "in-flight planner job"),
            ["HTNComponent.~PlanningToken"] = (StateClass.Volatile, "in-flight planner cancellation token"),
            ["GridAtmosphereComponent.~EqualizationQueueCycleControl"] = (StateClass.Volatile, "atmos processing cursor"),
            ["UserInterfaceComponent.~States"] = (StateClass.Derived, "BUI state cache, rebuilt on the next UI update"),
            ["ApcComponent.~LastChargeState"] = (StateClass.Derived, "APC visual and UI update throttle cache"),
            ["ApcComponent.~LastExternalState"] = (StateClass.Derived, "APC visual and UI update throttle cache"),
            ["ApcComponent.~LastChargeStateTime"] = (StateClass.Derived, "APC visual and UI update throttle cache"),
            ["ApcPowerProviderComponent.~LinkedReceivers"] = (StateClass.Derived, "receiver pairing is reassigned on reconnect"),
            ["ExtensionCableProviderComponent.~LinkedReceivers"] = (StateClass.Derived, "receiver pairing is reassigned on reconnect"),
            ["ExtensionCableReceiverComponent.~Provider"] = (StateClass.Derived, "receiver pairing is reassigned on reconnect"),
            ["ApcPowerReceiverComponent.~Provider"] = (StateClass.Derived, "receiver pairing is reassigned on reconnect"),
            ["ThermalSignatureComponent.*"] = (StateClass.Live, "heat signature accumulator"),
            ["BatteryComponent.CurrentCharge"] = (StateClass.Live, "battery charge under load"),
            ["PowerNetworkBatteryComponent.*"] = (StateClass.Live, "battery supply and ramp under load"),
            ["PowerSupplierComponent.SupplyRampPosition"] = (StateClass.Live, "generator supply ramp"),
            ["HTNComponent.CheckServices"] = (StateClass.Volatile, "planner flag toggled every planning cycle (HTNSystem)"),
            ["PressurizedSolutionComponent.SprayFizzinessThresholdRoll"] = (StateClass.Volatile, "RNG threshold pre-rolled for prediction; a re-roll keeps the odds"),
            ["ShipActivityComponent.*"] = (StateClass.Volatile, "inactivity counters, re-measured every check interval (LimitedShuttleSystem.Update)"),
            ["WiresPanelComponent.~Visible"] = (StateClass.Derived, "panel sprite visibility; appearance only"),
            ["SmesComponent.~LastChargeState"] = (StateClass.Derived, "SMES visual and UI update throttle cache"),
            ["HandheldLightComponent.~Level"] = (StateClass.Derived, "light level appearance cache"),
            ["GridPathfindingComponent.~Chunks"] = (StateClass.Derived, "pathfinding graph, rebuilt after load"),
            ["GasCanisterComponent.~LastPressure"] = (StateClass.Derived, "canister UI and appearance cache"),
            ["Appearance.ApcVisuals.ChargeState"] = (StateClass.Derived, "follows the APC battery, which the retrieve brownout drains"),
            ["SmesComponent.~LastChargeLevel"] = (StateClass.Derived, "SMES visual and UI update throttle cache"),
            ["PowerNetworkBatteryComponent.~NetworkBattery"] = (StateClass.Derived, "the power solver's battery record, rebuilt with the net"),
            ["PowerSupplierComponent.~NetworkSupply"] = (StateClass.Derived, "the power solver's supplier record, rebuilt with the net"),
            ["RadiationReceiverComponent.~CurrentRadiation"] = (StateClass.Volatile, "radiation reading, recomputed every radiation update"),
            ["StorageComponent.~OccupiedGrid"] = (StateClass.Derived, "grid inventory occupancy, rebuilt from the stored items"),
            ["TimedDespawnComponent.Lifetime"] = (StateClass.Live, "countdown to despawn"),
            ["GunComponent.~ShootCoordinates"] = (StateClass.Volatile, "the last shot's aim point"),
            ["AppearanceComponent.~AppearanceData"] = (StateClass.Derived, "the same data the Appearance.* keys compare"),
            ["PowerChargeComponent.~NeedUIUpdate"] = (StateClass.Volatile, "UI refresh flag, cleared only when an open UI updates (PowerChargeSystem.UpdateUI)"),
            ["PipeNetAir.*"] = (StateClass.Live, "pipe-net gas moves while pumps, vents and mixers run"),
            ["GridAtmosphereComponent.Tiles.moles"] = (StateClass.Live, "deck gas moves while atmos processes active tiles"),
            ["GridAtmosphereComponent.Tiles.temperature"] = (StateClass.Live, "deck gas temperature moves while atmos processes active tiles"),
        };

        /// <summary>Derived members whose per-entity values may reshuffle but whose sum over the grid may not.</summary>
        private static readonly HashSet<string> TotalChecked = new(StringComparer.Ordinal)
        {
            "ApcPowerProviderComponent.~LinkedReceivers",
            "ExtensionCableProviderComponent.~LinkedReceivers",
        };

        private static readonly string[] Ladder =
        {
            "Framework", "Taser", "Ample", "Veska", "Antlion", "Rook", "Tick", "Hound", "Mock", "Marshrutka",
            "QJ490", "Zephyr", "Sleipnir", "QJ270", "Bucket", "Snakelet", "Ugol", "Inertia", "Horsefly", "StratoRat",
            "QJ340", "Triage", "Betelgeuse", "Kupol", "Borer", "Piva", "Notch", "Strayk", "Svinya", "Plutomkii",
            "Stubby", "Kopye", "Vaquita", "Talon", "Solarsail", "Hunter", "Victoria", "Sellsword", "Tiger", "Hermes",
            "Spessbite", "Akula", "Mite", "Iapetus", "Molecule", "Artemis", "Duran", "Molotok", "Natisk", "Canister",
            "Gruznyk", "Twilight", "caracara", "Sekunda", "Ruchka", "Velios", "Drakon", "Praetorian", "Picses", "Spyglass",
            "Peregrine", "Mudskipper", "Femur", "Sakuratsu", "Tayfun", "Kestrel", "Goida", "Magus", "Judiciary", "Sirius",
            "Tanuki", "Kunai", "Odenta", "Povest", "twilightmk2", "Erudite", "Brute", "Valkyrie", "Argent", "Phaeron",
            "Pillbox", "Scallywag", "Sulak", "Littora", "Leaf", "Sagittarius", "Kuldi", "Rig", "Tzipora", "Medicus",
            "Siebel", "Palisade", "Takeaway", "Remontnik", "Infiltrator", "Padval", "Arribane", "Comet", "Tokarev", "Behir",
            "Promise", "Hammerhead", "Zeros", "Autumn", "Horizon", "Bratan", "Windreign", "Buran", "Ledokol", "Prospector",
            "Arkansaw",
        };

        private static IEnumerable<TestCaseData> Rungs() =>
            Ladder.Select((id, i) => new TestCaseData(i + 1, id).SetName($"Rung{i + 1:000}_{id}"));

        [TestCaseSource(nameof(Rungs))]
        public async Task Rung(int rung, string vesselId)
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapLoader = server.System<MapLoaderSystem>();

            var (owner, station) = await GoldenCorpus.PrepareHarness(pair, 3);
            var map = await pair.CreateTestMap();

            // A sector map carries a space atmosphere; a test map does not, and a firelock on a hull presented
            // beside the harness station asks its map for one and logs an error when it is missing.
            await server.WaitPost(() =>
            {
                var atmos = server.System<AtmosphereSystem>();
                var stationGrid = entMan.GetComponent<StationDataComponent>(station).Grids.First();
                foreach (var mapUid in new[] { map.MapUid, entMan.GetComponent<TransformComponent>(stationGrid).MapUid!.Value })
                {
                    if (!entMan.HasComponent<MapAtmosphereComponent>(mapUid))
                        atmos.SetMapAtmosphere(mapUid, space: true, new GasMixture());
                }
            });

            Assert.That(protoMan.TryIndex<VesselPrototype>(vesselId, out var vessel), Is.True,
                $"Rung {rung}: vessel {vesselId} no longer exists; regenerate the ladder.");

            EntityUid grid = default;
            await server.WaitPost(() =>
            {
                Assert.That(mapLoader.TryLoadGrid(map.MapId, vessel!.ShuttlePath, out var loaded), Is.True,
                    $"{vesselId}: its shuttle file would not load.");
                grid = loaded!.Value.Owner;

                // What a purchase gives a hull that its file does not carry, as the golden corpus does.
                entMan.AddComponents(grid, vessel.AddComponents);
                entMan.EnsureComponent<VesselComponent>(grid).VesselId = vessel.ID;
            });

            var recipes = new List<string>();
            await server.WaitPost(() => recipes = ApplyLivedIn(pair, grid));

            await pair.RunTicksSync(PreSettleTicks);

            // Rooms are the tiles air flows between, which atmos sets (TileAtmosphere.AdjacentBits) only once it has
            // processed the hull, so the gas recipes wait for the settle and then settle on their own.
            var gasRooms = new List<(string Recipe, List<Vector2i> Tiles)>();
            await server.WaitPost(() => gasRooms = ApplyLivedInAtmos(pair, grid));
            recipes.AddRange(gasRooms.Select(room => room.Recipe));
            await pair.RunTicksSync((int) Math.Ceiling(AtmosSettleSeconds / server.ResolveDependency<IGameTiming>().TickPeriod.TotalSeconds));

            var first = await RoundTrip(pair, grid, owner, station);
            var second = await RoundTrip(pair, first.Retrieved, owner, station);

            var sb = new StringBuilder();
            sb.AppendLine($"[ladder] rung {rung} {vesselId} through {(EngineMode ? "the engine serializer" : "the drydock")}");
            sb.AppendLine($"[ladder] lived-in recipes applied: {(recipes.Count == 0 ? "none" : string.Join(", ", recipes))}");
            Report(sb, rung, vesselId, 1, first, EngineMode ? null : RetrieveGrants, gasRooms, protoMan);
            Report(sb, rung, vesselId, 2, second, null, gasRooms, protoMan);
            await TestContext.Out.WriteLineAsync(sb.ToString());

            Assert.That(first.Before.Entities, Is.GreaterThan(0), "The control: round trip 1 compared no entities.");
            Assert.That(second.Before.Entities, Is.GreaterThan(0), "The control: round trip 2 compared no entities.");

            await server.WaitPost(() => entMan.DeleteEntity(second.Retrieved));
            await pair.RunTicksSync(3);
            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Puts the hull into states a lived-in ship has and a shuttle file does not: damage on a wall and
        /// a thruster, cargo in a closed locker, a loose item and a wheelchair (a <c>save: false</c> vehicle) on
        /// the deck, an interior door open, a wires panel open, a gravity generator switched off, a lathe queue
        /// (not in engine mode, which cannot write one), a restocked vendor, and a sidearm fired. Each recipe takes the first
        /// matching entity in tree order (the deck spot is a chair, else a computer) and is skipped when
        /// the hull has none. Returns the ones applied.
        /// </summary>
        private static List<string> ApplyLivedIn(TestPair pair, EntityUid grid)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var tree = server.System<DrydockFidelitySystem>().GridTreeList(grid);
            var applied = new List<string>();

            EntityUid? First(Func<EntityUid, string, bool> match)
            {
                foreach (var uid in tree)
                {
                    if (uid != grid && entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID is { } id && match(uid, id))
                        return uid;
                }

                return null;
            }

            bool Anchored(EntityUid uid) => entMan.GetComponent<TransformComponent>(uid).Anchored;

            if (First((uid, id) => id.StartsWith("Wall", StringComparison.Ordinal) && Anchored(uid)
                                   && entMan.HasComponent<DamageableComponent>(uid)) is { } wall)
            {
                server.System<DamageableSystem>().TryChangeDamage(wall,
                    new DamageSpecifier(protoMan.Index<DamageTypePrototype>("Blunt"), FixedPoint2.New(37)), ignoreResistances: true);
                applied.Add("damage-wall");
            }

            if (First((uid, id) => id.StartsWith("Thruster", StringComparison.Ordinal) && Anchored(uid)
                                   && entMan.HasComponent<DamageableComponent>(uid)) is { } thruster)
            {
                server.System<DamageableSystem>().TryChangeDamage(thruster,
                    new DamageSpecifier(protoMan.Index<DamageTypePrototype>("Blunt"), FixedPoint2.New(20)), ignoreResistances: true);
                applied.Add("damage-thruster");
            }

            if (First((uid, id) => id.Contains("Locker", StringComparison.Ordinal)
                                   && entMan.TryGetComponent<EntityStorageComponent>(uid, out var storage)
                                   && !storage.Open
                                   && storage.Contents.ContainedEntities.All(e =>
                                       entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID != "SheetSteel")) is { } locker)
            {
                var cargo = entMan.SpawnEntity("SheetSteel", entMan.GetComponent<TransformComponent>(locker).Coordinates);
                server.System<SharedStackSystem>().SetCount(cargo, 17);
                if (server.System<EntityStorageSystem>().Insert(cargo, locker))
                    applied.Add("cargo-into-locker");
                else
                    entMan.DeleteEntity(cargo);
            }

            var chair = First((_, id) => id.StartsWith("Chair", StringComparison.Ordinal))
                        ?? First((_, id) => id.StartsWith("Computer", StringComparison.Ordinal));
            if (chair is { } floorSpot)
            {
                var spot = entMan.GetComponent<TransformComponent>(floorSpot).Coordinates;
                entMan.SpawnEntity("Crowbar", spot);
                applied.Add("loose-item");

                // save: false player property: the store deletes it, and the report must say so every time.
                entMan.SpawnEntity("VehicleWheelchair", spot);
                applied.Add("unsaved-vehicle");
            }

            var door = First((uid, id) => id.StartsWith("Airlock", StringComparison.Ordinal)
                                          && !id.Contains("Shuttle", StringComparison.Ordinal)
                                          && !id.Contains("External", StringComparison.Ordinal)
                                          && entMan.HasComponent<DoorComponent>(uid));
            if (door is { } interior)
            {
                // TryOpen refuses an unpowered or bolted door; forcing the open is still a state a door can be stored in.
                var doors = server.System<SharedDoorSystem>();
                if (!doors.TryOpen(interior))
                    doors.StartOpening(interior);
                applied.Add("open-door");
            }

            if (First((uid, _) => uid != door && entMan.HasComponent<WiresPanelComponent>(uid)) is { } paneled
                && server.System<SharedWiresSystem>().TogglePanel(paneled, entMan.GetComponent<WiresPanelComponent>(paneled), true))
            {
                applied.Add("open-panel");
            }

            if (First((uid, id) => id.StartsWith("GravityGenerator", StringComparison.Ordinal)
                                   && entMan.HasComponent<PowerChargeComponent>(uid)) is { } gravity)
            {
                // The console's switch message: the only public path to a charged machine's on switch.
                entMan.EventBus.RaiseLocalEvent(gravity, new SwitchChargingMachineMessage(false));
                applied.Add("gravity-off");
            }

            // The engine serializer has no writer for LatheRecipeBatch (DrydockSerializationGap.CapturedTypes), so a
            // queued job fails the whole grid save and an engine-mode rung would measure nothing.
            if (!EngineMode && First((uid, _) => entMan.HasComponent<LatheComponent>(uid)) is { } lathe)
            {
                var batch = new LatheRecipeBatch(protoMan.Index<LatheRecipePrototype>("SheetSteel"), itemsPrinted: 1, itemsRequested: 5, actor: null);
                entMan.GetComponent<LatheComponent>(lathe).Queue.Add(batch);
                applied.Add("queue-lathe");
            }

            if (First((uid, _) => entMan.TryGetComponent<VendingMachineComponent>(uid, out var vend)
                                  && vend.Inventory.Values.Any(e => e.Amount is > 0 and < uint.MaxValue)) is { } vendor)
            {
                server.System<VendingMachineSystem>().RestockInventoryFromPrototype(vendor, restockQuality: 1f);
                applied.Add("restock-vendor");
            }

            if (chair is { } seat)
            {
                var pistol = entMan.SpawnEntity("WeaponPistolMk58", entMan.GetComponent<TransformComponent>(seat).Coordinates);
                var chamber = entMan.GetComponent<ChamberMagazineAmmoProviderComponent>(pistol);
                if (chamber.BoltClosed != true)
                    server.System<GunSystem>().SetBoltClosed(pistol, chamber, true);

                server.System<GunSystem>().AttemptShoot(pistol, entMan.GetComponent<GunComponent>(pistol));
                applied.Add("fire-gun");
            }

            return applied;
        }

        /// <summary>
        /// Puts the deck's gas into states a lived-in ship has and a shuttle file does not: a sealed room vented to
        /// vacuum, one pressurized to three times its moles, and one heated to 400 K. A room is the tiles air flows
        /// between (<c>TileAtmosphere.AdjacentBits</c>), at least <see cref="MinRoomTiles"/> of them holding at least a mole
        /// each on average, with none open to space or the map's atmosphere. The vented room is the largest with no atmos
        /// device on any tile, so no vent
        /// refills it; the other two are the largest rooms left. Each is skipped when the hull has no such room. Returns
        /// each one applied with its room's tiles, for the report's gas control.
        /// </summary>
        private static List<(string Recipe, List<Vector2i> Tiles)> ApplyLivedInAtmos(TestPair pair, EntityUid grid)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var applied = new List<(string Recipe, List<Vector2i> Tiles)>();

            if (!entMan.TryGetComponent<GridAtmosphereComponent>(grid, out var gridAtmos)
                || !entMan.TryGetComponent<MapGridComponent>(grid, out var mapGrid))
            {
                return applied;
            }

            var maps = server.System<SharedMapSystem>();
            var anchored = new List<EntityUid>();
            var seen = new HashSet<Vector2i>();
            var rooms = new List<(List<Vector2i> Tiles, bool HasDevice)>();

            // A room already at vacuum measures nothing: venting it changes no value and tripling it stays zero.
            const float minMolesPerTile = 1f;

            static bool Interior(TileAtmosphere tile) =>
                tile.Air is { Immutable: false } && !tile.Space && !tile.MapAtmosphere && !tile.NoGridTile;

            foreach (var (start, startTile) in gridAtmos.Tiles)
            {
                if (!Interior(startTile) || !seen.Add(start))
                    continue;

                var tiles = new List<Vector2i>();
                var open = false;
                var hasDevice = false;
                var moles = 0f;
                var frontier = new Stack<TileAtmosphere>();
                frontier.Push(startTile);

                while (frontier.TryPop(out var tile))
                {
                    tiles.Add(tile.GridIndices);
                    moles += tile.Air!.TotalMoles;

                    anchored.Clear();
                    maps.GetAnchoredEntities((grid, mapGrid), tile.GridIndices, anchored);
                    hasDevice |= anchored.Any(uid => entMan.HasComponent<AtmosDeviceComponent>(uid));

                    for (var i = 0; i < Atmospherics.Directions; i++)
                    {
                        if (!tile.AdjacentBits.IsFlagSet((AtmosDirection) (1 << i)))
                            continue;

                        if (tile.AdjacentTiles[i] is not { } next || !Interior(next))
                            open = true;
                        else if (seen.Add(next.GridIndices))
                            frontier.Push(next);
                    }
                }

                if (!open && tiles.Count >= MinRoomTiles && moles >= tiles.Count * minMolesPerTile)
                    rooms.Add((tiles, hasDevice));
            }

            rooms.Sort((a, b) => b.Tiles.Count.CompareTo(a.Tiles.Count));
            var atmos = server.System<AtmosphereSystem>();

            void Apply(List<Vector2i> tiles, Action<GasMixture> change)
            {
                foreach (var indices in tiles)
                {
                    if (atmos.GetTileMixture(grid, null, indices, excite: true) is { } air)
                        change(air);
                }
            }

            var vented = rooms.FindIndex(room => !room.HasDevice);
            if (vented >= 0)
            {
                Apply(rooms[vented].Tiles, air => air.Clear());
                applied.Add(("vent-room", rooms[vented].Tiles));
                rooms.RemoveAt(vented);
            }

            if (rooms.Count > 0)
            {
                Apply(rooms[0].Tiles, air => air.Multiply(3f));
                applied.Add(("pressurize-room", rooms[0].Tiles));
                rooms.RemoveAt(0);
            }

            if (rooms.Count > 0)
            {
                Apply(rooms[0].Tiles, air => air.Temperature = 400f);
                applied.Add(("heat-room", rooms[0].Tiles));
            }

            return applied;
        }

        private sealed record RoundTripResult(
            DrydockStateSnapshot Early,
            DrydockStateSnapshot Before,
            DrydockStateSnapshot After,
            DrydockStateSnapshot Late,
            EntityUid Retrieved,
            double ElapsedSeconds,
            DrydockMapInitReport? MapInit);

        private static async Task<RoundTripResult> RoundTrip(TestPair pair, EntityUid grid, Guid owner, EntityUid station)
        {
            var server = pair.Server;
            var timing = server.ResolveDependency<IGameTiming>();
            var fidelity = server.System<DrydockFidelitySystem>();

            DrydockStateSnapshot early = default!;
            await server.WaitPost(() => early = fidelity.DeepSnapshotGrid(grid));
            await pair.RunTicksSync(LiveWindowTicks);

            DrydockStateSnapshot before = default!;
            await server.WaitPost(() => before = fidelity.DeepSnapshotGrid(grid));

            var clockBefore = timing.CurTime;
            var (retrieved, mapInit) = EngineMode
                ? (await EngineRoundTrip(pair, grid), null)
                : await DrydockRoundTrip(pair, grid, owner, station);

            await pair.RunTicksSync(SettleTicks);

            DrydockStateSnapshot after = default!;
            await server.WaitPost(() => after = fidelity.DeepSnapshotGrid(retrieved));
            var elapsed = (timing.CurTime - clockBefore).TotalSeconds;

            await pair.RunTicksSync((int) Math.Ceiling(LateSeconds / timing.TickPeriod.TotalSeconds));

            DrydockStateSnapshot late = default!;
            await server.WaitPost(() => late = fidelity.DeepSnapshotGrid(retrieved));

            return new RoundTripResult(early, before, after, late, retrieved, elapsed, mapInit);
        }

        private static async Task<(EntityUid Grid, DrydockMapInitReport? MapInit)> DrydockRoundTrip(
            TestPair pair,
            EntityUid grid,
            Guid owner,
            EntityUid station)
        {
            var server = pair.Server;
            var timing = server.ResolveDependency<IGameTiming>();
            var drydock = server.System<DrydockSystem>();

            var (storeResult, shipId) = await DrydockTestHelpers.RunOnServer(pair,
                () => drydock.TryStoreShip(grid, owner, null));
            Assert.That(storeResult, Is.EqualTo(DrydockStoreResult.Success), "store refused.");

            await pair.RunTicksSync((int) Math.Ceiling(ClockGapSeconds / timing.TickPeriod.TotalSeconds));

            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Succeeded, Is.True, $"retrieve failed with {retrieved.Result}.");

            return (retrieved.Grid!.Value, server.System<DrydockFidelitySystem>().LastMapInitReport);
        }

        /// <summary>
        /// The same round trip through nothing but the engine: <c>MapLoaderSystem.TrySaveGrid</c> to user data,
        /// the grid deleted, the clock advanced, and <c>TryLoadGrid</c> back onto the same map. A post-init file's
        /// entities are flagged map-initialized without a <c>MapInitEvent</c> (<c>EntityDeserializer.SetMapInitLifestage</c>),
        /// so this measures what a plain engine save and load keeps, with no drydock step on either side.
        /// </summary>
        private static async Task<EntityUid> EngineRoundTrip(TestPair pair, EntityUid grid)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var timing = server.ResolveDependency<IGameTiming>();
            var mapLoader = server.System<MapLoaderSystem>();
            var file = new ResPath($"/DrydockLadder/{Guid.NewGuid():N}.yml");

            var mapId = MapId.Nullspace;
            await server.WaitPost(() =>
            {
                mapId = entMan.GetComponent<TransformComponent>(grid).MapID;
                Assert.That(mapLoader.TrySaveGrid(grid, file), Is.True, "engine save refused.");
                entMan.DeleteEntity(grid);
            });

            await pair.RunTicksSync((int) Math.Ceiling(ClockGapSeconds / timing.TickPeriod.TotalSeconds));

            EntityUid loaded = default;
            await server.WaitPost(() =>
            {
                Assert.That(mapLoader.TryLoadGrid(mapId, file, out var result), Is.True, "engine load failed.");
                loaded = result!.Value.Owner;
            });

            return loaded;
        }

        private enum Recovery
        {
            Persistent,
            Settling,
            Settled,
        }

        /// <summary>
        /// How a difference looks in the late snapshot: back at its stored value (settled), a number at
        /// most half as far from it as it was straight after the retrieve (settling), or neither. A
        /// whole-entity line has no value of its own, so it settled when the entity's presence matches
        /// again.
        /// </summary>
        private static Recovery RecoveryOf(string? key, RoundTripResult result)
        {
            if (key == null)
                return Recovery.Persistent;

            if (key.Contains("|<entity:"))
            {
                var path = key[..key.IndexOf('|')] + "|";
                return result.Before.Values.Keys.Any(k => k.StartsWith(path, StringComparison.Ordinal))
                       == result.Late.Values.Keys.Any(k => k.StartsWith(path, StringComparison.Ordinal))
                    ? Recovery.Settled
                    : Recovery.Persistent;
            }

            var had = result.Before.Values.TryGetValue(key, out var was);
            var has = result.Late.Values.TryGetValue(key, out var now);
            if (had != has)
                return Recovery.Persistent;
            if (!had)
                return Recovery.Settled;

            if (key.EndsWith(DrydockFidelitySystem.TimeSuffix))
            {
                return DrydockFidelitySystem.TimeKeepsItsMeaning(was!, now!, TimeToleranceSeconds)
                    ? Recovery.Settled
                    : Recovery.Persistent;
            }

            if (was == now)
                return Recovery.Settled;

            return result.After.Values.TryGetValue(key, out var then)
                   && Number(was) is { } b && Number(then) is { } a && Number(now) is { } l
                   && Math.Abs(l - b) <= Math.Abs(a - b) / 2
                ? Recovery.Settling
                : Recovery.Persistent;
        }

        private static double? Number(string? value)
        {
            if (value == null)
                return null;
            if (value.StartsWith("count=", StringComparison.Ordinal))
            {
                value = value["count=".Length..];
                var space = value.IndexOf(' ');
                if (space >= 0)
                    value = value[..space];
            }

            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n
                : null;
        }

        private static void Report(
            StringBuilder sb,
            int rung,
            string vesselId,
            int trip,
            RoundTripResult result,
            HashSet<string>? grants,
            List<(string Recipe, List<Vector2i> Tiles)> gasRooms,
            IPrototypeManager protoMan)
        {
            var live = DrydockStateSnapshot.Diff(result.Early, result.Before)
                .Select(KeyOf)
                .Where(k => k != null)
                .ToHashSet();

            var findings = new List<string>();
            var liveLines = new List<string>();
            var grantLines = new List<string>();
            var policyLines = new List<string>();
            var unsavedLines = new List<string>();
            var classifiedLines = new List<string>();
            var belowFloorLines = new List<string>();
            var settledLines = new List<string>();
            var settlingLines = new List<string>();
            var movedLines = new List<string>();
            var timeKept = 0;

            var diff = DrydockStateSnapshot.Diff(result.Before, result.After);
            var moved = MovedLines(diff);
            var consequences = PolicyConsequences(diff, result.Before, protoMan);

            foreach (var line in diff)
            {
                var key = KeyOf(line);

                if (moved.Contains(line))
                {
                    movedLines.Add(line);
                    continue;
                }

                if (line.StartsWith("CHANGED") && key != null && key.EndsWith(DrydockFidelitySystem.TimeSuffix)
                    && DrydockFidelitySystem.TimeKeepsItsMeaning(result.Before.Values[key], result.After.Values[key], TimeToleranceSeconds))
                {
                    timeKept++;
                    continue;
                }

                var recovery = RecoveryOf(key, result);

                if (key != null && live.Contains(key))
                    liveLines.Add(line);
                else if (grants != null && IsGrant(line, grants))
                    grantLines.Add(line);
                else if (IsRemovedByRule(line, key, protoMan) || IsPolicyConsequence(key, consequences))
                    policyLines.Add(line);
                else if (UnsavedAncestor(line, key, result.Before) is { } unsaved)
                {
                    if (IsTransient(unsaved, result.Before, protoMan))
                        classifiedLines.Add(line);
                    else
                        unsavedLines.Add(line);
                }
                else if (key != null && Classify(key, result, recovery, out var belowFloor) is { } _)
                    (belowFloor ? belowFloorLines : classifiedLines).Add(line);
                else if (recovery == Recovery.Settled)
                    settledLines.Add(line);
                else if (recovery == Recovery.Settling)
                    settlingLines.Add(line);
                else
                    findings.Add(line);
            }

            foreach (var member in TotalChecked)
            {
                var before = Total(result.Before, member);
                var after = Total(result.After, member);
                if (before != after)
                    findings.Add($"CHANGED  grid|{member}#total: {before} -> {after}");
            }

            Dump(rung, vesselId, trip, result, findings.Concat(settlingLines));

            sb.AppendLine($"[ladder] round trip {trip}: {result.Before.Entities} entities before, {result.After.Entities} after "
                          + $"({result.Before.TieBroken}/{result.After.TieBroken} tie-broken, "
                          + $"{result.Before.Uncapturable}/{result.After.Uncapturable} uncapturable), "
                          + $"{result.Before.Values.Count} keys, {live.Count} live key(s), clock advanced {result.ElapsedSeconds:F1}s, "
                          + $"{timeKept} time change(s) kept their meaning.");

            AppendUncapturable(sb, rung, vesselId, trip, result);

            foreach (var (recipe, tiles) in gasRooms)
            {
                var at = tiles.Select(tile => $"{tile.X},{tile.Y}").ToHashSet();
                var anchor = tiles.OrderBy(tile => tile.X).ThenBy(tile => tile.Y).First();
                sb.AppendLine($"[ladder] gas control {recipe} at {anchor.X},{anchor.Y}: before {RoomGas(result.Before, at)}; after {RoomGas(result.After, at)}; late {RoomGas(result.Late, at)}");
            }

            AppendKinds(sb, rung, vesselId, trip, "finding", findings, examples: 2);
            AppendKinds(sb, rung, vesselId, trip, "settling", settlingLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "settled", settledLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "classified", classifiedLines, examples: 0);
            AppendKinds(sb, rung, vesselId, trip, "below-floor", belowFloorLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "policy", policyLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "unsaved", unsavedLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "moved", movedLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "live", liveLines, examples: 0);
            if (grants != null)
                AppendKinds(sb, rung, vesselId, trip, "grant", grantLines, examples: 0);

            if (result.MapInit != null)
                sb.AppendLine($"[ladder] round trip {trip} map-init transaction:").Append(result.MapInit.Detail());
        }

        /// <summary>
        /// The registry entry that explains a key's difference, or null. A Live entry explains it only while
        /// the value is settling, settled, or within <see cref="LiveTolerance"/> of its stored value, or within
        /// <see cref="LiveFloor"/>, which sets <paramref name="belowFloor"/>.
        /// </summary>
        private static StateClass? Classify(string key, RoundTripResult result, Recovery recovery, out bool belowFloor)
        {
            belowFloor = false;

            // A colon separates a member from the instance it was rendered for (a tile, a node group), as in KindOf.
            var member = key[(key.IndexOf('|') + 1)..];
            var colon = member.IndexOf(':');
            if (colon >= 0)
                member = member[..colon];

            if (member.EndsWith(DrydockFidelitySystem.TimeSuffix))
                member = member[..^DrydockFidelitySystem.TimeSuffix.Length];

            var dot = member.IndexOf('.');
            var component = dot < 0 ? member : member[..dot];

            if (!Registry.TryGetValue(member, out var entry) && !Registry.TryGetValue(component + ".*", out entry))
                return null;

            if (entry.Class != StateClass.Live || recovery != Recovery.Persistent)
                return entry.Class;

            if (!result.Before.Values.TryGetValue(key, out var was)
                || !result.After.Values.TryGetValue(key, out var now)
                || Number(was) is not { } b
                || Number(now) is not { } a)
            {
                return null;
            }

            var difference = Math.Abs(a - b);
            if (difference <= Math.Abs(b) * LiveTolerance)
                return entry.Class;

            // Rounded, because two rendered decimals subtract to a hair over the floor (0.10 - 0.09 = 0.010000000000000009).
            if (Math.Round(difference, 9) > LiveFloor)
                return null;

            belowFloor = true;
            return entry.Class;
        }

        /// <summary>
        /// A whole entity the store or retrieve removed by rule, with everything inside it: saving contraband
        /// (the purge), mech parts and equipment (the drydock strip rule in
        /// <c>Resources/Prototypes/_Triad/Drydock/strip.yml</c>), or an unoccupied AI core's brain vessel
        /// (emptied at store, <c>DrydockSystem.StationAi.cs</c>). Matched on the entity or any ancestor.
        /// </summary>
        private static bool IsRemovedByRule(string line, string? key, IPrototypeManager protoMan)
        {
            if (!line.StartsWith("GONE") || key == null || !key.Contains("|<entity:"))
                return false;

            foreach (var segment in key[..key.IndexOf('|')].Split('/'))
            {
                var cut = segment.IndexOfAny(new[] { '@', '#' });
                var proto = cut < 0 ? segment : segment[..cut];

                if (!protoMan.TryIndex<EntityPrototype>(proto, out var entity))
                    continue;

                // A save: false contraband entity (a ship shield) is runtime state its owner respawns, not player
                // property the purge takes, so its loss is left to the unsaved and recovery checks.
                if ((entity.Components.ContainsKey("SavingContraband") && entity.MapSavable)
                    || entity.Components.ContainsKey("MechEquipment")
                    || entity.Components.ContainsKey("Mech")
                    || proto == "StationAiBrainVessel"
                    || protoMan.EnumerateParents<EntityPrototype>(proto).Any(p => p.ID is "BaseMechPart" or "BaseExosuitParts"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Where a removal by rule reaches other entities: the container that held a removed entity
        /// (<c>path|ContainerManagerComponent.container.ID</c> prefixes, as <c>path/ID/</c>), and every component
        /// whose stored values named a removed entity (<c>path|Component.</c> prefixes). The second covers state
        /// the removed entity's owner system tears down with it, such as an AI core holo's movement relay, which
        /// <c>SharedMoverController.OnRelayShutdown</c> strips when the brain that sourced it goes.
        /// </summary>
        private static (HashSet<string> Containers, HashSet<string> Components) PolicyConsequences(
            List<string> diff,
            DrydockStateSnapshot before,
            IPrototypeManager protoMan)
        {
            var removed = new List<string>();
            foreach (var line in diff)
            {
                var key = KeyOf(line);
                if (key != null && key.Contains("|<entity:") && IsRemovedByRule(line, key, protoMan))
                    removed.Add(key[..key.IndexOf('|')]);
            }

            var containers = new HashSet<string>(StringComparer.Ordinal);
            var components = new HashSet<string>(StringComparer.Ordinal);
            if (removed.Count == 0)
                return (containers, components);

            foreach (var path in removed)
            {
                var slash = path.LastIndexOf('/');
                if (slash > 0)
                    containers.Add(path[..(slash + 1)]);
            }

            foreach (var (key, value) in before.Values)
            {
                if (!removed.Any(path => Names(value, path)))
                    continue;

                var bar = key.IndexOf('|');
                var dot = key.IndexOf('.', bar + 1);
                if (bar > 0 && dot > bar)
                    components.Add(key[..(dot + 1)]);
            }

            return (containers, components);
        }

        private static bool IsPolicyConsequence(string? key, (HashSet<string> Containers, HashSet<string> Components) consequences)
        {
            if (key == null || (consequences.Components.Count == 0 && consequences.Containers.Count == 0))
                return false;

            const string containerMember = "|ContainerManagerComponent.container.";
            var at = key.IndexOf(containerMember, StringComparison.Ordinal);
            if (at > 0 && consequences.Containers.Contains($"{key[..at]}/{key[(at + containerMember.Length)..]}/"))
                return true;

            var bar = key.IndexOf('|');
            var dot = key.IndexOf('.', bar + 1);
            return bar > 0 && dot > bar && consequences.Components.Contains(key[..(dot + 1)]);
        }

        /// <summary>
        /// Whether a rendered value names the entity at <paramref name="path"/> or something inside it: the
        /// path must not continue into a longer name or a sibling suffix (<c>#n</c>, <c>@x,y</c>).
        /// </summary>
        private static bool Names(string value, string path)
        {
            for (var at = value.IndexOf(path, StringComparison.Ordinal); at >= 0; at = value.IndexOf(path, at + 1, StringComparison.Ordinal))
            {
                var end = at + path.Length;
                var startsClean = at == 0 || !char.IsLetterOrDigit(value[at - 1]);
                var endsClean = end == value.Length || value[end] is not ('#' or '@') && !char.IsLetterOrDigit(value[end]);
                if (startsClean && endsClean)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whole-entity lines for loose entities on the deck that did not vanish but settled onto a
        /// neighbouring tile: a GONE <c>Proto@x,y</c> paired with an APPEARED <c>Proto@x',y'</c> of the same
        /// prototype at most one tile away, and every GONE and APPEARED line inside either. Returns the lines
        /// so paired.
        /// </summary>
        private static HashSet<string> MovedLines(List<string> lines)
        {
            var gone = new List<(string Line, string Path, string Proto, int X, int Y)>();
            var appeared = new List<(string Line, string Path, string Proto, int X, int Y)>();

            foreach (var line in lines)
            {
                var key = KeyOf(line);
                if (key == null || !key.Contains("|<entity:"))
                    continue;

                var path = key[..key.IndexOf('|')];
                if (path.Contains('/') || !TryTile(path, out var proto, out var x, out var y))
                    continue;

                if (line.StartsWith("GONE"))
                    gone.Add((line, path, proto, x, y));
                else if (line.StartsWith("APPEARED"))
                    appeared.Add((line, path, proto, x, y));
            }

            var moved = new HashSet<string>();
            var pairs = new List<(string From, string To)>();
            foreach (var g in gone)
            {
                var match = appeared.FirstOrDefault(a => !moved.Contains(a.Line) && a.Proto == g.Proto
                                                         && Math.Abs(a.X - g.X) <= 1 && Math.Abs(a.Y - g.Y) <= 1);
                if (match.Line == null)
                    continue;

                moved.Add(g.Line);
                moved.Add(match.Line);
                pairs.Add((g.Path + "/", match.Path + "/"));
            }

            foreach (var line in lines)
            {
                var key = KeyOf(line);
                if (key == null || !key.Contains("|<entity:"))
                    continue;

                var path = key[..key.IndexOf('|')];
                if (pairs.Any(p => (line.StartsWith("GONE") && path.StartsWith(p.From, StringComparison.Ordinal))
                                   || (line.StartsWith("APPEARED") && path.StartsWith(p.To, StringComparison.Ordinal))))
                {
                    moved.Add(line);
                }
            }

            return moved;

            static bool TryTile(string path, out string proto, out int x, out int y)
            {
                proto = "";
                x = y = 0;
                var at = path.IndexOf('@');
                if (at < 0)
                    return false;

                proto = path[..at];
                var coords = path[(at + 1)..];
                var hash = coords.IndexOf('#');
                if (hash >= 0)
                    coords = coords[..hash];

                var comma = coords.IndexOf(',');
                return comma > 0
                       && int.TryParse(coords[..comma], out x)
                       && int.TryParse(coords[(comma + 1)..], out y);
            }
        }

        /// <summary>
        /// <c>save: false</c> prototypes whose loss across a store is expected: effects and markers with no
        /// meaning past the moment, and timed projections. An entity inheriting from one counts. Audio
        /// entities are recognised by their component instead.
        /// </summary>
        private static readonly HashSet<string> TransientUnsaved = new(StringComparer.Ordinal)
        {
            "PointingArrow",
            "Exclamation",
            "EffectVoidBlink",
            "BaseHolosign",
        };

        /// <summary>
        /// For a whole entity that did not come back, the path of the nearest entity at or above it whose
        /// prototype is <c>save: false</c>, which the store deleted along with everything inside it; or null.
        /// </summary>
        private static string? UnsavedAncestor(string line, string? key, DrydockStateSnapshot before)
        {
            if (!line.StartsWith("GONE") || key == null || !key.Contains("|<entity:"))
                return null;

            var path = key[..key.IndexOf('|')];
            var segments = path.Split('/');
            for (var i = segments.Length; i > 0; i--)
            {
                var prefix = string.Join('/', segments, 0, i);
                if (before.Values.TryGetValue(prefix + "|MetaDataComponent.savable", out var savable) && savable == "false")
                    return prefix;
            }

            return null;
        }

        private static bool IsTransient(string unsavedPath, DrydockStateSnapshot before, IPrototypeManager protoMan)
        {
            if (before.Values.ContainsKey(unsavedPath + "|AudioComponent.<present>"))
                return true;

            var proto = before.Values.GetValueOrDefault(unsavedPath + "|MetaDataComponent.prototype");
            if (proto == null || !protoMan.HasIndex<EntityPrototype>(proto))
                return false;

            return protoMan.EnumerateParents<EntityPrototype>(proto, includeSelf: true)
                .Any(p => TransientUnsaved.Contains(p.ID));
        }

        private static double Total(DrydockStateSnapshot snapshot, string member)
        {
            var suffix = "|" + member;
            return snapshot.Values
                .Where(kv => kv.Key.EndsWith(suffix, StringComparison.Ordinal))
                .Sum(kv => Number(kv.Value) ?? 0);
        }

        /// <summary>
        /// When <c>LADDER_DUMP</c> names a directory, writes every finding and settling line's full
        /// before, after and late values there, one file per rung and round trip. The console report
        /// cuts values at 120 characters.
        /// </summary>
        private static void Dump(int rung, string vesselId, int trip, RoundTripResult result, IEnumerable<string> lines)
        {
            var directory = Environment.GetEnvironmentVariable("LADDER_DUMP");
            if (string.IsNullOrEmpty(directory))
                return;

            System.IO.Directory.CreateDirectory(directory);
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                var key = KeyOf(line);
                sb.AppendLine(line);
                if (key == null)
                    continue;

                sb.AppendLine($"  before: {result.Before.Values.GetValueOrDefault(key, "<absent>")}");
                sb.AppendLine($"  after:  {result.After.Values.GetValueOrDefault(key, "<absent>")}");
                sb.AppendLine($"  late:   {result.Late.Values.GetValueOrDefault(key, "<absent>")}");
            }

            System.IO.File.WriteAllText(
                System.IO.Path.Combine(directory, $"rung{rung:000}_{vesselId}_trip{trip}.txt"), sb.ToString());
        }

        /// <summary>
        /// A gas recipe's room as one snapshot saw it: the moles over its tiles and their mean temperature. The control
        /// that the recipe held until the store, and the measure of what the round trip kept, since a room kept exactly
        /// produces no difference line at all.
        /// </summary>
        private static string RoomGas(DrydockStateSnapshot snapshot, HashSet<string> tiles)
        {
            const string moles = "grid|GridAtmosphereComponent.Tiles.moles:";
            const string temperature = "grid|GridAtmosphereComponent.Tiles.temperature:";
            var total = 0.0;
            var heat = 0.0;
            var measured = 0;

            foreach (var (key, value) in snapshot.Values)
            {
                if (key.StartsWith(moles, StringComparison.Ordinal))
                {
                    var tile = key[moles.Length..];
                    var dot = tile.IndexOf('.');
                    if (dot > 0 && tiles.Contains(tile[..dot]))
                        total += Number(value) ?? 0;
                }
                else if (key.StartsWith(temperature, StringComparison.Ordinal) && tiles.Contains(key[temperature.Length..]))
                {
                    heat += Number(value) ?? 0;
                    measured++;
                }
            }

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return measured == 0
                ? $"no air on {tiles.Count} tile(s)"
                : $"{total.ToString("F1", culture)} mol, {(heat / measured).ToString("F1", culture)} K over {measured}/{tiles.Count} tile(s)";
        }

        /// <summary>
        /// One <c>[ladder-kind]</c> line per member the detector could not render on either side, counted before the
        /// store, with the count after the retrieve, the first failure's reason, and one value as the reflection
        /// render compared it, so a reader can see how much of the member the comparison covered.
        /// </summary>
        private static void AppendUncapturable(StringBuilder sb, int rung, string vesselId, int trip, RoundTripResult result)
        {
            var members = result.Before.UncapturableMembers.Keys
                .Union(result.After.UncapturableMembers.Keys)
                .OrderBy(member => member, StringComparer.Ordinal)
                .ToList();

            sb.AppendLine($"[ladder] uncapturable: {result.Before.Uncapturable}/{result.After.Uncapturable} value(s) in {members.Count} member(s)");

            foreach (var member in members)
            {
                result.Before.UncapturableMembers.TryGetValue(member, out var before);
                result.After.UncapturableMembers.TryGetValue(member, out var after);
                var reason = before.Count > 0 ? before.Reason : after.Reason;

                var sample = result.Before.Values.FirstOrDefault(kv => kv.Key.EndsWith("|" + member, StringComparison.Ordinal)).Value;
                if (sample is { Length: > 160 })
                    sample = sample[..160] + "…";

                sb.AppendLine($"[ladder-kind] rung={rung} vessel={vesselId} trip={trip} bucket=uncapturable count={before.Count} kind=UNCAPTURABLE {member}");
                sb.AppendLine($"         after: {after.Count}; e.g. {reason}");
                sb.AppendLine($"         compared as: {sample ?? "<no value>"}");
            }
        }

        private static void AppendKinds(StringBuilder sb, int rung, string vesselId, int trip, string bucket, List<string> lines, int examples)
        {
            var kinds = lines.GroupBy(KindOf).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            sb.AppendLine($"[ladder] {bucket}: {lines.Count} line(s) in {kinds.Count} kind(s)");

            foreach (var kind in kinds)
            {
                sb.AppendLine($"[ladder-kind] rung={rung} vessel={vesselId} trip={trip} bucket={bucket} count={kind.Count()} kind={kind.Key}");
                foreach (var example in kind.Take(examples))
                    sb.AppendLine($"         e.g. {example}");
            }
        }

        private static bool IsGrant(string line, HashSet<string> grants)
        {
            var kind = KindOf(line);
            var member = kind[(kind.IndexOf(' ') + 1)..];
            var dot = member.IndexOf('.');
            var component = dot < 0 ? member : member[..dot];
            var trimmed = member.EndsWith(DrydockFidelitySystem.TimeSuffix)
                ? member[..^DrydockFidelitySystem.TimeSuffix.Length]
                : member;

            if (grants.Contains(component) || grants.Contains(trimmed))
                return true;

            var key = KeyOf(line);
            if (key == null)
                return false;

            var proto = ProtoOfPath(key[..key.IndexOf('|')]);
            foreach (var grant in grants)
            {
                var colon = grant.IndexOf(':');
                if (colon < 0 || grant[(colon + 1)..] != component)
                    continue;

                var scope = grant[..colon];
                if (scope.EndsWith('*') ? proto.StartsWith(scope[..^1], StringComparison.Ordinal) : proto == scope)
                    return true;
            }

            return false;
        }

        /// <summary>The prototype a deep-snapshot path names: its last segment, without tile or sibling suffix.</summary>
        private static string ProtoOfPath(string path)
        {
            var leaf = path[(path.LastIndexOf('/') + 1)..];
            var cut = leaf.IndexOfAny(new[] { '@', '#' });
            return cut < 0 ? leaf : leaf[..cut];
        }

        private static string? KeyOf(string line)
        {
            var start = line.IndexOf(' ');
            if (start < 0)
                return null;

            var body = line[start..].TrimStart();
            var colon = body.IndexOf(": ", StringComparison.Ordinal);
            if (colon >= 0)
                return body[..colon];

            var space = body.IndexOf(" (", StringComparison.Ordinal);
            return space < 0 ? body : body[..space];
        }

        /// <summary>The verb and <c>Component.Member</c> of a diff line, with the entity path removed.</summary>
        private static string KindOf(string line)
        {
            var verb = line.IndexOf(' ');
            if (verb < 0)
                return line;

            var body = line[verb..].TrimStart();
            var pipe = body.IndexOf('|');
            if (pipe < 0)
                return $"{line[..verb]} {body}";

            var member = body[(pipe + 1)..];
            if (member.StartsWith("<entity:", StringComparison.Ordinal))
                return $"{line[..verb]} {member[..(member.IndexOf('>') + 1)]}";

            var cut = member.IndexOfAny(new[] { ' ', ':' });
            return $"{line[..verb]} {(cut < 0 ? member : member[..cut])}";
        }
    }
}
