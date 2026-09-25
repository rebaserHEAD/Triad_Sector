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
using Content.Server.Disposal.Tube;
using Content.Server.Disposal.Unit;
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
using Content.Server._Crescent.Dispenser;
using Content.Server._Triad.Drydock.Codec;
using Content.Shared._Crescent.Dispenser;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
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

        /// <summary>
        /// Long enough for a fryer to fry three times (a tick after its start, then every 5 s) and not a fourth, which a
        /// potato slice needs to become fries and count one crisping cycle.
        /// </summary>
        private const double WorkbenchRecipeSeconds = 12;

        /// <summary>A cook, a grind and a manual flush last this long, so each is still under way after both round trips.</summary>
        private static readonly TimeSpan WorkbenchLongTimer = TimeSpan.FromSeconds(180);

        /// <param name="Components">The snapshot component names (<c>PdaComponent</c>) whose keys the recipe's control prints.</param>
        /// <param name="Paths">The snapshot paths of the recipe's entities, <c>Prototype@x,y</c>.</param>
        /// <param name="Start">Puts the placed machines into the recipe's state once power is up; returns what it did.</param>
        private sealed record WorkbenchRecipe(int Number, string Name, string Members, string[] Components, List<string> Paths, Func<List<string>> Start)
        {
            public readonly List<string> Setup = new();

            /// <summary>Holds the recipe's state still just before the store and says what it is (a fryer that has fried once).</summary>
            public Func<List<string>>? BeforeStore { get; init; }

            /// <summary>
            /// Reads the recipe's entities on the grid the second round trip returned: what to print, and what is wrong. What
            /// is wrong fails the rung only when no manifest member is held off, since holding them off is how a loss is shown.
            /// </summary>
            public Func<EntityUid, (List<string> Notes, List<string> Wrong)>? AfterLoad { get; init; }
        }

        private static IEnumerable<TestCaseData> WorkbenchWaves() =>
            new[] { new TestCaseData(1).SetName("Workbench_Wave1"), new TestCaseData(2).SetName("Workbench_Wave2") };

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
            LoopFailures.Clear();

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
            await server.WaitPost(() =>
            {
                foreach (var recipe in recipes)
                {
                    if (recipe.BeforeStore is { } hold)
                        recipe.Setup.AddRange(hold().Select(line => $"before the store: {line}"));
                }
            });

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
                           + Report(sb, rung, vesselId, 2, second, null, noRooms, null, protoMan, findingKinds, secondUnexplained, previous: first);
            AppendShapes(sb, rung, vesselId, first, second, secondUnexplained);

            foreach (var recipe in recipes)
                AppendRecipeControl(sb, recipe, first, second);

            var wrong = new List<string>();
            await server.WaitPost(() =>
            {
                foreach (var recipe in recipes)
                {
                    if (recipe.AfterLoad is not { } read)
                        continue;

                    var (notes, bad) = read(second.Retrieved);
                    foreach (var note in notes)
                        sb.AppendLine($"[workbench] recipe {recipe.Number} after the loads: {note}");
                    foreach (var line in bad)
                        sb.AppendLine($"[workbench] recipe {recipe.Number} WRONG after the loads: {line}");
                    wrong.AddRange(bad.Select(line => $"recipe {recipe.Number}: {line}"));
                }
            });

            foreach (var note in CodecNotes)
                sb.AppendLine(note);
            CodecNotes.Clear();

            sb.AppendLine($"[ladder] rung {rung} {vesselId}: {first.Before.Entities} entities, {findings} finding line(s), {clock.Elapsed.TotalSeconds:F1}s wall before cleanup");
            await TestContext.Out.WriteLineAsync(sb.ToString());

            Assert.That(recipes, Is.Not.Empty, "The control: the wave placed no recipe.");
            Assert.That(first.Before.Entities, Is.GreaterThan(0), "The control: round trip 1 compared no entities.");
            if (ManifestOff.Count == 0)
                Assert.That(wrong, Is.Empty, "With every manifest member applied, each recipe's after-load reading has to hold; the report names each that does not.");

            await server.WaitPost(() => entMan.DeleteEntity(second.Retrieved));
            await pair.RunTicksSync(3);
            await pair.CleanReturnAsync();
            AssertLoopHeld();
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

        /// <summary>
        /// A disposal tube on a tile centre, turned to face <paramref name="facing"/>. A straight tube connects its
        /// facing and the opposite of it, a bend its facing and the quarter turn back from it, and a trunk only its
        /// facing (DisposalTubeSystem.cs:138-143, :162-165, :226-232). The rotation is read when a holder asks the tube
        /// where to go next, so turning the tube after the spawn is enough.
        /// </summary>
        private static EntityUid PlaceTube(IEntityManager entMan, EntityUid grid, string prototype, int x, int y, Direction facing)
        {
            var uid = Place(entMan, grid, prototype, x, y);
            entMan.System<SharedTransformSystem>().SetLocalRotation(uid, facing.ToAngle());
            return uid;
        }

        /// <summary>The grid tile an entity stands on, however deep in a container it is parented.</summary>
        private static Vector2i TileOf(IEntityManager entMan, EntityUid grid, EntityUid uid) =>
            entMan.System<SharedMapSystem>().TileIndicesFor(
                (grid, entMan.GetComponent<MapGridComponent>(grid)),
                entMan.System<SharedTransformSystem>().GetMapCoordinates(uid));

        private static string PathOf(string prototype, int x, int y) => $"{prototype}@{x},{y}";

        /// <summary>
        /// Whether the manifest carries anything on the components a recipe names, which is what decides whether holding
        /// the manifest off can change that recipe's result. Asked of the manifest itself rather than written down per
        /// recipe, so a member moving into or out of the manifest cannot leave a recipe claiming the wrong thing.
        /// </summary>
        private static bool CarriesAManifestMember(WorkbenchRecipe recipe) =>
            recipe.Components
                .Select(name => name.EndsWith("Component", StringComparison.Ordinal) ? name[..^"Component".Length] : name)
                .Any(component => DrydockCodecManifestMembers.Members.Any(member => member.Component == component));


        /// <summary>Every recipe of a wave, each on its own tiles; the wave is the rows of the recipe file.</summary>
        private static List<WorkbenchRecipe> PlaceWorkbenchWave(TestPair pair, EntityUid grid, int wave) =>
            wave switch
            {
                1 => PlaceWave1(pair, grid),
                2 => PlaceWave2(pair, grid),
                _ => throw new ArgumentOutOfRangeException(nameof(wave), wave, "Waves 1 and 2 are built."),
            };

        /// <summary>
        /// Wave 2, from the Surveyor's re-cut recipe file: what one entity holds another by, where the holding is state no
        /// prototype carries. Built a recipe at a time, each on its own tiles, as wave 1's are.
        /// </summary>
        private static List<WorkbenchRecipe> PlaceWave2(TestPair pair, EntityUid grid)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var recipes = new List<WorkbenchRecipe>();
            EntityCoordinates At(int x, int y) => new(grid, x + 0.5f, y + 0.5f);

            // 33. A dart embedded in a wall. The pairing is two halves at once: the dart names the wall in
            // EmbeddedIntoUid and the wall names the dart in EmbeddedObjects, and the dart is parented to the wall and
            // made static by the embed (SharedProjectileSystem.cs:426-454). The store's despawn is the second question
            // here: EmbeddedContainer's terminate handler detaches what it holds and re-parents it to the grid
            // (:509-530), so a despawn that deletes the grid alone leaves the dart behind, where one on a staging map
            // takes it with the map.
            {
                var wall = Place(entMan, grid, "WallSolid", 5, 2);
                recipes.Add(new WorkbenchRecipe(33, "dart in a wall",
                    "EmbeddableProjectile.EmbeddedIntoUid, EmbeddedContainer.EmbeddedObjects",
                    new[] { "EmbeddableProjectileComponent", "EmbeddedContainerComponent" },
                    new List<string> { PathOf("WallSolid", 5, 2) },
                    () =>
                    {
                        var dart = entMan.SpawnEntity("Dart", At(5, 2));
                        var thrown = entMan.EnsureComponent<ThrownItemComponent>(dart);
                        entMan.EventBus.RaiseLocalEvent(dart, new ThrowDoHitEvent(dart, wall, thrown), true);

                        var embedded = entMan.GetComponent<EmbeddableProjectileComponent>(dart).EmbeddedIntoUid;
                        var held = entMan.GetComponentOrNull<EmbeddedContainerComponent>(wall)?.EmbeddedObjects.Count ?? 0;
                        var body = entMan.GetComponent<Robust.Shared.Physics.Components.PhysicsComponent>(dart).BodyType;
                        var parent = entMan.GetComponent<TransformComponent>(dart).ParentUid;

                        return new List<string>
                        {
                            $"dart embedded into {embedded?.ToString() ?? "nothing"} (the wall is {wall}), wall holds {held}, "
                            + $"dart body {body}, parented to {(parent == wall ? "the wall" : parent.ToString())}",
                        };
                    })
                {
                    AfterLoad = loaded =>
                    {
                        var notes = new List<string>();
                        var wrong = new List<string>();
                        var darts = 0;

                        var query = entMan.EntityQueryEnumerator<EmbeddableProjectileComponent, TransformComponent>();
                        while (query.MoveNext(out var uid, out var embeddable, out var xform))
                        {
                            if (xform.GridUid != loaded)
                                continue;

                            darts++;
                            var into = embeddable.EmbeddedIntoUid;
                            var holder = into is { } target && entMan.TryGetComponent<EmbeddedContainerComponent>(target, out var container)
                                ? container.EmbeddedObjects.Contains(uid)
                                : (bool?) null;
                            var body = entMan.GetComponent<Robust.Shared.Physics.Components.PhysicsComponent>(uid).BodyType;

                            notes.Add($"dart embedded into {into?.ToString() ?? "nothing"}, that entity lists it back {holder?.ToString() ?? "it has no container"}, "
                                      + $"body {body}, parent {(into is { } parent && xform.ParentUid == parent ? "the entity it is in" : xform.ParentUid.ToString())}");
                        }

                        if (darts != 1)
                            wrong.Add($"one dart has to come back, {darts} did");

                        return (notes, wrong);
                    },
                });
            }

            // 11. A cargo chute mid-dispense (the rank in the recipe file; wave 1's recipe 2 is the dead-end cable stub).
            // Its interaction deletes the trade good at the start and its update spawns the
            // payment only once the timer reaches the dispense time (Content.Server/_Crescent/Dispenser/DispenserSystem.cs:66-73,
            // :106-121), so a store inside that window holds the only record that anything was put in. Dispensing,
            // DispensingItemId and DispenseTimer are plain fields, which is why the manifest carries them.
            {
                var chute = Place(entMan, grid, "CargoChuteMD", 8, 4);
                recipes.Add(new WorkbenchRecipe(11, "cargo chute mid-dispense",
                    "Dispenser.Dispensing, Dispenser.DispensingItemId, Dispenser.DispenseTimer",
                    new[] { "DispenserComponent" },
                    new List<string> { PathOf("CargoChuteMD", 8, 4) },
                    () =>
                    {
                        var comp = entMan.GetComponent<DispenserComponent>(chute);
                        var (input, output) = comp.Inventory.First();

                        // Long enough that the dispense is still in flight at both stores, as a 0.25 s one never would be.
                        comp.DispenseTime = (float) WorkbenchLongTimer.TotalSeconds;

                        // What the interaction does, without a player to hold the good: the good goes in and is deleted,
                        // and the dispense that owes the payment starts.
                        var good = entMan.SpawnEntity(input, At(8, 4));
                        entMan.System<DispenserSystem>().TryDispenseItem(chute, comp, output);
                        entMan.DeleteEntity(good);

                        return new List<string>
                        {
                            $"{input} put in and deleted, dispensing {comp.Dispensing} of {comp.DispensingItemId}, "
                            + $"timer {comp.DispenseTimer} of {comp.DispenseTime}s",
                        };
                    })
                {
                    AfterLoad = loaded =>
                    {
                        var notes = new List<string>();
                        var wrong = new List<string>();
                        var chutes = 0;

                        var query = entMan.EntityQueryEnumerator<DispenserComponent, TransformComponent>();
                        while (query.MoveNext(out _, out var comp, out var xform))
                        {
                            if (xform.GridUid != loaded)
                                continue;

                            chutes++;
                            notes.Add($"dispensing {comp.Dispensing} of '{comp.DispensingItemId}', timer {comp.DispenseTimer} of {comp.DispenseTime}s");

                            if (!comp.Dispensing)
                                wrong.Add("the chute stopped dispensing, so the trade good put in is paid for by nothing");
                            else if (string.IsNullOrEmpty(comp.DispensingItemId))
                                wrong.Add("the chute is dispensing nothing, so what it owes is lost");
                        }

                        if (chutes != 1)
                            wrong.Add($"one chute has to come back, {chutes} did");

                        return (notes, wrong);
                    },
                });
            }

            // 9. A parcel in flight down a disposal run. The flush spawns a holder, puts what the unit held inside it and
            // pushes it into the trunk (DisposalTubeSystem.cs:420-441); every tube step after that lasts 0.1 s, set by
            // EnterTube itself (DisposableSystem.cs:200-201). The live window before the store is 90 ticks on its own, so
            // the eight tiles of pipe the recipe file asks for would be over before anything was measured: this run is a
            // 57-tube serpentine, 5.7 s of route, which leaves the holder about thirty tubes along at the store and lets
            // it finish well before the last reading. It cannot be made to last through the second store as well, so trip
            // 2 stores a parcel already lying on a tile, which the control says rather than leaving it read as a loss.
            //
            // None of what the holder is doing is a data field, so the reading is where the parcel ends up: with the
            // members applied the holder resumes its route and the far unit ejects the parcel at 14,14, and without them
            // CurrentTube comes back null and the first update drops it where it stood (DisposableSystem.cs:246-249).
            // BeingDisposed.Holder on the parcel is carried by nothing and comes back invalid; only a mob reads it, for
            // the air it breathes (BeingDisposedSystem.cs:17-38), and no mob is stored.
            {
                const string parcelId = "Wrench";
                var nearUnit = Place(entMan, grid, "DisposalUnit", 14, 10);
                PlaceTube(entMan, grid, "DisposalTrunk", 14, 10, Direction.North);

                // Out of the trunk going north, then west, east, west and east again along rows 11 to 14. Each tube
                // faces so that its connectable pair holds both the direction the holder arrives from and the one it
                // leaves by; a bend's pair is its facing and the quarter turn back, which is why the corners differ.
                var secondTube = PlaceTube(entMan, grid, "DisposalBend", 14, 11, Direction.South);
                for (var x = 13; x >= 2; x--)
                    PlaceTube(entMan, grid, "DisposalPipe", x, 11, Direction.East);
                PlaceTube(entMan, grid, "DisposalBend", 1, 11, Direction.North);
                PlaceTube(entMan, grid, "DisposalBend", 1, 12, Direction.East);
                for (var x = 2; x <= 13; x++)
                    PlaceTube(entMan, grid, "DisposalPipe", x, 12, Direction.East);
                PlaceTube(entMan, grid, "DisposalBend", 14, 12, Direction.West);
                PlaceTube(entMan, grid, "DisposalBend", 14, 13, Direction.South);
                for (var x = 13; x >= 2; x--)
                    PlaceTube(entMan, grid, "DisposalPipe", x, 13, Direction.East);
                PlaceTube(entMan, grid, "DisposalBend", 1, 13, Direction.North);
                PlaceTube(entMan, grid, "DisposalBend", 1, 14, Direction.East);
                for (var x = 2; x <= 13; x++)
                    PlaceTube(entMan, grid, "DisposalPipe", x, 14, Direction.East);
                Place(entMan, grid, "DisposalUnit", 14, 14);
                PlaceTube(entMan, grid, "DisposalTrunk", 14, 14, Direction.West);

                // What the flush and the hand-taken step found, for the after-load reading to hold them to.
                var setupWrong = new List<string>();
                var storedTile = "the parcel was never in a tube";
                var holderAir = "no holder ever existed";

                recipes.Add(new WorkbenchRecipe(9, "parcel in flight down a disposal run",
                    "DisposalHolder.StartingTime, TimeLeft, PreviousTube, PreviousDirection, CurrentTube, CurrentDirection, IsExitingDisposals, Tags",
                    new[] { "DisposalHolderComponent", "DisposalUnitComponent" },
                    new List<string>
                    {
                        PathOf("DisposalUnit", 14, 10), PathOf("DisposalTrunk", 14, 10),
                        PathOf("DisposalUnit", 14, 14), PathOf("DisposalTrunk", 14, 14),
                    },
                    () =>
                    {
                        // The parcel waits in the unit until the store is close: it goes into the container directly,
                        // since the unit's own insert arms the automatic flush and would take the route far too early.
                        var parcel = entMan.SpawnEntity(parcelId, At(14, 10));
                        var comp = entMan.GetComponent<DisposalUnitComponent>(nearUnit);
                        var inserted = entMan.System<SharedContainerSystem>().Insert(parcel, comp.Container);
                        return new List<string>
                        {
                            $"{parcelId} {(inserted ? "in" : "NOT in")} the near unit, engaged {comp.Engaged}, "
                            + $"unit powered {entMan.System<PowerReceiverSystem>().IsPowered(nearUnit)}",
                        };
                    })
                {
                    BeforeStore = () =>
                    {
                        var lines = new List<string>();
                        var comp = entMan.GetComponent<DisposalUnitComponent>(nearUnit);
                        var tubes = entMan.System<DisposalTubeSystem>();
                        var disposable = entMan.System<DisposableSystem>();
                        var containers = entMan.System<SharedContainerSystem>();

                        if (!entMan.System<DisposalUnitSystem>().TryFlush(nearUnit, comp))
                        {
                            setupWrong.Add("the near unit would not flush, so no holder was ever in a tube");
                            lines.Add("the flush was refused");
                            return lines;
                        }

                        var holder = EntityUid.Invalid;
                        DisposalHolderComponent? state = null;
                        var holders = entMan.EntityQueryEnumerator<DisposalHolderComponent, TransformComponent>();
                        while (holders.MoveNext(out var uid, out var found, out var xform))
                        {
                            if (xform.GridUid != grid)
                                continue;

                            holder = uid;
                            state = found;
                        }

                        if (state == null)
                        {
                            setupWrong.Add("the flush made no holder on the grid");
                            lines.Add("the flush left no holder");
                            return lines;
                        }

                        // The step the update takes, taken by hand so a previous tube is set by the time of the store:
                        // the tube it is in lets go before the next one takes it, or that insert fails and the holder
                        // leaves disposals instead of moving on (DisposableSystem.cs:267-278).
                        if (state.CurrentTube is { } current
                            && tubes.NextTubeFor(current, state.CurrentDirection) is { } next)
                        {
                            containers.Remove(holder, entMan.GetComponent<DisposalTubeComponent>(current).Contents, reparent: false, force: true);
                            disposable.EnterTube(holder, next, state);
                        }

                        if (!entMan.EntityExists(holder))
                        {
                            setupWrong.Add("the hand-taken step ended the holder's run instead of moving it on");
                            lines.Add("the holder left disposals on the hand-taken step");
                            return lines;
                        }

                        if (state.PreviousTube == null || state.CurrentTube != secondTube)
                        {
                            setupWrong.Add($"the hand-taken step had to leave the holder in the bend at 14,11 with a previous tube, and left it in "
                                           + $"{state.CurrentTube?.ToString() ?? "nothing"} after {state.PreviousTube?.ToString() ?? "nothing"}");
                        }

                        // Where it stands now, which is NOT where it is stored: the live window is 90 ticks and every
                        // tube step is 0.1 s, so the store catches it about thirty tubes further on, and no seam reads
                        // it there. The after-load reading says so rather than passing this tile off as the stored one.
                        storedTile = TileOf(entMan, grid, holder).ToString();
                        holderAir = $"{state.Air.TotalMoles:F2} mol at {state.Air.Temperature:F1} K";
                        lines.Add($"the holder is at {storedTile} at the hand-taken step in tube {state.CurrentTube?.ToString() ?? "nothing"} going {state.CurrentDirection}, "
                                  + $"after {state.PreviousTube?.ToString() ?? "nothing"} going {state.PreviousDirection}, {state.TimeLeft:F3}s of {state.StartingTime:F3}s left, "
                                  + $"exiting {state.IsExitingDisposals}, {state.Tags.Count} tag(s), {state.Air.TotalMoles:F2} mol aboard, "
                                  + $"holding {string.Join(", ", state.Container.ContainedEntities.Select(held => entMan.GetComponent<MetaDataComponent>(held).EntityPrototype?.ID ?? "(no prototype)"))}");
                        lines.Add($"the route is 57 tubes, so about {57 - 2 - LiveWindowTicks / 3} of it are left at the store, and none of it by the second store");
                        return lines;
                    },
                    AfterLoad = loaded =>
                    {
                        var notes = new List<string>();
                        var wrong = new List<string>(setupWrong.Select(line => $"the setup: {line}"));
                        var maps = entMan.System<SharedMapSystem>();

                        var parcels = new List<EntityUid>();
                        var holdersLeft = new List<string>();
                        var all = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
                        while (all.MoveNext(out var uid, out var meta, out var xform))
                        {
                            if (meta.EntityPrototype?.ID == parcelId)
                                parcels.Add(uid);

                            if (entMan.HasComponent<DisposalHolderComponent>(uid))
                                holdersLeft.Add($"{uid} on {(xform.GridUid == loaded ? "the loaded grid" : xform.GridUid?.ToString() ?? "no grid")}");
                        }

                        notes.Add($"the holder was at {storedTile} at the hand-taken step, three seconds of live window before the store, so the tile it was "
                                  + "stored in is about thirty tubes further along and nothing reads it there; the route's end is the far unit at (14, 14)");
                        foreach (var parcel in parcels)
                        {
                            var parcelXform = entMan.GetComponent<TransformComponent>(parcel);
                            if (parcelXform.GridUid != loaded)
                            {
                                notes.Add($"a {parcelId} came back off the loaded grid, on {parcelXform.GridUid?.ToString() ?? "no grid"}");
                                continue;
                            }

                            var tile = TileOf(entMan, loaded, parcel);
                            var anchored = new List<EntityUid>();
                            maps.GetAnchoredEntities((loaded, entMan.GetComponent<MapGridComponent>(loaded)), tile, anchored);
                            var onIt = anchored
                                .Select(held => entMan.GetComponent<MetaDataComponent>(held).EntityPrototype?.ID ?? "(no prototype)")
                                .ToList();

                            // What is anchored on the tile is what says whether a player could pick the parcel up: a
                            // disposal run goes under floors and through walls, and the Surveyor judges the landing.
                            notes.Add($"the {parcelId} ended on tile {tile}, held by {(entMan.System<SharedContainerSystem>().IsEntityInContainer(parcel) ? "a container" : "nothing")}, "
                                      + $"anchored on that tile: {(onIt.Count == 0 ? "nothing" : string.Join(", ", onIt))}");

                            // The holder carries the unit's air and merges it into whatever it is standing in when it
                            // leaves (DisposableSystem.cs:153-157), so the tile it left the parcel on is where a merge
                            // that happened twice would show. Read as moles, not as the presence of an atmosphere.
                            var mixture = entMan.System<AtmosphereSystem>().GetTileMixture(parcel);
                            notes.Add($"the tile the {parcelId} landed on holds {mixture?.TotalMoles.ToString("F2") ?? "no"} mol "
                                      + $"at {mixture?.Temperature.ToString("F1") ?? "no"} K, against the {holderAir} the holder carried at the hand-taken step");
                        }

                        notes.Add($"holders still alive after both loads: {(holdersLeft.Count == 0 ? "none" : string.Join("; ", holdersLeft))}");
                        notes.Add("the second store held a parcel already lying on a tile: the run is 5.7 s and no route survives two trips");

                        if (parcels.Count != 1)
                            wrong.Add($"one {parcelId} has to come back, {parcels.Count} did");
                        else if (entMan.GetComponent<TransformComponent>(parcels[0]).GridUid != loaded)
                            wrong.Add($"the {parcelId} came back off the loaded grid");
                        else if (TileOf(entMan, loaded, parcels[0]) != new Vector2i(14, 14))
                            wrong.Add($"the holder had to carry the {parcelId} to the far unit at (14, 14) and left it at {TileOf(entMan, loaded, parcels[0])}");

                        if (holdersLeft.Count > 0)
                            wrong.Add($"no holder can be left alive once the route has ended, and {holdersLeft.Count} is");

                        return (notes, wrong);
                    },
                });
            }

            return recipes;
        }

        /// <summary>Wave 1's recipes: the manifest members no sold hull exercises.</summary>
        private static List<WorkbenchRecipe> PlaceWave1(TestPair pair, EntityUid grid)
        {
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
                        var active = entMan.GetComponentOrNull<ActiveMicrowaveComponent>(microwave);
                        return new List<string> { $"cooking {active != null}, {WorkbenchLongTimer.TotalSeconds}s set, malfunction time {active?.MalfunctionTime?.ToString() ?? "none"}" };
                    })
                {
                    // The no-deadline control: a cook with nothing metal in it has no malfunction time, and a deadline that
                    // appears across the store, the load or the thaw is already past and makes the microwave explode.
                    AfterLoad = retrieved =>
                    {
                        var notes = new List<string>();
                        var bad = new List<string>();
                        var found = 0;
                        var query = entMan.EntityQueryEnumerator<MicrowaveComponent, TransformComponent>();
                        while (query.MoveNext(out var uid, out var comp, out var xform))
                        {
                            if (xform.GridUid != retrieved)
                                continue;

                            found++;
                            var active = entMan.GetComponentOrNull<ActiveMicrowaveComponent>(uid);
                            notes.Add($"microwave there, broken {comp.Broken}, cooking {active != null}, malfunction time {active?.MalfunctionTime?.ToString() ?? "none"}");
                            if (comp.Broken || active is { } cooking && cooking.MalfunctionTime != null)
                                bad.Add("the microwave has to come back whole, with no malfunction time");
                        }

                        if (found == 0)
                            bad.Add("the microwave has to come back at all");

                        return (notes, bad);
                    },
                });
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

            // 6. A disposal unit engaged with something inside, its flush three minutes off: the regression for row 475, out of
            // the manifest as not reproduced. The flush is a data field the rows carry; only a power-off edge after the load
            // would null it (SharedDisposalUnitSystem.cs:249-252), and the on edge's ManualEngage keeps the smaller of it and a
            // fresh one (:686), so the power receiver's keys are shown beside the unit's.
            {
                var unit = Place(entMan, grid, "DisposalUnit", 13, 5);
                recipes.Add(new WorkbenchRecipe(6, "disposal unit", "DisposalUnit.NextFlush, a data field, out of the manifest",
                    new[] { "DisposalUnitComponent", "ApcPowerReceiverComponent" },
                    new List<string> { PathOf("DisposalUnit", 13, 5) },
                    () =>
                    {
                        var comp = entMan.GetComponent<DisposalUnitComponent>(unit);
                        comp.ManualFlushTime = WorkbenchLongTimer;
                        containers.Insert(entMan.SpawnEntity("FoodBanana", At(13, 5)), comp.Container);
                        entMan.System<SharedDisposalUnitSystem>().ManualEngage(unit, comp);
                        return new List<string> { $"engaged {comp.Engaged}, NextFlush {comp.NextFlush?.ToString() ?? "null"}" };
                    })
                {
                    AfterLoad = retrieved =>
                    {
                        var notes = new List<string>();
                        var bad = new List<string>();
                        var now = server.ResolveDependency<IGameTiming>().CurTime;
                        var found = 0;
                        var query = entMan.EntityQueryEnumerator<DisposalUnitComponent, TransformComponent>();
                        while (query.MoveNext(out _, out var comp, out var xform))
                        {
                            if (xform.GridUid != retrieved)
                                continue;

                            found++;
                            var left = comp.NextFlush - now;
                            notes.Add($"disposal unit engaged {comp.Engaged}, flush in {(left is { } l ? $"{l.TotalSeconds:F1}s" : "none")} of {WorkbenchLongTimer.TotalSeconds}s");
                            // Two round trips run well over half a minute of the ship's clock; a flush lost and re-rolled on a
                            // load is back within a few seconds of the full timer.
                            if (!comp.Engaged || left is not { } remaining || remaining > WorkbenchLongTimer - TimeSpan.FromSeconds(30))
                                bad.Add("the disposal unit has to come back engaged with its flush kept (more than 30 s down after two trips)");
                        }

                        if (found == 0)
                            bad.Add("the disposal unit has to come back at all");

                        return (notes, bad);
                    },
                });
            }

            // 7. A fryer with oil and an item, which the wait before the store fries once.
            {
                var fryer = Place(entMan, grid, "KitchenDeepFryer", 5, 8);
                recipes.Add(new WorkbenchRecipe(7, "deep fryer",
                    "PreventCrisping.Cycles; DeepFried.OriginalName; MetaData.EntityName; DeepFryer.NextFryTime, re-armed by the power edge and accepted",
                    new[] { "DeepFryerComponent", "PreventCrispingComponent", "DeepFriedComponent", "MetaDataComponent" },
                    new List<string> { PathOf("KitchenDeepFryer", 5, 8) },
                    () =>
                    {
                        var comp = entMan.GetComponent<DeepFryerComponent>(fryer);
                        var solutions = entMan.System<SharedSolutionContainerSystem>();
                        var oiled = solutions.TryGetSolution(fryer, comp.SolutionName, out var vat, out _)
                                    && solutions.TryAddReagent(vat.Value, "Cornoil", 50, out _);
                        // A potato slice: two fries turn it into fries, which only then take PreventCrisping, and a third fry
                        // counts one cycle (DeepFryerSystem.cs:292-317). Raw meat would cook into cooked meat on the floor.
                        // And paper beside it: fries never take DeepFried, and paper does, for the fried name and original name.
                        var inserted = containers.Insert(entMan.SpawnEntity("FoodPotatoSlice", At(5, 8)), comp.Storage)
                                       && containers.Insert(entMan.SpawnEntity("Paper", At(5, 8)), comp.Storage);
                        return new List<string> { $"oil added {oiled}, potato slice and paper inserted {inserted}, powered {entMan.System<PowerReceiverSystem>().IsPowered(fryer)}" };
                    })
                {
                    // Three fries by now, one a tick after the start and one each 5 s. A fourth inside the live window would
                    // count another cycle or burn it, so the next fry is put three minutes off: the store then holds fries
                    // with one crisping cycle and a fry pending, whose timer the load's power edge re-arms to a full interval.
                    BeforeStore = () =>
                    {
                        var comp = entMan.GetComponent<DeepFryerComponent>(fryer);
                        // The fryer system's own member (Access), so by reflection.
                        typeof(DeepFryerComponent).GetProperty(nameof(DeepFryerComponent.NextFryTime))!
                            .SetValue(comp, server.ResolveDependency<IGameTiming>().CurTime + WorkbenchLongTimer);
                        var held = comp.Storage.ContainedEntities
                            .Select(item => $"{entMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID ?? "(no prototype)"} named '{entMan.GetComponent<MetaDataComponent>(item).EntityName}'"
                                            + (entMan.TryGetComponent<Content.Server._NF.Kitchen.Components.PreventCrispingComponent>(item, out var crisp) ? $" cycles {crisp.Cycles}" : " no PreventCrisping"))
                            .ToList();
                        return new List<string> { $"basket holds {held.Count}: {string.Join(", ", held)}; vat {comp.Solution.Volume}u; next fry {WorkbenchLongTimer.TotalSeconds}s off" };
                    },
                });
            }

            // 8. Two scuttle devices armed, one of each polarity: the Wyvern keeps its countdown across a map change, the
            // RazorN asks to be disarmed by one (nuke.yml:95), and storage is a map change.
            {
                var keeps = Place(entMan, grid, "ScuttleDeviceWyvern", 8, 8);
                var disarms = Place(entMan, grid, "ScuttleDeviceRazorN", 9, 9);
                recipes.Add(new WorkbenchRecipe(8, "scuttle devices",
                    "ScuttleDevice.RemainingTime, CooldownTime, Armed, PlayedAlertSound, ArmedMap",
                    new[] { "ScuttleDeviceComponent" },
                    new List<string> { PathOf("ScuttleDeviceWyvern", 8, 8), PathOf("ScuttleDeviceRazorN", 9, 9) },
                    () =>
                    {
                        var notes = new List<string>();
                        foreach (var device in new[] { keeps, disarms })
                        {
                            // The RazorN's own timer is 20 s, which a restored countdown would finish inside the run; a long
                            // one keeps the countdown the thing measured, not a detonation.
                            var comp = entMan.GetComponent<ScuttleDeviceComponent>(device);
                            comp.Timer = TimeSpan.FromMinutes(10);
                            comp.RemainingTime = comp.Timer;
                            entMan.System<ScuttleDeviceSystem>().ArmBomb(device);
                            notes.Add($"{entMan.GetComponent<MetaDataComponent>(device).EntityPrototype?.ID} armed {comp.Armed}, disarms on a map change {comp.DisarmOnMapChange}");
                        }

                        return notes;
                    })
                {
                    AfterLoad = retrieved =>
                    {
                        var notes = new List<string>();
                        var bad = new List<string>();
                        var query = entMan.EntityQueryEnumerator<ScuttleDeviceComponent, TransformComponent, MetaDataComponent>();
                        while (query.MoveNext(out _, out var comp, out var xform, out var meta))
                        {
                            if (xform.GridUid != retrieved)
                                continue;

                            var id = meta.EntityPrototype?.ID;
                            notes.Add($"{id}: armed {comp.Armed}, remaining {comp.RemainingTime.TotalSeconds:F1}s of {comp.Timer.TotalSeconds:F0}s, armed map {comp.ArmedMap} (on map {xform.MapID})");
                            // Two round trips run well over half a minute of countdown; one reset to the full timer on a
                            // load leaves it within a few seconds of it.
                            if (id == "ScuttleDeviceWyvern" && (!comp.Armed || comp.ArmedMap != xform.MapID || comp.RemainingTime > comp.Timer - TimeSpan.FromSeconds(30)))
                                bad.Add($"{id} has to come back armed, its countdown kept (more than 30 s down after two trips) and its armed map the map it loaded onto");
                            if (id == "ScuttleDeviceRazorN" && (comp.Armed || comp.RemainingTime != comp.Timer))
                                bad.Add($"{id} has to come back disarmed by never being armed, its countdown at its full timer");
                        }

                        return (notes, bad);
                    },
                });
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

            // A recipe whose members the manifest does not carry cannot differ between the two passes, so it runs once
            // and says so rather than costing a run that proves nothing (ruled 2026-09-19).
            if (!CarriesAManifestMember(recipe))
                sb.AppendLine($"[workbench]   recipe {recipe.Number}: no manifest member: one pass.");

            // How much of each recipe entity, and of what it holds, each snapshot saw: a whole entity missing shows here
            // before any key does.
            foreach (var path in recipe.Paths)
            {
                int Under(Content.Server._Triad.Drydock.DrydockStateSnapshot snapshot, string suffix) =>
                    snapshot.Values.Keys.Count(key => key.StartsWith(path + suffix, StringComparison.Ordinal));
                sb.AppendLine($"[workbench]   keys under {path}: own early/before/after/late "
                              + $"{Under(first.Early, "|")}/{Under(first.Before, "|")}/{Under(first.After, "|")}/{Under(first.Late, "|")}, "
                              + $"held {Under(first.Early, "/")}/{Under(first.Before, "/")}/{Under(first.After, "/")}/{Under(first.Late, "/")} on trip 1");

                var leftInWindow = first.Early.Values.Keys
                    .Where(key => key.StartsWith(path + "/", StringComparison.Ordinal) && !first.Before.Values.ContainsKey(key))
                    .Select(key => key[..key.IndexOf('|')])
                    .Distinct()
                    .Take(4)
                    .ToList();
                if (leftInWindow.Count > 0)
                {
                    sb.AppendLine($"[workbench]   held entities gone between early and before (the live window): {string.Join(", ", leftInWindow)}");
                    var cameInWindow = first.Before.Values.Keys
                        .Where(key => !first.Early.Values.ContainsKey(key) && key.Contains("|MetaDataComponent.<present>", StringComparison.Ordinal))
                        .Select(key => key[..key.IndexOf('|')])
                        .Take(6)
                        .ToList();
                    sb.AppendLine($"[workbench]   entities anywhere on the grid that came in the same window: {(cameInWindow.Count == 0 ? "none" : string.Join(", ", cameInWindow))}");
                }
            }

            var shown = 0;
            foreach (var (trip, result) in new[] { (1, first), (2, second) })
            {
                // A machine's board, parts and sound entities are the same on every machine and would fill the budget.
                var keys = result.Before.Values.Keys.Concat(result.After.Values.Keys)
                    .Where(key => recipe.Paths.Any(path => key.StartsWith(path + "|", StringComparison.Ordinal) || key.StartsWith(path + "/", StringComparison.Ordinal)))
                    .Where(key => !key.Contains("/machine_board/", StringComparison.Ordinal) && !key.Contains("/machine_parts/", StringComparison.Ordinal)
                                  && !key.Contains("/Audio|", StringComparison.Ordinal))
                    .Where(key => recipe.Components.Any(component => key.Contains("|" + component + ".", StringComparison.Ordinal)))
                    .Distinct()
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToList();

                if (keys.Count == 0)
                    sb.AppendLine($"[workbench]   trip {trip}: no key of {string.Join(", ", recipe.Components)} under {string.Join(", ", recipe.Paths)}");

                foreach (var key in keys)
                {
                    if (shown++ >= 60)
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
