#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Trade;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Codec;
using Content.Server.Atmos;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Light.Components;
using Content.Server.Light.EntitySystems;
using Content.Server.Nutrition.EntitySystems;
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
using Content.Shared.Nutrition.Components;
using Content.Shared.Power;
using Content.Shared.Research.Prototypes;
using Content.Shared.Smoking;
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
    /// does not assert on findings; the assertions are that both round trips completed, that the
    /// comparison saw something, and, through the grid image, that the store's despawn left nothing behind and, with
    /// every manifest member applied, that the manifest lost none of its members on the way (<see cref="AssertLoopHeld"/>).
    ///
    /// <para>Before round trip 1 the hull is put into lived-in states (<see cref="ApplyLivedIn"/>), so the
    /// comparison covers damage, cargo, open doors and panels, queues, fired guns and lit smokables, not only a hull
    /// as its file describes it. Once atmos has settled, rooms are vented, pressurized and heated
    /// (<see cref="ApplyLivedInAtmos"/>).</para>
    ///
    /// <para>Members the detector cannot render at all are listed under "uncapturable"
    /// (<see cref="AppendUncapturable"/>): their round trip is unmeasured, not clean.</para>
    ///
    /// <list type="bullet">
    /// <item><b>Round trip 1</b> starts from the shuttle file. Its findings are state a first store
    /// loses. What the retrieve grants a hull on purpose (<see cref="RetrieveGrants"/>) is reported
    /// apart, as is what every retrieve re-stamps (<see cref="RetrieveRestamps"/>) on both round trips.</item>
    /// <item><b>Round trip 2</b> starts from the ship round trip 1 returned. A ship that has been
    /// through the drydock once must come back identical every time after, so every finding here is
    /// the drydock changing a ship it already produced.</item>
    /// </list>
    ///
    /// <para>Each round trip snapshots the live ship twice, <see cref="LiveWindowTicks"/> apart, before
    /// storing. A key that changed between those two is live simulation (power ramps, heat, atmos
    /// timers) and its round-trip difference is reported under "live" rather than as a finding. A recipe
    /// that changes state inside that window sorts live by design, as the match lit in the tick the store
    /// is claimed does, and its control line is where the round trip is read instead.</para>
    ///
    /// <para>What remains is sorted against loose entities that settled onto a neighbouring tile ("moved"),
    /// <see cref="Registry"/> ("classified", or "below-floor" when only <see cref="LiveFloor"/> admits it), removals by rule: contraband, the mech strip, emptied AI cores,
    /// and the containers and components those removals reach (<see cref="PolicyConsequences"/>)
    /// ("policy"), <c>save: false</c> entities the store deleted ("unsaved", unless
    /// <see cref="TransientUnsaved"/> expects the loss), a transient its owner made again after the retrieve
    /// ("recreated", <see cref="RecreatedTransient"/>), and the late snapshot below; only unexplained,
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
    // Serial: a rung's times are read as budgets and its notes as that rung's report, and neither holds with rungs
    // running beside each other.
    [NonParallelizable]
    [TestOf(typeof(DrydockFidelitySystem))]
    public sealed partial class DrydockFidelityLadderLocalTest
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
        /// Round trip 1 only; on round trip 2 the ship already carries them, so a difference is a finding unless
        /// every retrieve rewrites it (<see cref="RetrieveRestamps"/>).
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

        /// <summary>
        /// What every drydock retrieve rewrites whether or not the ship already carries it, keyed as
        /// <see cref="RetrieveGrants"/> is: the ownership status time (<c>DrydockSystem.Retrieve.cs:1392-1400</c>).
        /// Sorted under "grant" on round trip 2, where <see cref="RetrieveGrants"/> no longer applies.
        /// </summary>
        private static readonly HashSet<string> RetrieveRestamps = new(StringComparer.Ordinal)
        {
            "ShipOwnershipComponent.LastStatusChangeTime",
        };

        internal enum StateClass
        {
            /// <summary>Simulation state that moves on its own. Accepted while it is settling, settled or within
            /// <see cref="LiveTolerance"/> of its stored value; a jump past that stays a finding. A time is a
            /// repeating timer whose phase moves by firing, accepted on its entry, whose reason names the period.</summary>
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
        /// entry is an explained difference; anything not here is a finding until explained, and so is a line that
        /// compounds across the two trips, whatever its entry says (<see cref="Compounding"/>). This is the seed
        /// of the census registry the grid image design calls for. Internal, for the manifest test's check that no member
        /// the manifest carries is classified here (DrydockCodecManifestMembersTest).
        /// </summary>
        internal static readonly Dictionary<string, (StateClass Class, string Reason)> Registry = new(StringComparer.Ordinal)
        {
            // Not here, because the manifest carries them and a difference on one is its failure: an extension-cable
            // receiver's provider, an APC's and a SMES's last charge state, a SMES's last charge level, a wire panel's
            // visibility (rows 477, 85, 39, 38 and 437).
            ["HTNComponent.~PlanAccumulator"] = (StateClass.Volatile, "NPC planner tick accumulator"),
            ["HTNComponent.~PlanningJob"] = (StateClass.Volatile, "in-flight planner job"),
            ["HTNComponent.~PlanningToken"] = (StateClass.Volatile, "in-flight planner cancellation token"),
            ["GridAtmosphereComponent.~EqualizationQueueCycleControl"] = (StateClass.Volatile, "atmos processing cursor"),
            ["ApcComponent.~LastExternalState"] = (StateClass.Derived, "APC visual and UI update throttle cache"),
            ["ApcComponent.~LastChargeStateTime"] = (StateClass.Derived, "APC visual and UI update throttle cache"),
            ["ApcPowerProviderComponent.~LinkedReceivers"] = (StateClass.Derived, "receiver pairing is reassigned on reconnect"),
            ["ExtensionCableProviderComponent.~LinkedReceivers"] = (StateClass.Derived, "receiver pairing is reassigned on reconnect"),
            ["ApcPowerReceiverComponent.~Provider"] = (StateClass.Derived, "receiver pairing is reassigned on reconnect"),
            ["ThermalSignatureComponent.*"] = (StateClass.Live, "heat signature accumulator"),
            ["BatteryComponent.CurrentCharge"] = (StateClass.Live, "battery charge under load"),
            ["PowerNetworkBatteryComponent.*"] = (StateClass.Live, "battery supply and ramp under load"),
            ["PowerSupplierComponent.SupplyRampPosition"] = (StateClass.Live, "generator supply ramp"),
            ["HTNComponent.CheckServices"] = (StateClass.Volatile, "planner flag toggled every planning cycle (HTNSystem)"),
            ["PressurizedSolutionComponent.SprayFizzinessThresholdRoll"] = (StateClass.Volatile, "RNG threshold pre-rolled for prediction; a re-roll keeps the odds"),
            ["ShipActivityComponent.*"] = (StateClass.Volatile, "inactivity counters, re-measured every check interval (LimitedShuttleSystem.Update)"),
            ["HandheldLightComponent.~Level"] = (StateClass.Derived, "light level appearance cache"),
            ["GridPathfindingComponent.~Chunks"] = (StateClass.Derived, "pathfinding graph, rebuilt after load"),
            ["GasCanisterComponent.~LastPressure"] = (StateClass.Derived, "canister UI and appearance cache"),
            ["Appearance.ApcVisuals.ChargeState"] = (StateClass.Derived, "follows the APC's charge state, which the load recomputes from the battery once the power net has synced it"),
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
            ["ThrusterComponent.NextFire"] = (StateClass.Live, "burn tick repeating every FireCooldown, 2 s, advanced one cooldown when due (ThrusterSystem.cs:522-525)"),
            ["SpamEmitSoundComponent.NextSound"] = (StateClass.Live, "fires when due and re-rolls its interval (EmitSoundSystem.cs:26-33)"),
            ["DeepFryerComponent.NextFryTime"] = (StateClass.Live, "fry tick repeating every FryInterval, 5 s, while powered, re-armed on a power change (DeepFryerSystem.Update.cs:20-26, DeepFryerSystem.cs:481-486)"),
            ["PoweredLightComponent.~LastThunk"] = (StateClass.Volatile, "turn-on sound throttle (PoweredLightSystem.cs:287-289)"),
            ["SpeechComponent.~LastTimeSoundPlayed"] = (StateClass.Volatile, "speech sound cooldown (SpeechNoiseSystem.cs:70-74)"),
            ["GridPathfindingComponent.~NextUpdate"] = (StateClass.Volatile, "graph update gate, set when a chunk is dirtied (PathfindingSystem.Grid.cs:329-330, :344-345)"),
            ["SmesComponent.~LastChargeLevelTime"] = (StateClass.Volatile, "SMES visual throttle; a restored zero passes the delay test (SmesSystem.cs:41)"),
            ["SmesComponent.~LastChargeStateTime"] = (StateClass.Volatile, "SMES visual throttle; a restored zero passes the delay test (SmesSystem.cs:50)"),
        };

        /// <summary>
        /// Live members that count down in seconds while the ship runs. One that fell by no more than the clock the ship
        /// ran between the before and after snapshots (<see cref="RoundTripResult.ShipSeconds"/>, plus a tick) is explained,
        /// since a countdown that started after the live control cannot be sorted live.
        /// </summary>
        private static readonly HashSet<string> LiveCountdowns = new(StringComparer.Ordinal)
        {
            "TimedDespawnComponent.Lifetime",
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
            if (CodecStopped is { } stopped)
                Assert.Ignore($"The codec-mode run stopped at {stopped}.");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await RungBody(rung, vesselId, clock);
            }
            catch when (CodecMode)
            {
                // Any failure stops a codec-mode run, not only the pool's error check at the end.
                CodecStopped ??= $"rung {rung} {vesselId}, which failed";
                throw;
            }
        }

        private async Task RungBody(int rung, string vesselId, System.Diagnostics.Stopwatch clock)
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapLoader = server.System<MapLoaderSystem>();

            CodecNotes.Clear();
            LoopFailures.Clear();
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
            string? doorPath = null;
            EntityUid? litMatch = null;
            await server.WaitPost(() => recipes = ApplyLivedIn(pair, grid, rung, out doorPath, out litMatch));

            await pair.RunTicksSync(PreSettleTicks);

            // Rooms are the tiles air flows between, which atmos sets (TileAtmosphere.AdjacentBits) only once it has
            // processed the hull, so the gas recipes wait for the settle and then settle on their own.
            var gasRooms = new List<(string Recipe, List<Vector2i> Tiles)>();
            await server.WaitPost(() => gasRooms = ApplyLivedInAtmos(pair, grid));
            recipes.AddRange(gasRooms.Select(room => room.Recipe));
            await pair.RunTicksSync((int) Math.Ceiling(AtmosSettleSeconds / server.ResolveDependency<IGameTiming>().TickPeriod.TotalSeconds));

            var first = await RoundTrip(pair, grid, owner, station, litMatch);
            var second = await RoundTrip(pair, first.Retrieved, owner, station, null);

            var sb = new StringBuilder();
            sb.AppendLine($"[ladder] rung {rung} {vesselId} through {(EngineMode ? "the engine serializer" : CodecMode ? "the grid image" : "the drydock")}");
            sb.AppendLine($"[ladder] lived-in recipes applied: {(recipes.Count == 0 ? "none" : string.Join(", ", recipes))}");
            var findingKinds = new HashSet<string>(StringComparer.Ordinal);
            var secondUnexplained = new List<string>();
            var findings = Report(sb, rung, vesselId, 1, first, EngineMode || CodecMode ? null : RetrieveGrants, gasRooms, doorPath, protoMan, findingKinds)
                           + Report(sb, rung, vesselId, 2, second, EngineMode || CodecMode ? null : RetrieveRestamps, gasRooms, doorPath, protoMan, findingKinds, secondUnexplained, previous: first);
            AppendShapes(sb, rung, vesselId, first, second, secondUnexplained);

            // A codec-mode run over many rungs stops on a rung that brings new kinds of finding faster than they can be
            // read, or one that failed, and the rest report themselves skipped rather than burying the stop under a
            // hundred more reports. The new kinds are printed whether or not they stop it: they are the run's product.
            if (CodecMode)
            {
                var seen = SeenFindingKinds(rung);
                var fresh = findingKinds.Where(kind => !seen.Contains(kind)).OrderBy(kind => kind, StringComparer.Ordinal).ToList();
                foreach (var note in CodecNotes)
                    sb.AppendLine(note);
                CodecNotes.Clear();

                sb.AppendLine($"[ladder] rung {rung} {vesselId}: {fresh.Count} new finding kind(s) against {seen.Count} seen on earlier rungs.");
                foreach (var kind in fresh)
                    sb.AppendLine($"[ladder-new] rung={rung} vessel={vesselId} kind={kind}");

                seen.UnionWith(findingKinds);
                if (fresh.Count > CodecStopNewKinds)
                    CodecStopped = $"rung {rung} {vesselId}, {fresh.Count} new finding kinds";
            }

            foreach (var note in CodecNotes)
                sb.AppendLine(note);
            sb.AppendLine($"[ladder] rung {rung} {vesselId}: {first.Before.Entities} entities, {findings} finding line(s), {clock.Elapsed.TotalSeconds:F1}s wall before cleanup");
            await TestContext.Out.WriteLineAsync(sb.ToString());

            Assert.That(first.Before.Entities, Is.GreaterThan(0), "The control: round trip 1 compared no entities.");
            Assert.That(second.Before.Entities, Is.GreaterThan(0), "The control: round trip 2 compared no entities.");

            await server.WaitPost(() => entMan.DeleteEntity(second.Retrieved));
            await pair.RunTicksSync(3);
            await pair.CleanReturnAsync();
            AssertLoopHeld();
        }

        /// <summary>
        /// A codec-mode run stops on a rung that brings more than this many finding kinds (verb and
        /// <c>Component.member</c>, as <see cref="StopKindOf"/> gives them) that no earlier rung had, or on any failure.
        /// Kinds rather than lines, because a known kind repeats with hull size and a line count stops the run on
        /// volume alone (rung 31, Stubby: 408 lines, 2 new kinds).
        /// </summary>
        private const int CodecStopNewKinds = 20;

        private static string? CodecStopped;

        private static HashSet<string>? _seenFindingKinds;

        /// <summary>
        /// The finding kinds earlier rungs had. Seeded once, from the dumps in <c>LADDER_DUMP</c> of the rungs before the
        /// first one this run takes, so a run resumed partway does not count every kind it meets as new. A dump holds the
        /// settling and predicted lines too, so the seed can hold a kind that was never a finding; that errs toward not
        /// stopping on it.
        /// </summary>
        private static HashSet<string> SeenFindingKinds(int rung)
        {
            if (_seenFindingKinds != null)
                return _seenFindingKinds;

            _seenFindingKinds = new HashSet<string>(StringComparer.Ordinal);
            var directory = Environment.GetEnvironmentVariable("LADDER_DUMP");
            if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory))
                return _seenFindingKinds;

            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "rung*_trip*.txt"))
            {
                var name = System.IO.Path.GetFileName(file);
                if (name.Length < 7 || !int.TryParse(name.AsSpan(4, 3), out var earlier) || earlier >= rung)
                    continue;

                foreach (var line in System.IO.File.ReadLines(file))
                {
                    if (line.Length > 0 && char.IsUpper(line[0]))
                        _seenFindingKinds.Add(StopKindOf(line));
                }
            }

            CodecNotes.Add($"[ladder] new-kind stop seeded with {_seenFindingKinds.Count} kind(s) from the dumps of rungs before {rung}.");
            return _seenFindingKinds;
        }

        /// <summary>
        /// Round trip 2's unexplained lines (findings and settling) against the same key on round trip 1, because a line
        /// that shows on both trips says nothing by itself about which way it is heading. <c>oscillates</c>: trip 2 goes
        /// back to trip 1's stored value (period two, as a sink's visual following whichever solution updated last).
        /// <c>repeats</c>: the same change both trips, a fixed loss re-applied. <c>compounds</c>: a number that takes trip
        /// 1's step again, or a collection that gains entries on both trips or loses an entry it had already lost, which is
        /// finding F36's signature, an init or startup handler applying a relative change to a persisted value (a grid's
        /// alerter list growing by one per restore); every one is listed.
        /// <c>trip2-only</c>: not on trip 1. Live values under load compound too, so a compounds line is read, not believed.
        /// </summary>
        private static void AppendShapes(StringBuilder sb, int rung, string vesselId, RoundTripResult first, RoundTripResult second, List<string> lines)
        {
            var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
            var compounding = new List<string>();

            foreach (var line in lines)
            {
                if (!line.StartsWith("CHANGED", StringComparison.Ordinal) || KeyOf(line) is not { } key || ShapeOf(key, first, second) is not { } shape)
                    continue;

                shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
                if (shape == Compounds)
                {
                    compounding.Add($"{key}: {OneLine(first.Before.Values.GetValueOrDefault(key) ?? "<absent>")} -> "
                                    + $"{OneLine(first.After.Values.GetValueOrDefault(key) ?? "<absent>")}, then {OneLine(second.After.Values[key])}");
                }
            }

            sb.AppendLine($"[ladder] round trip 2 against 1, {lines.Count} unexplained line(s): "
                          + (shapes.Count == 0 ? "none compared" : string.Join(", ", shapes.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key} {s.Value}"))) + ".");
            foreach (var line in compounding)
                sb.AppendLine($"[ladder-compounds] rung={rung} vessel={vesselId} {line}");
        }

        private const string Compounds = "compounds";

        /// <summary>
        /// A round trip 2 key's shape against round trip 1 (<see cref="AppendShapes"/> names each), or null when round trip
        /// 2 lacks it on either side.
        /// </summary>
        private static string? ShapeOf(string key, RoundTripResult first, RoundTripResult second)
        {
            if (!second.Before.Values.TryGetValue(key, out var b2) || !second.After.Values.TryGetValue(key, out var a2))
                return null;

            var b1 = first.Before.Values.GetValueOrDefault(key);
            var a1 = first.After.Values.GetValueOrDefault(key);

            if (b1 == null || a1 == null || a1 == b1)
                return "trip2-only";
            if (a2 == b1)
                return "oscillates";
            if (b2 == b1 && a2 == a1)
                return "repeats";
            // A collection that renders its entries is judged by them, and never by how many it has: a count alone cannot
            // tell one thing lost twice from two different things lost once each (ruled 2026-09-19).
            if (CollectionCompounds(b1, a1, b2, a2) is { } collection)
                return collection ? Compounds : "other";
            if (Number(b1) is { } nb1 && Number(a1) is { } na1 && Number(b2) is { } nb2 && Number(a2) is { } na2
                && SameStep(na1 - nb1, na2 - nb2))
                return Compounds;

            return "other";
        }

        /// <summary>
        /// Whether a collection compounds across the two trips, or null where either rendering does not list its entries.
        /// It compounds when it grows on both trips, and when it ends smaller after trip 2 than it ended after trip 1:
        /// each store leaving it worse off is growth per store read the other way. Both are measured as sizes, because an
        /// entry whose value changed is a different entry to a set and neither a gain nor a loss.
        ///
        /// <para>What that leaves out is a fixed loss re-applied: a UI cache that loses two states, has one of them filled
        /// again between the trips and loses it a second time ends empty both times and accumulates nothing (ruled
        /// 2026-09-19, on an air alarm that comes back unpowered for half a second and so misses its own refill).</para>
        /// </summary>
        private static bool? CollectionCompounds(string b1, string a1, string b2, string a2) =>
            CollectionGrew(b1, a1, b2, a2) is not { } grew
                ? null
                : grew || Entries(a2)!.Count < Entries(a1)!.Count;

        /// <summary>
        /// The growth arm of <see cref="CollectionCompounds"/> alone: a collection bigger after each trip than it was
        /// before it. Null where either rendering does not list its entries. Asked apart because growth holds a line back
        /// from every family, where ending smaller does not hold back a BUI state cache (ruled 2026-09-19).
        /// </summary>
        private static bool? CollectionGrew(string b1, string a1, string b2, string a2)
        {
            if (Entries(b1) is not { } before1 || Entries(a1) is not { } after1
                || Entries(b2) is not { } before2 || Entries(a2) is not { } after2)
            {
                return null;
            }

            return after1.Count > before1.Count && after2.Count > before2.Count;
        }

        /// <summary>
        /// A rendering's entries, from a <c>count=N [a, b]</c> mapping or a <c>- </c> list, or null where it is neither.
        /// An empty mapping is an empty set, not nothing: it lists its entries and has none.
        /// </summary>
        private static HashSet<string>? Entries(string render)
        {
            var open = render.IndexOf('[');
            var close = render.LastIndexOf(']');
            if (open >= 0 && close > open)
                return new HashSet<string>(render[(open + 1)..close].Split(", ", StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

            var lines = render.Split('\n')
                .Select(line => line.TrimStart())
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            return lines.Count > 0 ? lines : null;
        }

        /// <summary>
        /// Whether round trip 2 took round trip 1's step again: the same way by the same amount, within one per cent of it,
        /// or within an epsilon for a step too small for that. Growth per store is a step repeated, so a number that moves
        /// the same way to somewhere else each time is not it: a fizziness threshold re-rolled from nothing, or a heat
        /// signature still climbing, lands somewhere new every trip and is judged by its own class (ruled 2026-09-19).
        /// </summary>
        private static bool SameStep(double first, double second) =>
            Math.Sign(first) != 0
            && Math.Sign(second) == Math.Sign(first)
            && Math.Abs(second - first) <= Math.Max(Math.Abs(first) * 0.01, 1e-6);

        /// <summary>
        /// Whether a CHANGED line on round trip 2 compounds against round trip 1. That is growth per store, class 2 of the
        /// bar, a finding whatever explains its cause, so neither a family nor the registry absorbs it (ruled 2026-09-19);
        /// only the live bucket, which its own clock rule judges, still takes one. Round trip 1 has nothing to compound
        /// against, so <paramref name="previous"/> is null there.
        /// </summary>
        private static bool Compounding(string line, string key, RoundTripResult result, RoundTripResult? previous, KnownFamily? family = null)
        {
            if (previous == null || !line.StartsWith("CHANGED", StringComparison.Ordinal) || ShapeOf(key, previous, result) != Compounds)
                return false;

            // Ruled 2026-09-19: the guard has two arms and only one of them fits a BUI state cache. The cache is bounded
            // at empty and filled again at the open, by the system that owns each state or by the handler owed it, so a
            // cache that ends smaller after every store accumulates nothing and is not growth per store read the other
            // way. That is true of the member whatever family a line sorts into, which is why this is keyed by the member
            // and not by the family. Growth still holds a cache line back, as it holds back every line. Counted so the
            // exemption is visible in the report.
            if (SnapshotMember(key) != CacheMember
                || CollectionGrew(
                    previous.Before.Values.GetValueOrDefault(key) ?? string.Empty,
                    previous.After.Values.GetValueOrDefault(key) ?? string.Empty,
                    result.Before.Values.GetValueOrDefault(key) ?? string.Empty,
                    result.After.Values.GetValueOrDefault(key) ?? string.Empty) != false)
            {
                return true;
            }

            CacheLinesExempted++;
            return false;
        }

        /// <summary>The family for a cache every state of which is pushed at open, by name.</summary>
        private const string CacheFamily = "BUI state cache, pushed at open by the owning system";

        /// <summary>The member the ends-smaller arm does not hold back.</summary>
        private const string CacheMember = "UserInterfaceComponent.~States";

        /// <summary>
        /// How many cache lines that arm let through since the last report, printed by <see cref="Report"/> so an
        /// exemption nobody reads cannot grow quietly. Static because the ladder runs a rung at a time.
        /// </summary>
        private static int CacheLinesExempted;

        /// <summary>The family a line sorts into, and whether it was held back because it compounds (<see cref="Compounding"/>).</summary>
        private static (KnownFamily? Family, bool Compounds) FamilyFor(string line, string key, RoundTripResult result, RoundTripResult? previous) =>
            KnownFamilies.FirstOrDefault(f => f.Matches(line, key, result)) is { } family
                ? (family, Compounding(line, key, result, previous, family))
                : (null, false);

        /// <summary>
        /// Which sentinel a rendered time is, from its raw half (<c>raw|relative</c>): zero, the maximum or the minimum, which
        /// mean "never" or "not before the end of time" rather than a deadline, or null for an ordinary time. A move onto or
        /// off one is never a time that kept its meaning, whatever its relative half says: a zero that came back as the store's
        /// offset measured from the load clock has the same relative half and is a deadline already past (a disposal unit's
        /// next pressurisation, and a microwave's malfunction time that blew the microwave up).
        /// </summary>
        private static int? TimeSentinel(string render)
        {
            var bar = render.IndexOf('|');
            if (bar <= 0 || !double.TryParse(render.AsSpan(0, bar), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var raw))
            {
                return null;
            }

            const double end = 9.2e11; // TimeSpan.MaxValue is 922337203685.477 s.
            return raw == 0 ? 0 : raw >= end ? 1 : raw <= -end ? -1 : null;
        }

        /// <summary>
        /// A live time that went the wrong way across the round trip: it moved one way during the live window (a countdown
        /// falling) and the other way from before to after by more than the tolerance (the countdown back at its start). The
        /// window says what the clock does to it, so a move against that is the round trip, not the clock, and is a finding
        /// rather than live (a scuttle device's remaining time reset to full, 590.967 to 600.000). Times only: a live number
        /// under load (a battery, a supply ramp) turns round on its own.
        /// </summary>
        private static bool MovedAgainstItsWindow(string key, RoundTripResult result)
        {
            if (!key.EndsWith(DrydockFidelitySystem.TimeSuffix, StringComparison.Ordinal)
                || RawTime(result.Early.Values.GetValueOrDefault(key)) is not { } early
                || RawTime(result.Before.Values.GetValueOrDefault(key)) is not { } before
                || RawTime(result.After.Values.GetValueOrDefault(key)) is not { } after)
            {
                return false;
            }

            var window = Math.Sign(before - early);
            return window != 0 && Math.Sign(after - before) == -window && Math.Abs(after - before) > TimeToleranceSeconds;
        }

        /// <summary>The raw half of a rendered time (<c>raw|relative</c>), in seconds, or null for anything else.</summary>
        private static double? RawTime(string? render)
        {
            var bar = render?.IndexOf('|') ?? -1;
            return bar > 0 && double.TryParse(render.AsSpan(0, bar), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var raw)
                ? raw
                : null;
        }

        /// <summary>
        /// A line's kind as the new-kind stop counts it: <see cref="KindOf"/>, with a node network's label taken out. A
        /// network is named by its first member's path (DrydockFidelitySystem.DeepSnapshot.cs, RenderNetworks), so the
        /// same kind on another hull's pipe net would otherwise count as new every time.
        /// </summary>
        private static string StopKindOf(string line)
        {
            var kind = KindOf(line);
            foreach (var network in new[] { "PipeNetAir.", "NodeGroup." })
            {
                var at = kind.IndexOf(" " + network, StringComparison.Ordinal);
                if (at >= 0)
                    return kind[..(at + 1 + network.Length)] + "*";
            }

            return kind;
        }

        /// <summary>
        /// A codec-mode family: lines whose cause is read in code and cited, sorted apart so that a rung's findings are
        /// what nobody has explained. A line sorts only when its predicate holds; anything near it that does not stays
        /// a finding, and so does a line that compounds across the two trips, whatever its predicate (<see cref="FamilyFor"/>).
        /// </summary>
        private sealed record KnownFamily(string Name, string Receipt, Func<string, string, RoundTripResult, bool> Matches);

        private static readonly KnownFamily[] KnownFamilies = new KnownFamily[]
        {
            new("occluder-rewound",
                "Same vertices, other winding: the default polygon is clockwise (OccluderComponent.cs:26-32), the engine's "
                + "writer omits it as default (OccluderComponent.g.cs:249) but the codec writes it, and PhysicsHullSerializer "
                + "returns ComputePoints' counter-clockwise hull (PhysicsHullSerializer.cs:25). Its consumers ignore winding: "
                + "the renderer normalises it (Clyde.LightRendering.cs:1730-1736), shared edges key undirected "
                + "(ClientOccluderSystem.cs:324-334), bounds come from the vertices (OccluderSystem.cs:110). Not verified in a client.",
                (line, key, result) => line.StartsWith("CHANGED", StringComparison.Ordinal)
                                       && key.EndsWith("|OccluderComponent._polygon", StringComparison.Ordinal)
                                       && result.Before.Values.TryGetValue(key, out var before)
                                       && result.After.Values.TryGetValue(key, out var after)
                                       && SameVertexSet(before, after)),
            new("F23-trade-crate",
                "OWED, not explained: a trade crate's init re-draws its destination, re-sets its icon, relabels it and restarts "
                + "its express deadline (CargoSystem.TradeCrates.cs:53-81); the loader is to store the destination by prototype.",
                (_, key, result) => TradeCrateMembers.Contains(key[(key.IndexOf('|') + 1)..])
                                    && result.Before.Values.ContainsKey(key[..key.IndexOf('|')] + "|TradeCrateComponent.<present>")),

            new("F20-collection-master",
                "Same collection, other master: startup's anchor event and every node-group rebuild re-run "
                + "AssignEntityAsCollectionMaster (PowerMonitoringConsoleSystem.cs:191-200, :226-229, :742-818), and which device "
                + "becomes master is query order (F20). It only names the console's \"array of N\" row (join row 345, derived:startup). "
                + "Sorted only where the collection, master and children, is the same set of devices before and after.",
                (_, key, result) => CollectionMembers.Contains(key[(key.IndexOf('|') + 1)..])
                                    && Collection(result.Before.Values, key[..key.IndexOf('|')]) is { } before
                                    && Collection(result.After.Values, key[..key.IndexOf('|')]) is { } after
                                    && before.SetEquals(after)),

            // Owed to a rebuild handler that does not exist yet (resources/2026-09-18-rebuild-list.tsv), so each handler's
            // arrival has lines to delete. The load stays free of the old retrieve sweeps, which are what these replace.
            new("owed: H12",
                "OWED to H12, wire layout and timed wire re-arm: the state data is set by each wire's action as it is added "
                + "(WiresSystem.cs:143, :167, through SetData at :812), the statuses are refilled from the wires by "
                + "UpdateUserInterface (:528), and both run from map init (:469-488), which the silent map-init stamp never "
                + "raises. Sorted only where the same entity's wire list also came back empty, for a wire's own state and "
                + "for a UI state cache whose only unexplained loss is the wires state alike: nothing pushes that state at "
                + "open, and the H12 handler ends with WiresSystem.UpdateUserInterface (Surveyor's sweep, 2026-09-19).",
                (_, key, result) => (OwedToH12.Contains(key[(key.IndexOf('|') + 1)..]) || CacheWaitsOn(key, result) == "H12")
                                    && result.After.Values.TryGetValue(key[..key.IndexOf('|')] + "|WiresComponent.~WiresList", out var wires)
                                    && wires.StartsWith("count=0", StringComparison.Ordinal)),

            // What the manifest's fourth moment, after the first power solve, set back before it was cut (ruled 2026-09-19).
            new("re-armed by the power edge",
                "Accepted: the power edge a load raises re-arms a timer to its full delay or restarts an idle cycle, and nothing "
                + "is lost (Surveyor's consequence triage, 2026-09-19). An open door's auto-close to a full AutoCloseDelay "
                + "(AirlockSystem.cs:36-53, SharedAirlockSystem.cs:106-127); a fryer's next fry to a full FryInterval "
                + "(DeepFryerSystem.cs:481-486); an engaged disposal unit's flush through ManualEngage, which keeps the smaller "
                + "(SharedDisposalUnitSystem.cs:241-261, :686); a cargo telepad back to idle, its accumulator at its delay, "
                + "holding no order, since orders live in the station's database (CargoSystem.Telepad.cs:40-46, :131-152, :66-69).",
                (line, key, _) => line.StartsWith("CHANGED", StringComparison.Ordinal)
                                  && RearmedByThePowerEdge.Contains(key[(key.IndexOf('|') + 1)..])),

            // Ruled 2026-09-19 with the severed-reference rule: an invalid uid parked in a nullable member is a value the
            // codebase's own idiom cannot express, so the load reads it as null. The count is the point of the family.
            new("invalid in a nullable reads null",
                "Accepted: a reference the image could not keep reads null where its member is nullable (the codec's field "
                + "pass, FieldCase.Reference, amending F17 on 2026-09-19), so a member that already held an invalid uid at "
                + "the store comes back null. Sorted only where the stored value was invalid and the loaded one is null; a "
                + "reference that pointed at something and came back invalid is a severed reference, not this.",
                (line, key, result) => line.StartsWith("CHANGED", StringComparison.Ordinal)
                                       && result.Before.Values.TryGetValue(key, out var before)
                                       && before == "invalid"
                                       && result.After.Values.TryGetValue(key, out var after)
                                       && after == "null"),

            // Sorted by state type rather than by prototype (Surveyor's sweep, ruled 2026-09-19:
            // resources/2026-09-19-bui-state-cache-sweep.tsv, join row 427).
            new(CacheFamily,
                "Accepted: UserInterfaceComponent.States holds the last state the server sent each open interface, it is "
                + "not saved, and a load has no client with one open. A client opening a BUI calls UpdateState only where "
                + "the cache holds an entry (SharedUserInterfaceSystem.cs:1104-1121), so an empty cache is a blank window "
                + "unless the owning system pushes at open, and these systems do: an air alarm's ActivateInWorld handler "
                + "opens the interface, syncs its devices and calls UpdateUI in the same tick (AirAlarmSystem.cs:255-273); "
                + "a holopad fills its state on BeforeActivatableUIOpen (HolopadSystem.cs:52, :86-89, :467-498); a gas "
                + "mixer (GasMixerSystem.cs:151-155), a lathe (LatheSystem.cs:91) and a research client "
                + "(ResearchSystem.Client.cs:79-81) each push theirs. Sorted only where every state the cache lost is one "
                + "of those; a cache waiting on a handler sorts into that handler's family, and one waiting on two stays a "
                + "finding, because no single family explains it.",
                (line, key, result) => line.StartsWith("CHANGED", StringComparison.Ordinal) && CacheWaitsOn(key, result) == string.Empty),

            // The three the sweep found nothing pushing at open. Each sorts where its handler is the only one the cache
            // is waiting on; H21 is a new handler, the network configurator's own open push.
            new("owed: H10",
                "OWED to H10, the research client's re-link: a research console pushes its console state at open only "
                + "while its client is linked to a server, and the H10 re-link raises ResearchRegistrationChangedEvent, "
                + "which refills it (Surveyor's sweep, 2026-09-19). Sorted where the console state is the only one the "
                + "cache is waiting on.",
                (line, key, result) => line.StartsWith("CHANGED", StringComparison.Ordinal) && CacheWaitsOn(key, result) == "H10"),

            new("owed: H21",
                "OWED to H21, the network configurator's open push: nothing fills its list state at open, so a "
                + "configurator in a locker or a toolbox comes back with a blank window until H21 subscribes "
                + "BoundUIOpenedEvent to UpdateListUiState (spec resources/2026-09-18-handler-specs/"
                + "H21-network-configurator-open-push.md). Sorted where that state is the only one the cache is waiting on.",
                (line, key, result) => line.StartsWith("CHANGED", StringComparison.Ordinal) && CacheWaitsOn(key, result) == "H21"),

            // Ruled 2026-09-19, deliberately narrow: it may never absorb the state the load gets wrong.
            new("charge state caught up with a live battery",
                "Accepted: an APC recomputes its charge state at most once a second (ApcSystem.cs:151), so a battery that "
                + "crossed the 0.9 threshold in the last second before the store is stored a window behind itself, and the load "
                + "computes it fresh from the same battery, whose charge the registry already treats as live "
                + "(BatteryComponent.CurrentCharge, \"battery charge under load\"). Sorted only where the state settled, the "
                + "after value being the late one, and is the Full that the loaded battery's own charge gives under "
                + "CalcChargeState (ApcSystem.cs:197-209). Any other move, a Full the battery does not justify or the Lack of a "
                + "battery not yet synced among them, stays a finding. Proven by its control so far "
                + "(OnlyAChargeStateTheLoadedBatteryJustifiesSorts); no rung has handed it a line since the power fix.",
                (line, key, result) => line.StartsWith("CHANGED", StringComparison.Ordinal)
                                       && SnapshotMember(key) == "ApcComponent.~LastChargeState"
                                       && result.After.Values.TryGetValue(key, out var state)
                                       && state == "Full"
                                       && result.Late.Values.TryGetValue(key, out var late)
                                       && late == state
                                       && ChargeRatio(result.After.Values, key[..key.IndexOf('|')]) is { } ratio
                                       && ratio > ApcComponent.HighPowerThreshold),
        }.Concat(NotCarriedFamilies()).ToArray();

        /// <summary>
        /// How full the battery of the entity at <paramref name="path"/> is, from a snapshot's values, or null when it has
        /// no battery or no capacity. This is the ratio <see cref="ApcSystem"/> reads
        /// (<c>ApcSystem.cs:202</c>), taken from the component rather than the power solver's copy of it.
        /// </summary>
        private static float? ChargeRatio(Dictionary<string, string> values, string path) =>
            values.TryGetValue($"{path}|BatteryComponent.CurrentCharge", out var charge)
            && values.TryGetValue($"{path}|BatteryComponent.MaxCharge", out var capacity)
            && float.TryParse(charge, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var held)
            && float.TryParse(capacity, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var max)
            && max > 0
                ? held / max
                : null;

        /// <summary>
        /// A family for each member the manifest leaves out on purpose and marks as sorting so
        /// (<see cref="DrydockNotCarried.SortsAsNotCarried"/>), that entry's own reason its receipt. Opt-in per entry, never
        /// the whole list: a grid's alerter list is left out because a carried copy grows, and a line on it is F36.
        /// </summary>
        private static IEnumerable<KnownFamily> NotCarriedFamilies() =>
            DrydockCodecManifestMembers.NotCarried
                .Where(entry => entry.SortsAsNotCarried)
                .Select(entry => new KnownFamily(
                    $"not carried: {entry.Component}.{entry.Member}",
                    $"Not carried, by verdict (DrydockCodecManifestMembers.NotCarried, row {entry.Row}): {entry.Reason}.",
                    (line, key, _) => line.StartsWith("CHANGED", StringComparison.Ordinal)
                                      && SnapshotMember(key) is var member
                                      && (member == $"{entry.Component}Component.{entry.Member}" || member == $"{entry.Component}Component.~{entry.Member}")));

        /// <summary>
        /// Which handler a UI state cache is waiting on: the empty string where every state it lost is pushed at open and
        /// it waits on nothing, the handler's name where that handler is the only one it waits on, and null where it waits
        /// on two of them, where a state is one nobody has read yet, or where the line is not a cache at all. A line is one
        /// dictionary and sorts once, so a cache waiting on two handlers is explained by neither and stays a finding.
        /// </summary>
        private static string? CacheWaitsOn(string key, RoundTripResult result)
        {
            if (SnapshotMember(key) != "UserInterfaceComponent.~States"
                || !result.Before.Values.TryGetValue(key, out var before)
                || !result.After.Values.TryGetValue(key, out var after))
            {
                return null;
            }

            var waiting = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in LostStates(before, after))
            {
                if (PushedAtOpen.Contains(state))
                    continue;

                if (!OwedStates.TryGetValue(state, out var handler))
                    return null;

                waiting.Add(handler);
            }

            return waiting.Count switch
            {
                0 => string.Empty,
                1 => waiting.Single(),
                _ => null,
            };
        }

        /// <summary>
        /// The BUI states the owning system pushes at or before the open, so a cache that lost one is full by the time a
        /// player sees the interface (Surveyor's sweep, resources/2026-09-19-bui-state-cache-sweep.tsv).
        /// </summary>
        private static readonly HashSet<string> PushedAtOpen = new(StringComparer.Ordinal)
        {
            "AirAlarmUIState",
            "HolopadBoundInterfaceState",
            "GasMixerBoundUserInterfaceState",
            "LatheUpdateState",
            "ResearchClientBoundInterfaceState",
        };

        /// <summary>The BUI states nothing pushes at open, by the handler owed the push.</summary>
        private static readonly Dictionary<string, string> OwedStates = new(StringComparer.Ordinal)
        {
            ["ResearchConsoleBoundInterfaceState"] = "H10",
            ["WiresBoundUserInterfaceState"] = "H12",
            ["NetworkConfiguratorUserInterfaceState"] = "H21",
        };

        /// <summary>
        /// The state types a UI cache held before and no longer holds after, from the two renderings of it
        /// (<c>count=N [Key=&lt;StateType&gt;, ...]</c>). An entry whose state type changed counts as the old one lost.
        /// </summary>
        private static IEnumerable<string> LostStates(string before, string after)
        {
            var kept = Entries(after) ?? new HashSet<string>(StringComparer.Ordinal);
            return (Entries(before) ?? new HashSet<string>(StringComparer.Ordinal))
                .Where(entry => !kept.Contains(entry))
                .Select(entry => entry[(entry.IndexOf('<') + 1)..].TrimEnd('>'));
        }

        /// <summary>A key's <c>Component.member</c>, without the entity's path or the suffix a time takes.</summary>
        private static string SnapshotMember(string key)
        {
            var member = key[(key.IndexOf('|') + 1)..];
            return member.EndsWith(DrydockFidelitySystem.TimeSuffix, StringComparison.Ordinal)
                ? member[..^DrydockFidelitySystem.TimeSuffix.Length]
                : member;
        }

        /// <summary>
        /// The not-carried families' control, with no server: a difference on a member whose entry opts in sorts into that
        /// entry's family, a time as well; one on a member left out without opting in (a scuttle device's playing alert, a
        /// grid's alerter list) does not.
        /// </summary>
        [Test]
        public void OnlyANotCarriedEntryThatOptsInSortsItsLines()
        {
            var empty = new RoundTripResult(new DrydockStateSnapshot(), new DrydockStateSnapshot(), new DrydockStateSnapshot(),
                new DrydockStateSnapshot(), EntityUid.Invalid, 0, 0, 0, null);
            string? Sorted(string key) => FamilyFor($"CHANGED  {key}: before -> after", key, empty, null).Family?.Name;

            Assert.Multiple(() =>
            {
                Assert.That(Sorted("ScuttleDeviceWyvern@8,8|ScuttleDeviceComponent.~SelectedNukeSong"),
                    Is.EqualTo("not carried: ScuttleDevice.SelectedNukeSong"), "An opted-in member's line has to sort into its entry's family.");
                Assert.That(Sorted($"ScuttleDeviceWyvern@8,8|ScuttleDeviceComponent.~NukeSongLength{DrydockFidelitySystem.TimeSuffix}"),
                    Is.EqualTo("not carried: ScuttleDevice.NukeSongLength"), "A time's suffix must not keep it out of its family.");
                Assert.That(Sorted("ScuttleDeviceWyvern@8,8|ScuttleDeviceComponent.~PlayedNukeSong"),
                    Is.EqualTo("not carried: ScuttleDevice.PlayedNukeSong"), "The played flag opts in with the rest of the music state.");
                Assert.That(Sorted("ScuttleDeviceWyvern@8,8|ScuttleDeviceComponent.~AlertAudioStream"), Is.Null,
                    "A member left out without opting in has to stay a finding.");
                Assert.That(Sorted("grid|TargetSeekerAlertGridComponent.Alerters"), Is.Null,
                    "The alerter list, left out because a carried copy grows, has to stay a finding.");
            });
        }

        /// <summary>
        /// The charge-state family's control, with no server: a state that settled on the Full the loaded battery's own
        /// charge gives sorts into it, and nothing else does. The line this family may never take is the one the load used
        /// to get wrong, a full battery reading Lack.
        /// </summary>
        [Test]
        public void OnlyAChargeStateTheLoadedBatteryJustifiesSorts()
        {
            const string apc = "APCBasic@1,-10";
            const string state = $"{apc}|ApcComponent.~LastChargeState";

            string? Sorted(string after, string late, float charge)
            {
                var result = new RoundTripResult(new DrydockStateSnapshot(), new DrydockStateSnapshot(), new DrydockStateSnapshot(),
                    new DrydockStateSnapshot(), EntityUid.Invalid, 0, 0, 0, null);
                result.After.Values[state] = after;
                result.Late.Values[state] = late;
                result.After.Values[$"{apc}|BatteryComponent.CurrentCharge"] = charge.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result.After.Values[$"{apc}|BatteryComponent.MaxCharge"] = "50000";
                return FamilyFor($"CHANGED  {state}: Charging -> {after}", state, result, null).Family?.Name;
            }

            Assert.Multiple(() =>
            {
                Assert.That(Sorted("Full", "Full", 50000), Is.EqualTo("charge state caught up with a live battery"),
                    "A state that settled on the Full the loaded battery gives has to sort.");
                Assert.That(Sorted("Lack", "Lack", 50000), Is.Null,
                    "The Lack of a battery not yet synced has to stay a finding, whatever the battery holds.");
                Assert.That(Sorted("Full", "Full", 25000), Is.Null,
                    "A Full the battery's own charge does not give has to stay a finding.");
                Assert.That(Sorted("Full", "Charging", 50000), Is.Null,
                    "A state still moving at the late snapshot has to stay a finding.");
            });
        }

        /// <summary>What the power edge a load raises re-arms or restarts, as deep-snapshot members.</summary>
        private static readonly HashSet<string> RearmedByThePowerEdge = new(StringComparer.Ordinal)
        {
            $"DoorComponent.~NextStateChange{DrydockFidelitySystem.TimeSuffix}",
            $"DeepFryerComponent.NextFryTime{DrydockFidelitySystem.TimeSuffix}",
            $"DisposalUnitComponent.NextFlush{DrydockFidelitySystem.TimeSuffix}",
            "CargoTelepadComponent.CurrentState",
            "CargoTelepadComponent.Accumulator",
            "Appearance.CargoTelepadVisuals.State",
        };

        /// <summary>
        /// The UI cache families' control, with no server: a cache sorts by the state types it lost, into the family where
        /// every one of them is pushed at open, into the family of the one handler it waits on where there is exactly one,
        /// and nowhere at all where it waits on two or where a state is one nobody has read yet. The wire list's own
        /// condition is not asked of a cache line, which the handler's push cures whatever the list did.
        /// </summary>
        [Test]
        public void OnlyAUiCacheWhoseLostStatesAreExplainedSorts()
        {
            const string path = "AirAlarm@-1,3";
            const string key = $"{path}|UserInterfaceComponent.~States";
            const string alarm = "count=1 [Key=<AirAlarmUIState>]";
            const string alarmAndWires = "count=2 [Key=<AirAlarmUIState>, Key=<WiresBoundUserInterfaceState>]";
            const string console = "count=2 [Key=<ResearchConsoleBoundInterfaceState>, Key=<WiresBoundUserInterfaceState>]";
            const string configurator = "count=1 [List=<NetworkConfiguratorUserInterfaceState>]";
            const string nothing = "count=0 []";

            string? Sorted(string before, string after, string wires)
            {
                var result = new RoundTripResult(new DrydockStateSnapshot(), new DrydockStateSnapshot(), new DrydockStateSnapshot(),
                    new DrydockStateSnapshot(), EntityUid.Invalid, 0, 0, 0, null);
                result.Before.Values[key] = before;
                result.After.Values[key] = after;
                result.After.Values[$"{path}|WiresComponent.~WiresList"] = wires;
                return FamilyFor($"CHANGED  {key}: {before} -> {after}", key, result, null).Family?.Name;
            }

            Assert.Multiple(() =>
            {
                Assert.That(Sorted(alarm, nothing, nothing), Is.EqualTo("BUI state cache, pushed at open by the owning system"),
                    "A cache that lost only states the owning system pushes at open has to sort into the family.");
                Assert.That(Sorted(alarmAndWires, alarm, nothing), Is.EqualTo("owed: H12"),
                    "One waiting on the wires state alone is H12's.");
                Assert.That(Sorted(alarmAndWires, nothing, nothing), Is.EqualTo("owed: H12"),
                    "So is one that lost a pushed state beside it, since only the wires state is waiting.");
                Assert.That(Sorted(configurator, nothing, nothing), Is.EqualTo("owed: H21"),
                    "A configurator's list state is H21's until that handler pushes it.");
                Assert.That(Sorted(console, nothing, nothing), Is.Null,
                    "A cache waiting on two handlers, H10 for the console state and H12 for the wires, is explained by neither.");
                Assert.That(Sorted(alarmAndWires, nothing, "count=2 [- a, - b]"), Is.Null,
                    "With the wires themselves back, H12's list condition fails and the line stays a finding, for now.");
                Assert.That(Sorted("count=1 [Key=<FireControlConsoleBoundInterfaceState>]", nothing, nothing), Is.Null,
                    "A state nobody has read yet keeps the line a finding.");
            });
        }

        /// <summary>
        /// The cache's exemption from the guard's ends-smaller arm, with no server: a cache that ends smaller after every
        /// store stays with its family, because it is bounded at empty and filled again at the open; one that grows on both
        /// trips is held back like any other line; and a line that is not a cache is held back by the same arm the cache is
        /// exempt from (ruled 2026-09-19).
        /// </summary>
        [Test]
        public void OnlyTheCacheIsExemptFromTheEndsSmallerArm()
        {
            const string path = "AirAlarm@-1,3";
            const string cache = $"{path}|UserInterfaceComponent.~States";
            const string statuses = $"{path}|WiresComponent.~Statuses";

            RoundTripResult Trip(string key, string before, string after)
            {
                var stored = new DrydockStateSnapshot();
                var loaded = new DrydockStateSnapshot();
                stored.Values[key] = before;
                loaded.Values[key] = after;
                loaded.Values[$"{path}|WiresComponent.~WiresList"] = "count=0 []";
                return new RoundTripResult(new DrydockStateSnapshot(), stored, loaded, loaded, EntityUid.Invalid, 0, 0, 0, null);
            }

            (KnownFamily? Family, bool Held) Sorted(string key, RoundTripResult first, RoundTripResult second) =>
                FamilyFor($"CHANGED  {key}: before -> after", key, second, first);

            var cacheFamily = KnownFamilies.Single(family => family.Name == CacheFamily);
            var h12 = KnownFamilies.Single(family => family.Name == "owed: H12");

            var drains = Sorted(cache,
                Trip(cache, "count=2 [Key=<AirAlarmUIState>, InteractionWindow=<HolopadBoundInterfaceState>]", "count=1 [Key=<AirAlarmUIState>]"),
                Trip(cache, "count=1 [Key=<AirAlarmUIState>]", "count=0 []"));

            var grows = Sorted(cache,
                Trip(cache, "count=0 []", "count=1 [Key=<AirAlarmUIState>]"),
                Trip(cache, "count=1 [Key=<AirAlarmUIState>]", "count=2 [Key=<AirAlarmUIState>, InteractionWindow=<HolopadBoundInterfaceState>]"));

            var wires = Sorted(statuses,
                Trip(statuses, "- a\n- b\n- c", "- a\n- b"),
                Trip(statuses, "- a\n- b", "- a"));

            Assert.Multiple(() =>
            {
                Assert.That(drains, Is.EqualTo((cacheFamily, false)), "A cache that ends smaller each store stays with its family.");
                Assert.That(grows, Is.EqualTo((cacheFamily, true)), "One that grows on both trips is held back, as every family's line is.");
                Assert.That(wires, Is.EqualTo((h12, true)), "And a line that is not a cache is still held back by the arm the cache is exempt from.");
            });
        }

        /// <summary>
        /// The compounds shape's control, with no server: growth per store is a step taken again, so a value that takes the
        /// same step twice compounds and one that lands somewhere new each time does not, however far it goes the same way.
        /// A pre-rolled fizziness threshold is the second of those, and its own class judges it. A collection is judged by
        /// where it ends instead: growing on both trips compounds, so does ending smaller after trip 2 than after trip 1,
        /// and a loss re-applied to the same size does not (ruled 2026-09-19).
        /// </summary>
        [Test]
        public void OnlyAStepTakenAgainCompounds()
        {
            const string key = "SodaDispenser@-3,10|PressurizedSolutionComponent.SprayFizzinessThresholdRoll";

            string? Shape(string b1, string a1, string b2, string a2)
            {
                RoundTripResult Trip(string before, string after)
                {
                    var stored = new DrydockStateSnapshot();
                    var loaded = new DrydockStateSnapshot();
                    stored.Values[key] = before;
                    loaded.Values[key] = after;
                    return new RoundTripResult(new DrydockStateSnapshot(), stored, loaded, loaded, EntityUid.Invalid, 0, 0, 0, null);
                }

                return ShapeOf(key, Trip(b1, a1), Trip(b2, a2));
            }

            Assert.Multiple(() =>
            {
                Assert.That(Shape("1", "2", "2", "3"), Is.EqualTo(Compounds), "A value that takes the same step again compounds.");
                Assert.That(Shape("100", "101", "101", "102.005"), Is.EqualTo(Compounds), "A step within one per cent of the first is that step again.");
                Assert.That(Shape("3", "2", "2", "1"), Is.EqualTo(Compounds), "A step down taken again compounds as well.");
                Assert.That(Shape("0.18811038", "0.37616146", "0.37616146", "0.71269476"), Is.EqualTo("other"),
                    "A number re-rolled to somewhere new each time does not compound, though it moved the same way.");
                Assert.That(Shape("- a", "- a\n- b", "- a\n- b", "- a\n- b\n- c"), Is.EqualTo(Compounds),
                    "A list that gains entries on both trips compounds.");
                Assert.That(Shape("- a", "- a\n- b", "- a\n- b", "- a\n- b\n- c\n- d"), Is.EqualTo(Compounds),
                    "It compounds whatever it gains, and however many: growth per store is growth.");
                Assert.That(Shape("count=3 [A, B, C]", "count=2 [B, C]", "count=2 [B, C]", "count=1 [C]"), Is.EqualTo(Compounds),
                    "A collection that ends smaller after each trip is growth per store read the other way.");
                Assert.That(Shape("count=2 [A, B]", "count=0 []", "count=1 [B]", "count=0 []"), Is.EqualTo("other"),
                    "One that ends empty both times, having had an entry filled in between, is a fixed loss re-applied.");
                Assert.That(Shape("count=1 [A=1]", "count=1 [A=2]", "count=1 [A=3]", "count=1 [A=4]"), Is.EqualTo("other"),
                    "An entry whose value moved is a different entry to a set and no gain at all, so it does not compound.");
            });
        }

        /// <summary>
        /// The no-growth guard's control, with no server: a wire panel's statuses sort into the H12 family on round trip 1,
        /// where there is nothing to compound against, and on round trip 2 when they repeat the same change, but not when
        /// they grow again.
        /// </summary>
        [Test]
        public void AFamilyNeverAbsorbsALineThatCompounds()
        {
            const string path = "AirlockGlass@1,1";
            const string key = path + "|WiresComponent.~Statuses";
            const string line = "CHANGED  " + key + ": before -> after";

            RoundTripResult Trip(string before, string after)
            {
                var stored = new DrydockStateSnapshot();
                var loaded = new DrydockStateSnapshot();
                stored.Values[key] = before;
                loaded.Values[key] = after;
                loaded.Values[path + "|WiresComponent.~WiresList"] = "count=0 []";
                return new RoundTripResult(new DrydockStateSnapshot(), stored, loaded, new DrydockStateSnapshot(), EntityUid.Invalid, 0, 0, 0, null);
            }

            var first = Trip("- a", "- a\n- b");
            var repeats = Trip("- a", "- a\n- b");
            var grows = Trip("- a\n- b", "- a\n- b\n- c");
            var h12 = KnownFamilies.Single(family => family.Name == "owed: H12");

            Assert.Multiple(() =>
            {
                Assert.That(FamilyFor(line, key, first, null), Is.EqualTo((h12, false)), "The control: on round trip 1 the family takes the line.");
                Assert.That(FamilyFor(line, key, repeats, first), Is.EqualTo((h12, false)), "A line repeating round trip 1's change is the family's.");
                Assert.That(FamilyFor(line, key, grows, first), Is.EqualTo((h12, true)), "A line growing again on round trip 2 must be held back from the family.");
            });
        }

        /// <summary>
        /// The no-growth guard on the registry, through the report itself: a line the registry classifies as derived (a gas
        /// canister's pressure cache) stays classified on round trip 1 and when it repeats, and is a finding when it grows
        /// again on round trip 2. The server is only for the prototypes the report reads.
        /// </summary>
        [Test]
        public async Task TheRegistryNeverAbsorbsALineThatCompounds()
        {
            await using var pair = await PoolManager.GetServerClient();
            var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
            const string key = "GasCanister@0,0|GasCanisterComponent.~LastPressure";

            // Early as before, so the key is not live and reaches the registry.
            RoundTripResult Trip(string before, string after)
            {
                var early = new DrydockStateSnapshot();
                var stored = new DrydockStateSnapshot();
                var loaded = new DrydockStateSnapshot();
                early.Values[key] = before;
                stored.Values[key] = before;
                loaded.Values[key] = after;
                return new RoundTripResult(early, stored, loaded, loaded, EntityUid.Invalid, 0, 0, 0, null);
            }

            int Findings(RoundTripResult result, RoundTripResult? previous) =>
                Report(new StringBuilder(), 0, "control", previous == null ? 1 : 2, result, null,
                    new List<(string Recipe, List<Vector2i> Tiles)>(), null, protoMan, new HashSet<string>(StringComparer.Ordinal), previous: previous);

            var first = Trip("1", "2");
            var repeats = Trip("1", "2");
            var grows = Trip("2", "3");

            Assert.Multiple(() =>
            {
                Assert.That(Registry.ContainsKey("GasCanisterComponent.~LastPressure"), Is.True, "The control's control: the key has to be the registry's.");
                Assert.That(Findings(first, null), Is.Zero, "The control: on round trip 1 the registry classifies the line.");
                Assert.That(Findings(repeats, first), Is.Zero, "A line repeating round trip 1's change stays classified.");
                Assert.That(Findings(grows, first), Is.EqualTo(1), "A line growing again on round trip 2 has to be a finding.");
            });

            await pair.CleanReturnAsync();
        }

        private const string CollectionMasterMember = "PowerMonitoringDeviceComponent.~CollectionMaster";
        private const string ChildDevicesMember = "PowerMonitoringDeviceComponent.~ChildDevices";

        private static readonly HashSet<string> CollectionMembers = new(StringComparer.Ordinal) { CollectionMasterMember, ChildDevicesMember };

        /// <summary>
        /// The power-monitoring collection a device belongs to, as a set of device paths: its master and the master's
        /// children. Null when the device names no master, or the master's children are not in the snapshot.
        /// </summary>
        private static HashSet<string>? Collection(IReadOnlyDictionary<string, string> values, string path)
        {
            if (!values.TryGetValue($"{path}|{CollectionMasterMember}", out var master) || master is "null" or "<absent>" || master.Length == 0)
                return null;

            if (!values.TryGetValue($"{master}|{ChildDevicesMember}", out var children))
                return null;

            var set = new HashSet<string>(StringComparer.Ordinal) { master };
            var open = children.IndexOf('[');
            var close = children.LastIndexOf(']');
            if (open < 0 || close <= open)
                return null;

            foreach (var entry in children[(open + 1)..close].Split(", ", StringSplitOptions.RemoveEmptyEntries))
            {
                var at = entry.IndexOf('=');
                set.Add(at < 0 ? entry : entry[..at]);
            }

            return set;
        }

        /// <summary>What H12's layout rebuild fills besides the wire list, which the census predicts on its own (join row 434).</summary>
        private static readonly HashSet<string> OwedToH12 = new(StringComparer.Ordinal)
        {
            "WiresComponent.~StateData",
            "WiresComponent.~Statuses",
        };

        /// <summary>What <c>OnTradeCrateInit</c> rewrites (CargoSystem.TradeCrates.cs:58-80), as deep-snapshot members.</summary>
        private static readonly HashSet<string> TradeCrateMembers = new(StringComparer.Ordinal)
        {
            "TradeCrateComponent.~DestinationStation",
            $"TradeCrateComponent.~ExpressDeliveryTime{DrydockFidelitySystem.TimeSuffix}",
            "Appearance.TradeCrateVisuals.DestinationIcon",
            "Appearance.TradeCrateVisuals.IsPriority",
            "LabelComponent.CurrentLabel",
            "MetaDataComponent.name",
        };

        /// <summary>Two polygon renders (one <c>- x,y</c> per vertex) holding the same vertices in any order.</summary>
        private static bool SameVertexSet(string before, string after)
        {
            static List<string> Vertices(string render) => render.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .Select(line => line[2..].Trim())
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList();

            var a = Vertices(before);
            var b = Vertices(after);
            return a.Count >= 3 && a.SequenceEqual(b);
        }

        /// <summary>
        /// Puts the hull into states a lived-in ship has and a shuttle file does not: damage on a wall and
        /// a thruster, cargo in a closed locker, a loose item, a wheelchair (a <c>save: false</c> vehicle) and a trade crate
        /// with an express deadline on the deck, a lit cigarette and a match beside their unlit controls and a match left to
        /// burn out, an interior door open,
        /// a wires panel open, a gravity generator switched off, a lathe queue
        /// (not in engine mode, which cannot write one), a restocked vendor, and a sidearm fired. Each recipe takes the first
        /// matching entity in tree order (the deck spot is a chair, else a computer) and is skipped when
        /// the hull has none. Returns the ones applied, the path the detector names the recipe's door by, and the match
        /// <see cref="RoundTrip"/> lights in the tick the store is claimed.
        ///
        /// <para>The three toggles (the door, the panel, gravity) run in both polarities, flipped on an even rung and left
        /// as the hull came on an odd one, because a recipe that only ever turns a thing off hides every defect that brings
        /// it back off: gravity switched off on every rung is how a restored ship coming back without gravity went unseen
        /// (GravitySystem.cs:27-55). The polarity is in the recipe's name either way.</para>
        /// </summary>
        private static List<string> ApplyLivedIn(TestPair pair, EntityUid grid, int rung, out string? doorPath, out EntityUid? litMatch)
        {
            var flip = rung % 2 == 0;
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var tree = server.System<DrydockFidelitySystem>().GridTreeList(grid);
            var applied = new List<string>();
            doorPath = null;
            litMatch = null;

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

            if (chair is { } crateSpot)
            {
                // A trade crate draws its destination and express deadline at component init. Four destinations off the
                // grid, each on its own prototype, make a re-draw show in the path the crate's destination renders as.
                var mapId = entMan.GetComponent<TransformComponent>(grid).MapID;
                var destinations = new[] { ("Crowbar", "CargoA"), ("Wrench", "CargoB"), ("Screwdriver", "CargoC"), ("Wirecutter", "Beacon") };
                for (var i = 0; i < destinations.Length; i++)
                {
                    var marker = entMan.SpawnEntity(destinations[i].Item1, new MapCoordinates(new System.Numerics.Vector2(5000f + i * 10f, 5000f), mapId));
                    entMan.AddComponent(marker, new TradeCrateDestinationComponent { DestinationProto = destinations[i].Item2 });
                }

                entMan.SpawnEntity("CrateTradeSecureNormal", entMan.GetComponent<TransformComponent>(crateSpot).Coordinates);
                applied.Add("trade-crate");
            }

            if (chair is { } smokingSpot)
            {
                // A lit smokable burns its own solution down (SmokingSystem.cs:151-156), which the store saves, so it
                // is lit here and still lit at the store: 10 u of nicotine at 0.05 u/s. A match burns out on a timer
                // instead (MatchstickSystem.cs:91-98) and lasts 10 s, shorter than the settle, so the match is lit in
                // the tick the store is claimed (<see cref="RoundTrip"/>). The unlit pair beside them is the control.
                var spot = entMan.GetComponent<TransformComponent>(smokingSpot).Coordinates;
                litMatch = entMan.SpawnEntity("Matchstick", spot.Offset(new System.Numerics.Vector2(-0.2f, -0.2f)));
                entMan.SpawnEntity("Matchstick", spot.Offset(new System.Numerics.Vector2(0.2f, -0.2f)));
                applied.Add("match-lit-at-store");

                // The control for the burn-out itself: a match lit here has the whole settle to burn out, so it must
                // read Burnt before the store. If it does not, no burn-out timer fired in this harness at all and the
                // lit match coming back lit proves nothing.
                var burning = entMan.SpawnEntity("Matchstick", spot.Offset(new System.Numerics.Vector2(0f, -0.2f)));
                server.System<MatchstickSystem>().Ignite((burning, entMan.GetComponent<MatchstickComponent>(burning)), burning);
                applied.Add("match-burns-out-control");

                var cigarette = entMan.SpawnEntity("Cigarette", spot.Offset(new System.Numerics.Vector2(-0.2f, 0.2f)));
                entMan.SpawnEntity("Cigarette", spot.Offset(new System.Numerics.Vector2(0.2f, 0.2f)));
                server.System<SmokingSystem>().SetSmokableState(cigarette, SmokableState.Lit);
                applied.Add("light-cigarette");
            }

            var door = First((uid, id) => id.StartsWith("Airlock", StringComparison.Ordinal)
                                          && !id.Contains("Shuttle", StringComparison.Ordinal)
                                          && !id.Contains("External", StringComparison.Ordinal)
                                          && entMan.HasComponent<DoorComponent>(uid));
            if (door is { } interior)
            {
                if (flip)
                {
                    // TryOpen refuses an unpowered or bolted door; forcing the open is still a state a door can be stored in.
                    var doors = server.System<SharedDoorSystem>();
                    if (!doors.TryOpen(interior))
                        doors.StartOpening(interior);
                    applied.Add("open-door");
                }
                else
                {
                    applied.Add("door-left-closed");
                }

                // The detector names a direct child of the grid by prototype and tile (DrydockFidelitySystem.DeepPaths).
                var xform = entMan.GetComponent<TransformComponent>(interior);
                if (xform.ParentUid == grid)
                {
                    var proto = entMan.GetComponent<MetaDataComponent>(interior).EntityPrototype!.ID;
                    doorPath = $"{proto}@{(int) MathF.Floor(xform.LocalPosition.X)},{(int) MathF.Floor(xform.LocalPosition.Y)}";
                }
            }

            if (First((uid, _) => uid != door && entMan.HasComponent<WiresPanelComponent>(uid)) is { } paneled)
            {
                if (!flip)
                    applied.Add("panel-left-closed");
                else if (server.System<SharedWiresSystem>().TogglePanel(paneled, entMan.GetComponent<WiresPanelComponent>(paneled), true))
                    applied.Add("open-panel");
            }

            if (First((uid, id) => id.StartsWith("GravityGenerator", StringComparison.Ordinal)
                                   && entMan.HasComponent<PowerChargeComponent>(uid)) is { } gravity)
            {
                if (flip)
                {
                    // The console's switch message: the only public path to a charged machine's on switch.
                    entMan.EventBus.RaiseLocalEvent(gravity, new SwitchChargingMachineMessage(false));
                    applied.Add("gravity-off");
                }
                else
                {
                    applied.Add("gravity-left-on");
                }
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

        /// <param name="ShipSeconds">The clock the ship ran between the before and after snapshots: the ticks its grid
        /// ended unpaused, the old grid through the store and the new one through the retrieve and the settle.</param>
        /// <param name="TickSeconds">One tick, the slack for the tick a grid was unpaused partway through.</param>
        private sealed record RoundTripResult(
            DrydockStateSnapshot Early,
            DrydockStateSnapshot Before,
            DrydockStateSnapshot After,
            DrydockStateSnapshot Late,
            EntityUid Retrieved,
            double ElapsedSeconds,
            double ShipSeconds,
            double TickSeconds,
            DrydockMapInitReport? MapInit);

        /// <param name="lightAtClaim">A match lit in the tick the store is claimed, so its whole 10 s burn is ahead of
        /// the store: lighting it any earlier burns it out before the snapshot, since the settle is longer than that.</param>
        private static async Task<RoundTripResult> RoundTrip(TestPair pair, EntityUid grid, Guid owner, EntityUid station, EntityUid? lightAtClaim)
        {
            var server = pair.Server;
            var timing = server.ResolveDependency<IGameTiming>();
            var fidelity = server.System<DrydockFidelitySystem>();

            DrydockStateSnapshot early = default!;
            await server.WaitPost(() => early = fidelity.DeepSnapshotGrid(grid, TieBreakBefore(server.EntMan, grid)));
            await pair.RunTicksSync(LiveWindowTicks);

            DrydockStateSnapshot before = default!;
            await server.WaitPost(() =>
            {
                if (lightAtClaim is { } match && server.EntMan.TryGetComponent<MatchstickComponent>(match, out var stick))
                    server.System<MatchstickSystem>().Ignite((match, stick), match);

                before = fidelity.DeepSnapshotGrid(grid, TieBreakBefore(server.EntMan, grid));
            });

            var clockBefore = timing.CurTime;
            var (retrieved, mapInit, shipTicks) = EngineMode
                ? (await EngineRoundTrip(pair, grid), null, 0)
                : CodecMode
                    ? (await CodecRoundTrip(pair, grid), null, 0)
                    : await DrydockRoundTrip(pair, grid, owner, station);

            await pair.RunTicksSync(SettleTicks);

            DrydockStateSnapshot after = default!;
            var running = false;
            await server.WaitPost(() =>
            {
                after = fidelity.DeepSnapshotGrid(retrieved, TieBreakAfter());
                running = !server.EntMan.GetComponent<MetaDataComponent>(retrieved).EntityPaused;
            });
            var elapsed = (timing.CurTime - clockBefore).TotalSeconds;
            var tickSeconds = timing.TickPeriod.TotalSeconds;

            // An engine round trip saves and loads between ticks, so the settle is all the clock its ship runs.
            var shipSeconds = (shipTicks + (running ? SettleTicks : 0)) * tickSeconds;

            await pair.RunTicksSync((int) Math.Ceiling(LateSeconds / timing.TickPeriod.TotalSeconds));

            DrydockStateSnapshot late = default!;
            await server.WaitPost(() => late = fidelity.DeepSnapshotGrid(retrieved, TieBreakAfter()));

            return new RoundTripResult(early, before, after, late, retrieved, elapsed, shipSeconds, tickSeconds, mapInit);
        }

        /// <returns>The retrieved grid, the map-init report, and the ticks the ship's grid ended unpaused
        /// during the store and the retrieve.</returns>
        private static async Task<(EntityUid Grid, DrydockMapInitReport? MapInit, int ShipTicks)> DrydockRoundTrip(
            TestPair pair,
            EntityUid grid,
            Guid owner,
            EntityUid station)
        {
            var server = pair.Server;
            var timing = server.ResolveDependency<IGameTiming>();
            var drydock = server.System<DrydockSystem>();
            var ran = new Dictionary<EntityUid, int>();

            var (storeResult, shipId) = await PumpCountingGridTicks(pair,
                () => drydock.TryStoreShip(grid, owner, null), ran);
            Assert.That(storeResult, Is.EqualTo(DrydockStoreResult.Success), "store refused.");

            await pair.RunTicksSync((int) Math.Ceiling(ClockGapSeconds / timing.TickPeriod.TotalSeconds));

            var retrieved = await PumpCountingGridTicks(pair,
                () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null), ran);
            Assert.That(retrieved.Succeeded, Is.True, $"retrieve failed with {retrieved.Result}.");

            var shipTicks = ran.GetValueOrDefault(grid) + ran.GetValueOrDefault(retrieved.Grid!.Value);
            return (retrieved.Grid!.Value, server.System<DrydockFidelitySystem>().LastMapInitReport, shipTicks);
        }

        /// <summary>
        /// Runs a server-side operation as <see cref="DrydockTestHelpers.RunOnServer{T}"/> does, a tick at a time
        /// under the same wall-clock bound, and adds to <paramref name="ran"/> each grid that ended a tick unpaused.
        /// </summary>
        private static async Task<T> PumpCountingGridTicks<T>(TestPair pair, Func<Task<T>> start, Dictionary<EntityUid, int> ran)
        {
            var server = pair.Server;
            Task<T>? task = null;
            await server.WaitPost(() => task = start());

            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!task!.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await pair.RunTicksSync(1);
                await server.WaitPost(() =>
                {
                    var grids = server.EntMan.AllEntityQueryEnumerator<MapGridComponent, MetaDataComponent>();
                    while (grids.MoveNext(out var uid, out _, out var meta))
                    {
                        if (!meta.EntityPaused)
                            ran[uid] = ran.GetValueOrDefault(uid) + 1;
                    }
                });
            }

            Assert.That(task!.IsCompleted, Is.True, "The drydock operation never completed.");
            return await task;
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

        /// <returns>The round trip's finding lines, the count a codec-mode run stops on.</returns>
        private static int Report(
            StringBuilder sb,
            int rung,
            string vesselId,
            int trip,
            RoundTripResult result,
            HashSet<string>? grants,
            List<(string Recipe, List<Vector2i> Tiles)> gasRooms,
            string? doorPath,
            IPrototypeManager protoMan,
            HashSet<string> findingKinds,
            List<string>? unexplained = null,
            RoundTripResult? previous = null)
        {
            // A time's render carries its clock-relative half, which moves every tick, so a time key is live only when its
            // meaning moved during the window; any other key is live when its rendered value moved.
            var live = DrydockStateSnapshot.Diff(result.Early, result.Before)
                .Select(KeyOf)
                .Where(k => k != null && !TimeHeldDuringWindow(k, result))
                .ToHashSet();

            var findings = new List<string>();
            var liveLines = new List<string>();
            var grantLines = new List<string>();
            var policyLines = new List<string>();
            var unsavedLines = new List<string>();
            var recreatedLines = new List<string>();
            var classifiedLines = new List<string>();
            var belowFloorLines = new List<string>();
            var settledLines = new List<string>();
            var settlingLines = new List<string>();
            var movedLines = new List<string>();
            var predictedLines = new List<string>();
            var predictedSeen = new HashSet<PredictedRow>();
            var familyLines = new Dictionary<KnownFamily, List<string>>();
            var familyGrew = new Dictionary<KnownFamily, int>();
            var registryGrew = 0;
            var heldBack = new List<(string By, string Line)>();
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
                    && DrydockFidelitySystem.TimeKeepsItsMeaning(result.Before.Values[key], result.After.Values[key], TimeToleranceSeconds)
                    && TimeSentinel(result.Before.Values[key]) == TimeSentinel(result.After.Values[key]))
                {
                    timeKept++;
                    continue;
                }

                // The census's prediction, taken before anything else sorts the line, because the question the
                // codec mode asks is what differs that the census did not already say would.
                if (CodecMode && PredictedFor(line) is { Count: > 0 } rows)
                {
                    predictedLines.Add(line);
                    predictedSeen.UnionWith(rows);
                    continue;
                }

                // A family whose cause is read in code, which would otherwise bury the new ones by its volume; never one that
                // compounds across the two trips, which stays a finding.
                if (CodecMode && key != null && FamilyFor(line, key, result, previous) is ({ } family, var grew))
                {
                    if (grew)
                    {
                        familyGrew[family] = familyGrew.GetValueOrDefault(family) + 1;
                        heldBack.Add(($"family {family.Name}", line));
                        findings.Add(line);
                        continue;
                    }

                    if (!familyLines.TryGetValue(family, out var members))
                        familyLines[family] = members = new List<string>();

                    members.Add(line);
                    continue;
                }

                var recovery = RecoveryOf(key, result);

                if (key != null && live.Contains(key) && !MovedAgainstItsWindow(key, result))
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
                else if (RecreatedTransient(line, key, result, protoMan) != null)
                    recreatedLines.Add(line);
                else if (key != null && Classify(key, result, recovery, out var belowFloor) is { } _)
                {
                    if (Compounding(line, key, result, previous))
                    {
                        registryGrew++;
                        heldBack.Add(("registry", line));
                        findings.Add(line);
                    }
                    else
                    {
                        (belowFloor ? belowFloorLines : classifiedLines).Add(line);
                    }
                }
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

            foreach (var line in findings)
                findingKinds.Add(StopKindOf(line));

            unexplained?.AddRange(findings.Concat(settlingLines));

            Dump(rung, vesselId, trip, result, findings.Concat(settlingLines).Concat(predictedLines));

            sb.AppendLine($"[ladder] round trip {trip}: {result.Before.Entities} entities before, {result.After.Entities} after "
                          + $"({result.Before.TieBroken}/{result.After.TieBroken} tie-broken, "
                          + $"{result.Before.Uncapturable}/{result.After.Uncapturable} uncapturable), "
                          + $"{result.Before.Values.Count} keys, {live.Count} live key(s), clock advanced {result.ElapsedSeconds:F1}s, "
                          + $"ship ran {result.ShipSeconds:F3}s, {timeKept} time change(s) kept their meaning.");

            AppendUncapturable(sb, rung, vesselId, trip, result);

            foreach (var key in result.Before.Values.Keys
                         .Where(k => k.EndsWith("|TradeCrateComponent.~DestinationStation", StringComparison.Ordinal))
                         .OrderBy(k => k, StringComparer.Ordinal))
            {
                var path = key[..key.IndexOf('|')];
                var express = $"{path}|TradeCrateComponent.~ExpressDeliveryTime{DrydockFidelitySystem.TimeSuffix}";
                sb.AppendLine($"[ladder] trade control {path}: destination {ValueIn(result.Before, key)}, {ValueIn(result.After, key)}, {ValueIn(result.Late, key)}; "
                              + $"express {ValueIn(result.Before, express)}, {ValueIn(result.After, express)}, {ValueIn(result.Late, express)}");
            }

            // The recipe's door, and every door with a state change pending before the store, which is F9's case.
            var nextChange = $"|DoorComponent.~NextStateChange{DrydockFidelitySystem.TimeSuffix}";
            var doors = result.Before.Values
                .Where(kv => kv.Key.EndsWith(nextChange, StringComparison.Ordinal) && kv.Value != "null")
                .Select(kv => kv.Key[..kv.Key.IndexOf('|')])
                .ToHashSet();
            if (doorPath != null)
                doors.Add(doorPath);

            foreach (var path in doors.OrderBy(p => p, StringComparer.Ordinal))
            {
                var state = $"{path}|DoorComponent.State";
                sb.AppendLine($"[ladder] door control {path}: state {ValueIn(result.Before, state)}, {ValueIn(result.After, state)}, {ValueIn(result.Late, state)}; "
                              + $"next change {ValueIn(result.Before, path + nextChange)}, {ValueIn(result.After, path + nextChange)}, {ValueIn(result.Late, path + nextChange)}");
            }

            // Every match and smokable the hull carries: the recipe's lit pair and the unlit pair beside them. A lit one
            // comes back lit, so the late column is the measurement: a match's burn-out is a timer no store records
            // (MatchstickSystem.cs:91-98) and a smokable burns down only while the system holds it (SmokingSystem.cs:151-156).
            foreach (var key in result.Before.Values.Keys
                         .Where(k => k.EndsWith("|MatchstickComponent.CurrentState", StringComparison.Ordinal)
                                     || k.EndsWith("|SmokableComponent.State", StringComparison.Ordinal))
                         .OrderBy(k => k, StringComparer.Ordinal))
            {
                var path = key[..key.IndexOf('|')];
                var duration = ValueIn(result.Before, $"{path}|MatchstickComponent.Duration");
                sb.AppendLine($"[ladder] smoking control {path}: {key[(key.IndexOf('|') + 1)..]} early {ValueIn(result.Early, key)}, "
                              + $"before {ValueIn(result.Before, key)}, after {ValueIn(result.After, key)}, late {ValueIn(result.Late, key)}"
                              + (duration == "<absent>" ? "" : $"; burns out {duration}s after it is lit"));

                var solution = result.Before.Values.Keys.FirstOrDefault(k =>
                    k.StartsWith(path + "/", StringComparison.Ordinal)
                    && k.EndsWith("|SolutionComponent.Solution", StringComparison.Ordinal));
                if (solution == null)
                    continue;

                sb.AppendLine($"         compared as {solution[(path.Length + 1)..]}: early {OneLine(ValueIn(result.Early, solution))}; "
                              + $"before {OneLine(ValueIn(result.Before, solution))}; after {OneLine(ValueIn(result.After, solution))}; "
                              + $"late {OneLine(ValueIn(result.Late, solution))}");
            }

            foreach (var (recipe, tiles) in gasRooms)
            {
                var at = tiles.Select(tile => $"{tile.X},{tile.Y}").ToHashSet();
                var anchor = tiles.OrderBy(tile => tile.X).ThenBy(tile => tile.Y).First();
                sb.AppendLine($"[ladder] gas control {recipe} at {anchor.X},{anchor.Y}: before {RoomGas(result.Before, at)}; after {RoomGas(result.After, at)}; late {RoomGas(result.Late, at)}");
            }

            if (CodecMode)
            {
                AppendKinds(sb, rung, vesselId, trip, "predicted", predictedLines, examples: 0);
                AppendPredicted(sb, trip, predictedSeen, result);

                foreach (var family in KnownFamilies)
                {
                    var lines = familyLines.GetValueOrDefault(family) ?? new List<string>();
                    var grew = familyGrew.GetValueOrDefault(family);
                    sb.AppendLine($"[ladder] family {family.Name}: {lines.Count} line(s)"
                                  + (grew > 0 ? $", and {grew} it matched that compound across the trips, left as findings" : "")
                                  + $". {family.Receipt}");
                }
            }

            if (previous != null)
            {
                sb.AppendLine($"[ladder] round trip {trip}: {registryGrew} line(s) the registry classifies compound across the trips, left as findings.");
                sb.AppendLine($"[ladder] round trip {trip}: {CacheLinesExempted} BUI state cache line(s) ended smaller each store and were left with their family, "
                              + "which is bounded at empty and filled at the open (ruled 2026-09-19); growth would still have held them back.");
                CacheLinesExempted = 0;
            }

            // Every line the no-growth rule kept from an explanation, one per line so a run over many rungs can be grepped.
            foreach (var (by, line) in heldBack)
                sb.AppendLine($"[ladder-held-back] rung={rung} vessel={vesselId} trip={trip} by={by} {OneLine(line, 300)}");

            AppendKinds(sb, rung, vesselId, trip, "finding", findings, examples: 2);
            AppendKinds(sb, rung, vesselId, trip, "settling", settlingLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "settled", settledLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "classified", classifiedLines, examples: 0);
            AppendKinds(sb, rung, vesselId, trip, "below-floor", belowFloorLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "policy", policyLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "unsaved", unsavedLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "recreated", recreatedLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "moved", movedLines, examples: 1);
            AppendKinds(sb, rung, vesselId, trip, "live", liveLines, examples: 0);
            if (grants != null)
                AppendKinds(sb, rung, vesselId, trip, "grant", grantLines, examples: 0);

            if (result.MapInit != null)
                sb.AppendLine($"[ladder] round trip {trip} map-init transaction:").Append(result.MapInit.Detail());

            return findings.Count;
        }

        /// <summary>
        /// The registry entry that explains a key's difference, or null. A Live entry explains it only while
        /// the value is settling, settled, or within <see cref="LiveTolerance"/> of its stored value, or within
        /// <see cref="LiveFloor"/>, which sets <paramref name="belowFloor"/>. A Live entry for a time explains it
        /// outright, and one of <see cref="LiveCountdowns"/> explains a fall no longer than the clock the ship ran.
        /// </summary>
        private static StateClass? Classify(string key, RoundTripResult result, Recovery recovery, out bool belowFloor)
        {
            belowFloor = false;

            // A colon separates a member from the instance it was rendered for (a tile, a node group), as in KindOf.
            var member = key[(key.IndexOf('|') + 1)..];
            var colon = member.IndexOf(':');
            if (colon >= 0)
                member = member[..colon];

            var time = member.EndsWith(DrydockFidelitySystem.TimeSuffix);
            if (time)
                member = member[..^DrydockFidelitySystem.TimeSuffix.Length];

            var dot = member.IndexOf('.');
            var component = dot < 0 ? member : member[..dot];

            if (!Registry.TryGetValue(member, out var entry) && !Registry.TryGetValue(component + ".*", out entry))
                return null;

            // A Live time is a repeating timer: one that fired between two snapshots fails TimeKeepsItsMeaning by its
            // phase alone, so its entry, whose reason carries the period, explains it. A time render is not a number.
            if (entry.Class != StateClass.Live || recovery != Recovery.Persistent || time)
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

            // A countdown runs only while the ship does, so it may fall by the clock the ship ran and no further.
            if (LiveCountdowns.Contains(member) && a <= b && b - a <= result.ShipSeconds + result.TickSeconds)
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

        /// <summary>
        /// For a changed field, the path of the nearest entity at or above it whose prototype is <c>save: false</c>, when
        /// that entity is on both sides and <see cref="IsTransient"/>: the store deleted it and its owner made it again
        /// after the retrieve, as an alarm plays its sound again. Null otherwise, so a recreated entity that is not
        /// transient, such as a shield its emitter respawns, stays a finding.
        /// </summary>
        private static string? RecreatedTransient(string line, string? key, RoundTripResult result, IPrototypeManager protoMan)
        {
            if (!line.StartsWith("CHANGED") || key == null)
                return null;

            var segments = key[..key.IndexOf('|')].Split('/');
            for (var i = segments.Length; i > 0; i--)
            {
                var prefix = string.Join('/', segments, 0, i);
                if (!result.Before.Values.TryGetValue(prefix + "|MetaDataComponent.savable", out var savable) || savable != "false")
                    continue;

                return result.After.Values.ContainsKey(prefix + "|MetaDataComponent.savable") && IsTransient(prefix, result.Before, protoMan)
                    ? prefix
                    : null;
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
        /// Whether a time key held its meaning across the live window, so its rendered change is only the clock moving.
        /// </summary>
        private static bool TimeHeldDuringWindow(string key, RoundTripResult result) =>
            key.EndsWith(DrydockFidelitySystem.TimeSuffix, StringComparison.Ordinal)
            && result.Early.Values.TryGetValue(key, out var early)
            && result.Before.Values.TryGetValue(key, out var before)
            && DrydockFidelitySystem.TimeKeepsItsMeaning(early, before, TimeToleranceSeconds);

        private static string ValueIn(DrydockStateSnapshot snapshot, string key) =>
            snapshot.Values.GetValueOrDefault(key, "<absent>");

        /// <summary>A rendered value folded onto one line and cut short, so a control line stays one line. Long enough
        /// for a solution, whose reagent quantity the serializer writes last.</summary>
        private static string OneLine(string value, int max = 160)
        {
            var flat = string.Join(' ', value.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));
            return flat.Length <= max ? flat : flat[..max] + "…";
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
