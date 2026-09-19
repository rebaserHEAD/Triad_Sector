#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Mono.ScuttleDevice;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Kitchen.Components;
using Content.Server.Kitchen.EntitySystems;
using Content.Server.Nyanotrasen.Kitchen.Components;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._FarHorizons.Power.Generation.FissionGenerator;
using Content.Shared.Atmos;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Disposal.Components;
using Content.Shared.Disposal.Unit;
using Content.Shared.Kitchen;
using Content.Shared.Maps;
using Content.Shared.PDA;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The workbench rung (<c>resources/2026-09-19-manifest-recipes.tsv</c>): a synthetic grid built in code and populated
    /// with the manifest members no sold hull exercises, each put in the state its recipe names, then round-tripped twice
    /// through the grid image exactly as a hull rung is. Codec mode only.
    ///
    /// <para>Run each wave twice: first with <c>LADDER_MANIFEST_OFF</c> naming the wave's components, so every recipe shows
    /// the loss its member would have without the manifest, then without it, so the same recipes show the apply. Only fixed
    /// prototypes are placed, never a random or filled spawner, so the population is the same every run.</para>
    ///
    /// <para>The grid: 16 by 16 steel floor walled round, the power chain DebugGenerator, HV cable, SubstationBasic, MV
    /// cable, APCBasic and an extension-cable lattice (the column x = 3 and the rows y = 4, 7, 10) that every machine is one
    /// or two tiles from. No air: nothing in wave 1 reads the room, and a grid atmosphere adds keys to every snapshot.</para>
    ///
    /// <para>Every recipe's machines are placed with the grid, and each recipe is started once power is up: a receiver
    /// pairs when it starts but reads powered only after a power solve, and a cook, a flush and a fry refuse an unpowered
    /// machine.</para>
    /// </summary>
    public sealed partial class DrydockFidelityLadderLocalTest
    {
        private const int WorkbenchSize = 16;

        /// <summary>Long enough for the power chain to come up and every receiver to read powered before a recipe starts.</summary>
        private const double WorkbenchPowerSeconds = 5;

        /// <summary>Long enough for a fryer to fry once (its interval is 5 s) before the store.</summary>
        private const double WorkbenchRecipeSeconds = 6;

        /// <summary>A cook, a grind and a manual flush last this long, so each is still under way at the store.</summary>
        private static readonly TimeSpan WorkbenchLongTimer = TimeSpan.FromSeconds(60);

        /// <param name="Components">The snapshot component names (<c>PdaComponent</c>) whose keys the recipe's control prints.</param>
        /// <param name="Paths">The snapshot paths of the recipe's entities, <c>Prototype@x,y</c>.</param>
        /// <param name="Start">Puts the placed machines into the recipe's state once power is up; returns what it did.</param>
        private sealed record WorkbenchRecipe(int Number, string Name, string Members, string[] Components, List<string> Paths, Func<List<string>> Start)
        {
            public readonly List<string> Setup = new();
        }

        private static IEnumerable<TestCaseData> WorkbenchWaves() => new[] { new TestCaseData(1).SetName("Workbench_Wave1") };

        [TestCaseSource(nameof(WorkbenchWaves))]
        public async Task Workbench(int wave)
        {
            if (!CodecMode)
                Assert.Ignore("The workbench measures the grid image: run it with LADDER_MODE=codec.");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var timing = server.ResolveDependency<IGameTiming>();
            CodecNotes.Clear();

            var map = await pair.CreateTestMap();
            EntityUid grid = default;
            var recipes = new List<WorkbenchRecipe>();
            await server.WaitPost(() =>
            {
                var atmos = server.System<AtmosphereSystem>();
                if (!entMan.HasComponent<MapAtmosphereComponent>(map.MapUid))
                    atmos.SetMapAtmosphere(map.MapUid, space: true, new GasMixture());

                grid = BuildWorkbench(pair, map.MapId);
                recipes = PlaceWorkbenchWave(pair, grid, wave);
            });

            await pair.RunTicksSync(Ticks(timing, WorkbenchPowerSeconds));
            await server.WaitPost(() =>
            {
                foreach (var recipe in recipes)
                    recipe.Setup.AddRange(recipe.Start());
            });
            await pair.RunTicksSync(Ticks(timing, WorkbenchRecipeSeconds));

            var first = await RoundTrip(pair, grid, Guid.Empty, EntityUid.Invalid, null);
            var second = await RoundTrip(pair, first.Retrieved, Guid.Empty, EntityUid.Invalid, null);

            var rung = 900 + wave;
            var vesselId = $"Workbench{wave}{(ManifestOff.Count > 0 ? "off" : "on")}";
            var sb = new StringBuilder();
            sb.AppendLine($"[ladder] rung {rung} {vesselId} through the grid image; manifest held off: {(ManifestOff.Count == 0 ? "none" : string.Join(",", ManifestOff))}");
            var findingKinds = new HashSet<string>(StringComparer.Ordinal);
            var secondUnexplained = new List<string>();
            var noRooms = new List<(string Recipe, List<Vector2i> Tiles)>();
            var findings = Report(sb, rung, vesselId, 1, first, null, noRooms, null, protoMan, findingKinds)
                           + Report(sb, rung, vesselId, 2, second, null, noRooms, null, protoMan, findingKinds, secondUnexplained);
            AppendShapes(sb, rung, vesselId, first, second, secondUnexplained);

            foreach (var recipe in recipes)
                AppendRecipeControl(sb, recipe, first, second);

            foreach (var note in CodecNotes)
                sb.AppendLine(note);
            CodecNotes.Clear();

            sb.AppendLine($"[ladder] rung {rung} {vesselId}: {first.Before.Entities} entities, {findings} finding line(s), {clock.Elapsed.TotalSeconds:F1}s wall before cleanup");
            await TestContext.Out.WriteLineAsync(sb.ToString());

            Assert.That(recipes, Is.Not.Empty, "The control: the wave placed no recipe.");
            Assert.That(first.Before.Entities, Is.GreaterThan(0), "The control: round trip 1 compared no entities.");

            await server.WaitPost(() => entMan.DeleteEntity(second.Retrieved));
            await pair.RunTicksSync(3);
            await pair.CleanReturnAsync();
        }

        private static int Ticks(IGameTiming timing, double seconds) => (int) Math.Ceiling(seconds / timing.TickPeriod.TotalSeconds);

        /// <summary>The grid, walls and the power chain; every machine a recipe adds is placed within reach of the lattice.</summary>
        private static EntityUid BuildWorkbench(TestPair pair, MapId mapId)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var mapSystem = entMan.System<SharedMapSystem>();
            var tileDefs = server.ResolveDependency<ITileDefinitionManager>();

            var grid = mapSystem.CreateGridEntity(mapId);

            // Away from the test map's own grid, so the two never touch.
            entMan.System<SharedTransformSystem>().SetLocalPosition(grid.Owner, new Vector2(64, 64));

            var floor = new Tile(tileDefs["FloorSteel"].TileId);
            for (var x = 0; x < WorkbenchSize; x++)
            {
                for (var y = 0; y < WorkbenchSize; y++)
                    mapSystem.SetTile(grid.Owner, grid.Comp, new Vector2i(x, y), floor);
            }

            for (var i = 0; i < WorkbenchSize; i++)
            {
                Place(entMan, grid.Owner, "WallSolid", i, 0);
                Place(entMan, grid.Owner, "WallSolid", i, WorkbenchSize - 1);
                if (i is > 0 and < WorkbenchSize - 1)
                {
                    Place(entMan, grid.Owner, "WallSolid", 0, i);
                    Place(entMan, grid.Owner, "WallSolid", WorkbenchSize - 1, i);
                }
            }

            Place(entMan, grid.Owner, "DebugGenerator", 1, 1);
            Place(entMan, grid.Owner, "CableHV", 1, 1);
            Place(entMan, grid.Owner, "CableHV", 2, 1);
            Place(entMan, grid.Owner, "SubstationBasic", 2, 1);
            Place(entMan, grid.Owner, "CableMV", 2, 1);
            Place(entMan, grid.Owner, "CableMV", 3, 1);
            Place(entMan, grid.Owner, "APCBasic", 3, 1);

            var lattice = new HashSet<Vector2i>();
            for (var y = 1; y <= 10; y++)
                lattice.Add(new Vector2i(3, y));
            foreach (var row in new[] { 4, 7, 10 })
            {
                for (var x = 3; x <= WorkbenchSize - 2; x++)
                    lattice.Add(new Vector2i(x, row));
            }

            foreach (var tile in lattice)
                Place(entMan, grid.Owner, "CableApcExtension", tile.X, tile.Y);

            return grid.Owner;
        }

        /// <summary>A prototype on a tile centre, anchored when it is a static body, so anchor-gated handlers run.</summary>
        private static EntityUid Place(IEntityManager entMan, EntityUid grid, string prototype, int x, int y)
        {
            var uid = entMan.SpawnEntity(prototype, new EntityCoordinates(grid, x + 0.5f, y + 0.5f));
            var xform = entMan.GetComponent<TransformComponent>(uid);
            if (!xform.Anchored && entMan.HasComponent<Robust.Shared.Physics.Components.PhysicsComponent>(uid)
                && entMan.GetComponent<Robust.Shared.Physics.Components.PhysicsComponent>(uid).BodyType == Robust.Shared.Physics.BodyType.Static)
            {
                entMan.System<SharedTransformSystem>().AnchorEntity((uid, xform));
            }

            return uid;
        }

        private static string PathOf(string prototype, int x, int y) => $"{prototype}@{x},{y}";

        /// <summary>Every recipe of a wave, each on its own tiles; the wave is the rows of the recipe file.</summary>
        private static List<WorkbenchRecipe> PlaceWorkbenchWave(TestPair pair, EntityUid grid, int wave)
        {
            if (wave != 1)
                throw new ArgumentOutOfRangeException(nameof(wave), wave, "Only wave 1 is built.");

            var server = pair.Server;
            var entMan = server.EntMan;
            var recipes = new List<WorkbenchRecipe>();
            var containers = entMan.System<SharedContainerSystem>();
            var slots = entMan.System<ItemSlotsSystem>();
            EntityCoordinates At(int x, int y) => new(grid, x + 0.5f, y + 0.5f);

            // 1. A PDA holding an ID card. ContainedId is set only from the ID slot's insert message, and the passenger PDA
            // comes with its own card, so that card is taken out and deleted first and the insert here is the write under test.
            {
                var pda = Place(entMan, grid, "PassengerPDA", 5, 5);
                recipes.Add(new WorkbenchRecipe(1, "PDA and ID", "Pda.ContainedId", new[] { "PdaComponent" },
                    new List<string> { PathOf("PassengerPDA", 5, 5) },
                    () =>
                    {
                        var comp = entMan.GetComponent<PdaComponent>(pda);
                        var ejected = slots.TryEject(pda, PdaComponent.PdaIdSlotId, null, out var own);
                        if (own is { } spawnedCard)
                            entMan.DeleteEntity(spawnedCard);
                        var emptied = comp.ContainedId == null;

                        var card = entMan.SpawnEntity("PassengerIDCard", At(5, 5));
                        var inserted = slots.TryInsert(pda, PdaComponent.PdaIdSlotId, card, null);
                        return new List<string> { $"own card ejected {ejected}, ContainedId null after {emptied}; new card inserted {inserted}, ContainedId is it: {comp.ContainedId == card}" };
                    }));
            }

            // 2. A receiver paired with the lattice at startup, then a dead-end stub laid a tile nearer: a startup on the
            // load would take the stub.
            {
                var alarm = Place(entMan, grid, "AirAlarm", 13, 12);
                recipes.Add(new WorkbenchRecipe(2, "dead-end cable stub", "ExtensionCableReceiver.Provider",
                    new[] { "ExtensionCableReceiverComponent", "ApcPowerReceiverComponent" },
                    new List<string> { PathOf("AirAlarm", 13, 12) },
                    () =>
                    {
                        var provider = entMan.GetComponent<ExtensionCableReceiverComponent>(alarm).Provider?.Owner;
                        var powered = entMan.GetComponent<ApcPowerReceiverComponent>(alarm).Powered;
                        Place(entMan, grid, "CableApcExtension", 13, 13);
                        return new List<string> { $"paired with {provider?.ToString() ?? "nothing"}, powered {powered}; stub laid at 13,13, one tile off, the lattice two" };
                    }));
            }

            // 3. A cell charging: the charger adds Charging to the cell and names itself in ChargerUid.
            {
                var charger = Place(entMan, grid, "PowerCellRecharger", 7, 5);
                recipes.Add(new WorkbenchRecipe(3, "Charging", "Charging.ChargerUid", new[] { "ChargingComponent", "BatteryComponent" },
                    new List<string> { PathOf("PowerCellRecharger", 7, 5) },
                    () =>
                    {
                        var cell = entMan.SpawnEntity("PowerCellMedium", At(7, 5));
                        if (entMan.TryGetComponent<BatteryComponent>(cell, out var battery))
                            entMan.System<BatterySystem>().SetCharge(cell, battery.MaxCharge * 0.1f, battery);
                        var inserted = slots.TryInsert(charger, entMan.GetComponent<ChargerComponent>(charger).SlotId, cell, null);
                        return new List<string> { $"cell inserted {inserted} at 10% charge, charging {entMan.HasComponent<Content.Server._NF.Power.Components.ChargingComponent>(cell)}" };
                    }));
            }

            // 4. A microwave mid-cook.
            {
                var microwave = Place(entMan, grid, "KitchenMicrowave", 9, 5);
                recipes.Add(new WorkbenchRecipe(4, "microwave", "ActiveMicrowave.CookTimeRemaining, TotalTime, PortionedRecipe",
                    new[] { "ActiveMicrowaveComponent", "MicrowaveComponent" },
                    new List<string> { PathOf("KitchenMicrowave", 9, 5) },
                    () =>
                    {
                        var comp = entMan.GetComponent<MicrowaveComponent>(microwave);
                        comp.CurrentCookTimerTime = (uint) WorkbenchLongTimer.TotalSeconds;
                        containers.Insert(entMan.SpawnEntity("FoodMeat", At(9, 5)), comp.Storage);
                        entMan.System<MicrowaveSystem>().Wzhzhzh(microwave, comp, null);
                        return new List<string> { $"cooking {entMan.HasComponent<ActiveMicrowaveComponent>(microwave)}, {WorkbenchLongTimer.TotalSeconds}s set" };
                    }));
            }

            // 5. A grinder mid-work. Its component and its start are the system's own (Access), so both go by reflection.
            {
                var grinder = Place(entMan, grid, "KitchenReagentGrinder", 11, 5);
                recipes.Add(new WorkbenchRecipe(5, "grinder", "ActiveReagentGrinder.EndTime, Program",
                    new[] { "ActiveReagentGrinderComponent" },
                    new List<string> { PathOf("KitchenReagentGrinder", 11, 5) },
                    () =>
                    {
                        var comp = entMan.GetComponent<ReagentGrinderComponent>(grinder);
                        typeof(ReagentGrinderComponent).GetField(nameof(ReagentGrinderComponent.WorkTime))!.SetValue(comp, WorkbenchLongTimer);
                        slots.TryInsert(grinder, SharedReagentGrinder.BeakerSlotId, entMan.SpawnEntity("Beaker", At(11, 5)), null);
                        containers.Insert(entMan.SpawnEntity("FoodBanana", At(11, 5)),
                            containers.EnsureContainer<Container>(grinder, SharedReagentGrinder.InputContainerId));
                        var doWork = typeof(ReagentGrinderSystem).GetMethod("DoWork", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        foreach (var program in new[] { GrinderProgram.Juice, GrinderProgram.Grind })
                        {
                            if (entMan.HasComponent<ActiveReagentGrinderComponent>(grinder))
                                break;
                            doWork.Invoke(entMan.System<ReagentGrinderSystem>(), new object[] { grinder, comp, program });
                        }

                        return new List<string> { $"working {entMan.HasComponent<ActiveReagentGrinderComponent>(grinder)}" };
                    }));
            }

            // 6. A disposal unit engaged with something inside, its flush a minute off.
            {
                var unit = Place(entMan, grid, "DisposalUnit", 13, 5);
                recipes.Add(new WorkbenchRecipe(6, "disposal unit", "DisposalUnit.NextFlush", new[] { "DisposalUnitComponent" },
                    new List<string> { PathOf("DisposalUnit", 13, 5) },
                    () =>
                    {
                        var comp = entMan.GetComponent<DisposalUnitComponent>(unit);
                        comp.ManualFlushTime = WorkbenchLongTimer;
                        containers.Insert(entMan.SpawnEntity("FoodBanana", At(13, 5)), comp.Container);
                        entMan.System<SharedDisposalUnitSystem>().ManualEngage(unit, comp);
                        return new List<string> { $"engaged {comp.Engaged}, NextFlush {comp.NextFlush?.ToString() ?? "null"}" };
                    }));
            }

            // 7. A fryer with oil and an item, which the wait before the store fries once.
            {
                var fryer = Place(entMan, grid, "KitchenDeepFryer", 5, 8);
                recipes.Add(new WorkbenchRecipe(7, "deep fryer", "DeepFryer.NextFryTime; PreventCrisping.Cycles; DeepFried.OriginalName; MetaData.EntityName",
                    new[] { "DeepFryerComponent", "PreventCrispingComponent", "DeepFriedComponent", "MetaDataComponent" },
                    new List<string> { PathOf("KitchenDeepFryer", 5, 8) },
                    () =>
                    {
                        var comp = entMan.GetComponent<DeepFryerComponent>(fryer);
                        var solutions = entMan.System<SharedSolutionContainerSystem>();
                        var oiled = solutions.TryGetSolution(fryer, comp.SolutionName, out var vat, out _)
                                    && solutions.TryAddReagent(vat.Value, "Cornoil", 50, out _);
                        containers.Insert(entMan.SpawnEntity("FoodMeat", At(5, 8)), comp.Storage);
                        return new List<string> { $"oil added {oiled}, an item in the basket, fried by the {WorkbenchRecipeSeconds}s wait" };
                    }));
            }

            // 8. A scuttle device armed, its countdown running.
            {
                var scuttle = Place(entMan, grid, "ScuttleDeviceRazorN", 8, 8);
                recipes.Add(new WorkbenchRecipe(8, "scuttle device",
                    "ScuttleDevice.RemainingTime, CooldownTime, Armed, PlayedNukeSong, NukeSongLength, SelectedNukeSong, PlayedAlertSound",
                    new[] { "ScuttleDeviceComponent" },
                    new List<string> { PathOf("ScuttleDeviceRazorN", 8, 8) },
                    () =>
                    {
                        entMan.System<ScuttleDeviceSystem>().ArmBomb(scuttle);
                        return new List<string> { $"armed {entMan.GetComponent<ScuttleDeviceComponent>(scuttle).Armed}" };
                    }));
            }

            // 9. Monitors naming their machines. The link member is set as its link would set it; the readings the panels
            // then take over the device network stay owed until H18.
            {
                var turbine = Place(entMan, grid, "GasTurbineSmall", 10, 12);
                var turbineMonitor = Place(entMan, grid, "GasTurbineMonitor", 11, 8);
                var reactor = Place(entMan, grid, "NuclearReactorEmpty", 6, 12);
                var reactorMonitor = Place(entMan, grid, "NuclearReactorMonitor", 13, 8);
                recipes.Add(new WorkbenchRecipe(9, "turbine and reactor monitors", "GasTurbineMonitor.turbine, NuclearReactorMonitor.reactor",
                    new[] { "GasTurbineMonitorComponent", "NuclearReactorMonitorComponent" },
                    new List<string> { PathOf("GasTurbineMonitor", 11, 8), PathOf("NuclearReactorMonitor", 13, 8) },
                    () =>
                    {
                        entMan.GetComponent<GasTurbineMonitorComponent>(turbineMonitor).turbine = entMan.GetNetEntity(turbine);
                        entMan.GetComponent<NuclearReactorMonitorComponent>(reactorMonitor).reactor = entMan.GetNetEntity(reactor);
                        return new List<string> { "links set directly; readings owed until H18" };
                    }));
            }

            return recipes;
        }

        /// <summary>
        /// A recipe's control: every key under its entities (and what they hold) of its components, before, after and late
        /// on both trips, a star on each that differs. This is where a recipe is read, whatever bucket its lines sorted into.
        /// </summary>
        private static void AppendRecipeControl(StringBuilder sb, WorkbenchRecipe recipe, RoundTripResult first, RoundTripResult second)
        {
            sb.AppendLine($"[workbench] recipe {recipe.Number} {recipe.Name}: members {recipe.Members}; setup: {string.Join("; ", recipe.Setup)}");
            var shown = 0;
            foreach (var (trip, result) in new[] { (1, first), (2, second) })
            {
                var keys = result.Before.Values.Keys.Concat(result.After.Values.Keys)
                    .Where(key => recipe.Paths.Any(path => key.StartsWith(path + "|", StringComparison.Ordinal) || key.StartsWith(path + "/", StringComparison.Ordinal)))
                    .Where(key => recipe.Components.Any(component => key.Contains("|" + component + ".", StringComparison.Ordinal)))
                    .Distinct()
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToList();

                if (keys.Count == 0)
                    sb.AppendLine($"[workbench]   trip {trip}: no key of {string.Join(", ", recipe.Components)} under {string.Join(", ", recipe.Paths)}");

                foreach (var key in keys)
                {
                    if (shown++ >= 40)
                        break;

                    var before = ValueIn(result.Before, key);
                    var after = ValueIn(result.After, key);
                    var late = ValueIn(result.Late, key);
                    var mark = before != after || after != late ? "*" : " ";
                    sb.AppendLine($"[workbench]  {mark}trip {trip} {key}: before {OneLine(before)} | after {OneLine(after)} | late {OneLine(late)}");
                }
            }
        }
    }
}
