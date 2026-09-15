#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server.Storage.Components;
using Content.Server.Storage.EntitySystems;
using Content.Server.VendingMachines;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Doors.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Content.Shared.VendingMachines;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Wires;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

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
    /// as its file describes it.</para>
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
    /// <para>What remains is sorted against <see cref="Registry"/> ("classified"), the store's contraband
    /// purge ("policy"), <c>save: false</c> entities the store deleted ("unsaved", unless
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
        private const int PreSettleTicks = 60;
        private const int SettleTicks = 10;
        private const int LiveWindowTicks = 90;
        private const double ClockGapSeconds = 10;
        private const double TimeToleranceSeconds = 1;
        private const double LateSeconds = 10;

        /// <summary>
        /// Components the retrieve adds or rewrites on purpose because a purchase would have: the
        /// station join, ownership, repair baseline and console locks. Keyed <c>Component</c>,
        /// <c>Component.member</c>, or <c>Prototype:Component</c> for a grant only one prototype gets.
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
            "NFHolopadShip:LabelComponent",
            "NFHolopadShip:NameModifierComponent",
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
            ["GunComponent.~ShootCoordinates"] = (StateClass.Volatile, "the last shot's aim point"),
            ["AppearanceComponent.~AppearanceData"] = (StateClass.Derived, "the same data the Appearance.* keys compare"),
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

            var first = await RoundTrip(pair, grid, owner, station);
            var second = await RoundTrip(pair, first.Retrieved, owner, station);

            var sb = new StringBuilder();
            sb.AppendLine($"[ladder] rung {rung} {vesselId}");
            sb.AppendLine($"[ladder] lived-in recipes applied: {(recipes.Count == 0 ? "none" : string.Join(", ", recipes))}");
            Report(sb, rung, vesselId, 1, first, RetrieveGrants, protoMan);
            Report(sb, rung, vesselId, 2, second, null, protoMan);
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
        /// the deck, an interior door open, a wires panel open, a lathe queue, a restocked vendor, and a
        /// sidearm fired. Each recipe takes the first
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

            var door = First((_, id) => id.StartsWith("Airlock", StringComparison.Ordinal)
                                        && !id.Contains("Shuttle", StringComparison.Ordinal)
                                        && !id.Contains("External", StringComparison.Ordinal));
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

            if (First((uid, _) => entMan.HasComponent<LatheComponent>(uid)) is { } lathe)
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
            var drydock = server.System<DrydockSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();

            DrydockStateSnapshot early = default!;
            await server.WaitPost(() => early = fidelity.DeepSnapshotGrid(grid));
            await pair.RunTicksSync(LiveWindowTicks);

            DrydockStateSnapshot before = default!;
            await server.WaitPost(() => before = fidelity.DeepSnapshotGrid(grid));

            var (storeResult, shipId) = await DrydockTestHelpers.RunOnServer(pair,
                () => drydock.TryStoreShip(grid, owner, null));
            Assert.That(storeResult, Is.EqualTo(DrydockStoreResult.Success), "store refused.");

            var clockBefore = timing.CurTime;
            await pair.RunTicksSync((int) Math.Ceiling(ClockGapSeconds / timing.TickPeriod.TotalSeconds));

            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Succeeded, Is.True, $"retrieve failed with {retrieved.Result}.");
            var mapInit = fidelity.LastMapInitReport;

            await pair.RunTicksSync(SettleTicks);

            DrydockStateSnapshot after = default!;
            await server.WaitPost(() => after = fidelity.DeepSnapshotGrid(retrieved.Grid!.Value));
            var elapsed = (timing.CurTime - clockBefore).TotalSeconds;

            await pair.RunTicksSync((int) Math.Ceiling(LateSeconds / timing.TickPeriod.TotalSeconds));

            DrydockStateSnapshot late = default!;
            await server.WaitPost(() => late = fidelity.DeepSnapshotGrid(retrieved.Grid!.Value));

            return new RoundTripResult(early, before, after, late, retrieved.Grid!.Value, elapsed, mapInit);
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
            var settledLines = new List<string>();
            var settlingLines = new List<string>();
            var timeKept = 0;

            foreach (var line in DrydockStateSnapshot.Diff(result.Before, result.After))
            {
                var key = KeyOf(line);

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
                else if (IsContrabandPurge(line, key, protoMan))
                    policyLines.Add(line);
                else if (UnsavedAncestor(line, key, result.Before) is { } unsaved)
                {
                    if (IsTransient(unsaved, result.Before, protoMan))
                        classifiedLines.Add(line);
                    else
                        unsavedLines.Add(line);
                }
                else if (key != null && Classify(key, result, recovery) is { } _)
                    classifiedLines.Add(line);
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

            AppendKinds(sb, rung, vesselId, trip, "finding", findings, examples: 2);
            AppendKinds(sb, rung, vesselId, trip, "settling", settlingLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "settled", settledLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "classified", classifiedLines, examples: 0);
            AppendKinds(sb, rung, vesselId, trip, "policy", policyLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "unsaved", unsavedLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "live", liveLines, examples: 0);
            if (grants != null)
                AppendKinds(sb, rung, vesselId, trip, "grant", grantLines, examples: 0);

            if (result.MapInit != null)
                sb.AppendLine($"[ladder] round trip {trip} map-init transaction:").Append(result.MapInit.Detail());
        }

        /// <summary>
        /// The registry entry that explains a key's difference, or null. A Live entry explains it only while
        /// the value is settling, settled, or within <see cref="LiveTolerance"/> of its stored value.
        /// </summary>
        private static StateClass? Classify(string key, RoundTripResult result, Recovery recovery)
        {
            var member = key[(key.IndexOf('|') + 1)..];
            if (member.EndsWith(DrydockFidelitySystem.TimeSuffix))
                member = member[..^DrydockFidelitySystem.TimeSuffix.Length];

            var dot = member.IndexOf('.');
            var component = dot < 0 ? member : member[..dot];

            if (!Registry.TryGetValue(member, out var entry) && !Registry.TryGetValue(component + ".*", out entry))
                return null;

            if (entry.Class != StateClass.Live || recovery != Recovery.Persistent)
                return entry.Class;

            return result.Before.Values.TryGetValue(key, out var was)
                   && result.After.Values.TryGetValue(key, out var now)
                   && Number(was) is { } b && Number(now) is { } a
                   && Math.Abs(a - b) <= Math.Abs(b) * LiveTolerance
                ? entry.Class
                : null;
        }

        /// <summary>
        /// A whole entity the store removed as saving contraband: its own prototype carries
        /// <c>SavingContraband</c>, or one of its ancestors' does, so it left with that ancestor.
        /// </summary>
        private static bool IsContrabandPurge(string line, string? key, IPrototypeManager protoMan)
        {
            if (!line.StartsWith("GONE") || key == null || !key.Contains("|<entity:"))
                return false;

            foreach (var segment in key[..key.IndexOf('|')].Split('/'))
            {
                var cut = segment.IndexOfAny(new[] { '@', '#' });
                var proto = cut < 0 ? segment : segment[..cut];

                if (protoMan.TryIndex<EntityPrototype>(proto, out var entity)
                    && entity.Components.ContainsKey("SavingContraband"))
                {
                    return true;
                }
            }

            return false;
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

            var key = KeyOf(line);
            var scoped = key == null ? null : $"{ProtoOfPath(key[..key.IndexOf('|')])}:{component}";

            return grants.Contains(component) || grants.Contains(trimmed) || (scoped != null && grants.Contains(scoped));
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
