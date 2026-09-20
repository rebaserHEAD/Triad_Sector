#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Codec;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.DeviceNetwork.Systems;
using Content.Shared.DeviceNetwork.Components;
using Content.Server.Chemistry.Components;
using Content.Server.Power.Components;
using Content.Server.Pinpointer;
using Content.Server.Power.EntitySystems;
using Content.Shared.APC;
using Content.Shared.Pinpointer;
using Content.Shared.Chemistry;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.EntitySerialization;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Reflection;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The first full loop (<c>LADDER_MODE=codec</c>): the ship stored as the grid image and loaded back by the
    /// engine's own deserializer, driven one phase at a time with the image's rows applied between its component pass
    /// and its startup. That is route 1, the hybrid, ruled on 2026-09-18.
    ///
    /// <para>The loop is server code (<see cref="DrydockImageSystem"/>: the store, the load, the despawn) and this file
    /// holds what measures it: the store's write costs (<see cref="WriteProbe"/>), each load phase's time, the census of
    /// what the walk left out, the leftovers of the despawn, and the notes and failures the ladder prints. Rows travel as
    /// JSON text in memory (<see cref="DrydockImage"/>); there is no database, no store and no retrieve.</para>
    ///
    /// <para>The load goes back onto the hull's own map, as the engine mode does, and not onto a paused staging map. The
    /// whole load runs inside one <c>WaitPost</c>, so no tick can run between its phases, which is what the pause was
    /// for, and a fresh map would lack the atmosphere the rung gives this one. The store's despawn is the drydock's, on a
    /// paused staging map deleted with the hull (<see cref="Despawn"/>), and what it leaves behind is checked before the
    /// load (<see cref="Leftovers"/>).</para>
    ///
    /// <para>What the manifest (F33, <see cref="DrydockCodecManifestMembers"/>) sets, missed or refused, and the appearance
    /// entries the gate refused, come back in the load's result and fail the test once the report has printed
    /// (<see cref="AssertLoopHeld"/>), unless <c>LADDER_MANIFEST_OFF</c> holds members off to show a loss on purpose. What the
    /// first power solve re-arms has no moment; the ladder sorts it as accepted.</para>
    /// </summary>
    public sealed partial class DrydockFidelityLadderLocalTest
    {
        private static readonly bool CodecMode = Environment.GetEnvironmentVariable("LADDER_MODE") == "codec";

        /// <summary>What the codec round trips measured, printed after the rung's report.</summary>
        private static readonly List<string> CodecNotes = new();

        /// <summary>The whole store of the last round trip: the walk, every component's write and its JSON, and the tiles.</summary>
        private static TimeSpan LastStoreTime;

        /// <summary>
        /// The parts of the last store a sliced write has to budget: the id pass (the walk and the id assignment) that
        /// runs whole before the first slice, the tile table and the appearance rows that are units of their own, and
        /// the dearest single component writes (the write and its JSON), which bound how far one slice can overrun.
        /// Garbage collections are counted by generation over the whole store and over each dearest write, because a
        /// collection that lands inside one write is a pause no time check between writes can cut.
        /// </summary>
        private static StoreTimes LastStoreTimes = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, new List<DearWrite>(), default);

        private readonly record struct Collections(int Gen0, int Gen1, int Gen2)
        {
            public static Collections Now() => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

            public Collections Since(Collections start) => new(Gen0 - start.Gen0, Gen1 - start.Gen1, Gen2 - start.Gen2);

            public override string ToString() => $"GC {Gen0}/{Gen1}/{Gen2}";
        }

        private sealed record DearWrite(TimeSpan Time, string Component, string? Prototype, Collections During);

        private sealed record StoreTimes(TimeSpan IdPass, TimeSpan Tiles, TimeSpan Appearance, TimeSpan DearestAppearance, int Writes, List<DearWrite> Dearest, Collections During);

        private const int DearestKept = 3;

        /// <summary>The last load's manifest, for the round trip's notes and failures.</summary>
        private static DrydockManifestApply? LastManifest;

        /// <summary>
        /// <c>LADDER_MANIFEST_OFF</c>: component names or member keys, comma-separated, whose manifest members the load
        /// decodes and does not set, so a recipe shows its predicted loss before the same run shows the member's apply.
        /// </summary>
        private static readonly HashSet<string> ManifestOff = new(
            (Environment.GetEnvironmentVariable("LADDER_MANIFEST_OFF") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

        /// <summary>
        /// What the loop lost or left on this test's round trips. The manifest's, while every member was applied
        /// (<see cref="ManifestOff"/> empty): a member no serializer wrote at the store, one whose component was gone at its
        /// moment, one the load refused. The despawn's, always: an entity it left behind (<see cref="Leftovers"/>). Each is
        /// a failure, not a note (ruled 2026-09-19), raised by <see cref="AssertLoopHeld"/> once the report has printed.
        /// </summary>
        private static readonly List<string> LoopFailures = new();

        private static void AssertLoopHeld()
        {
            var failures = LoopFailures.ToList();
            LoopFailures.Clear();
            Assert.That(failures, Is.Empty,
                "Nothing may be lost or left on the loop: each named here is a manifest member not written at the store, "
                + "one whose component was gone at its moment or one the load refused, with every member applied, or an "
                + "entity the store's despawn left behind.");
        }

        private static string PrototypeOf(IEntityManager entMan, EntityUid uid) =>
            entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID ?? "(no prototype)";

        /// <summary>
        /// The seam's hardest case: a reagent dispenser registers its beaker slot at map init and its storage slots
        /// from its parts (ReagentDispenserSystem.cs:275, :294-313), neither of which runs under the silent map-init
        /// stamp, so its slots come back only if the seam adds them from the row. Its jugs and beaker sit in
        /// containers the row carries, so a restored slot finds its item already inside. No drydock sweep runs:
        /// nothing here calls the drydock.
        /// </summary>
        [Test]
        public async Task TheLoopKeepsADispensersSlots()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var slots = server.System<ItemSlotsSystem>();
            CodecNotes.Clear();
            LoopFailures.Clear();

            var storageIds = new List<string>();
            var stored = new Dictionary<string, string?>();

            await server.WaitPost(() =>
            {
                var dispenser = entMan.SpawnEntity("ChemDispenserEmpty", map.GridCoords);
                var comp = entMan.GetComponent<ReagentDispenserComponent>(dispenser);
                storageIds.AddRange(comp.StorageSlotIds);

                var beaker = entMan.SpawnEntity("Beaker", map.GridCoords);
                slots.TryInsert(dispenser, SharedReagentDispenser.OutputSlotName, beaker, null);
                for (var i = 0; i < 2 && i < storageIds.Count; i++)
                    slots.TryInsert(dispenser, storageIds[i], entMan.SpawnEntity("JugCarbon", map.GridCoords), null);

                foreach (var id in storageIds.Prepend(SharedReagentDispenser.OutputSlotName))
                    stored[id] = slots.TryGetSlot(dispenser, id, out var slot) && slot.Item is { } item
                        ? entMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID
                        : null;
            });

            var loaded = await CodecRoundTrip(pair, map.Grid.Owner);

            var restored = new Dictionary<string, string?>();
            var registered = 0;
            await server.WaitPost(() =>
            {
                var dispensers = entMan.EntityQueryEnumerator<ReagentDispenserComponent, TransformComponent>();
                while (dispensers.MoveNext(out var uid, out _, out var xform))
                {
                    if (xform.GridUid != loaded)
                        continue;

                    foreach (var id in stored.Keys)
                    {
                        if (!slots.TryGetSlot(uid, id, out var slot))
                            continue;

                        registered++;
                        restored[id] = slot.Item is { } item ? entMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID : null;
                    }
                }
            });

            foreach (var note in CodecNotes)
                await TestContext.Out.WriteLineAsync(note);

            Assert.Multiple(() =>
            {
                Assert.That(storageIds, Is.Not.Empty, "The control: the dispenser must have storage slots, from its parts at map init.");
                Assert.That(stored[SharedReagentDispenser.OutputSlotName], Is.EqualTo("Beaker"), "The control: the beaker has to be in its slot before the store.");
                Assert.That(stored.Values.Count(v => v == "JugCarbon"), Is.EqualTo(2), "The control: two jugs have to be in storage slots before the store.");

                Assert.That(registered, Is.EqualTo(stored.Count), "Every slot the dispenser had must be registered again, though nothing that registers them runs.");
                Assert.That(restored, Is.EquivalentTo(stored), "And each must hold what it held.");
            });

            await pair.CleanReturnAsync();
            AssertLoopHeld();
        }

        /// <summary>
        /// F37 through the loader's own path: a member whose prototype authors a value (a deep fryer's solutions, vat_oil
        /// in its YAML) that the live entity holds as null. The load builds the component from the prototype and copies
        /// the row over it, so a row that leaves the null out gives the prototype's value back. The solution container
        /// system nulls the dictionary once it has made the solutions into entities, which is the live null here.
        /// </summary>
        [Test]
        public async Task APrototypeAuthoredValueNulledLiveComesBackNull()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            CodecNotes.Clear();
            LoopFailures.Clear();

            bool authored = false, nullBefore = false;
            var storedVolume = Content.Shared.FixedPoint.FixedPoint2.Zero;
            await server.WaitPost(() =>
            {
                var fryer = entMan.SpawnEntity("KitchenDeepFryer", map.GridCoords);

                // Oil in the vat, so a vat that came back as an empty template reads differently from the real one.
                var solutions = server.System<Content.Shared.Chemistry.EntitySystems.SharedSolutionContainerSystem>();
                if (solutions.TryGetSolution(fryer, "vat_oil", out var vat, out _))
                    solutions.TryAddReagent(vat.Value, "Cornoil", 30, out _);
                storedVolume = VatEntitySolution(entMan, fryer, "vat_oil")?.Volume ?? Content.Shared.FixedPoint.FixedPoint2.Zero;

                authored = entMan.GetComponent<MetaDataComponent>(fryer).EntityPrototype!.Components
                    .TryGetValue("SolutionContainerManager", out var proto)
                    && ((Content.Shared.Chemistry.Components.SolutionManager.SolutionContainerManagerComponent) proto.Component).Solutions is { Count: > 0 };
                nullBefore = entMan.GetComponent<Content.Shared.Chemistry.Components.SolutionManager.SolutionContainerManagerComponent>(fryer).Solutions == null;
            });

            var loaded = await CodecRoundTrip(pair, map.Grid.Owner);

            var fryers = 0;
            var nullAfter = 0;
            var vatIsTheEntity = 0;
            var restoredVolume = Content.Shared.FixedPoint.FixedPoint2.Zero;
            await server.WaitPost(() =>
            {
                var query = entMan.EntityQueryEnumerator<Content.Server.Nyanotrasen.Kitchen.Components.DeepFryerComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out var fryer, out var xform))
                {
                    if (xform.GridUid != loaded)
                        continue;

                    fryers++;
                    if (entMan.GetComponent<Content.Shared.Chemistry.Components.SolutionManager.SolutionContainerManagerComponent>(uid).Solutions == null)
                        nullAfter++;

                    // The fryer caches the solution init hands it; it has to be the one in the solution entity, where the
                    // oil is, not a template beside it.
                    if (VatEntitySolution(entMan, uid, fryer.SolutionName) is { } vat && ReferenceEquals(vat, fryer.Solution))
                        vatIsTheEntity++;

                    restoredVolume = fryer.Solution.Volume;
                }
            });

            foreach (var note in CodecNotes)
                await TestContext.Out.WriteLineAsync(note);

            Assert.Multiple(() =>
            {
                Assert.That(authored, Is.True, "The control: the fryer's prototype has to author its solutions, or this is the initializer case.");
                Assert.That(nullBefore, Is.True, "The control: the live fryer has to hold them as null before the store.");
                Assert.That(storedVolume, Is.GreaterThan(Content.Shared.FixedPoint.FixedPoint2.Zero), "The control: the vat has to hold oil before the store.");
                Assert.That(fryers, Is.EqualTo(1), "One fryer has to come back.");
                Assert.That(nullAfter, Is.EqualTo(1), "Its solutions have to come back null, not as the prototype's vat_oil.");
                Assert.That(vatIsTheEntity, Is.EqualTo(1), "And the vat it caches has to be its solution entity's, not an empty template.");
                Assert.That(restoredVolume, Is.EqualTo(storedVolume), "And that vat has to hold the oil it held.");
            });

            await pair.CleanReturnAsync();
            AssertLoopHeld();
        }

        /// <summary>
        /// The F37 fix's own control: it must not change a first spawn. A fryer spawned the normal way has no solution
        /// container yet at its init, so it still takes its vat from its prototype, and map init makes that an entity.
        /// </summary>
        [Test]
        public async Task AFreshFryerStillTakesItsVatFromThePrototype()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();

            string? vatName = null;
            bool solutionsConverted = false, vatIsTheEntity = false;
            await server.WaitPost(() =>
            {
                var uid = entMan.SpawnEntity("KitchenDeepFryer", map.GridCoords);
                var fryer = entMan.GetComponent<Content.Server.Nyanotrasen.Kitchen.Components.DeepFryerComponent>(uid);
                var vat = VatEntitySolution(entMan, uid, fryer.SolutionName);
                vatName = vat?.Name;
                solutionsConverted = entMan.GetComponent<Content.Shared.Chemistry.Components.SolutionManager.SolutionContainerManagerComponent>(uid).Solutions == null;
                vatIsTheEntity = vat != null && ReferenceEquals(vat, fryer.Solution);
            });

            Assert.Multiple(() =>
            {
                Assert.That(vatName, Is.EqualTo("vat_oil"), "A fresh fryer has to have its prototype's vat as a solution entity.");
                Assert.That(solutionsConverted, Is.True, "Map init has to have made the prototype's solutions into entities.");
                Assert.That(vatIsTheEntity, Is.True, "And the vat it caches has to be that entity's.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A full APC has to come back reading Full. The load starts it the way a spawn does, so its deferred first state
        /// update has to wait for its battery's first sync as a fresh one's does (<c>ApcFirstStateTest</c>): before the power
        /// net's first batch its network battery reads 0 of 0, which computes as Lack, and a full battery then raises
        /// nothing that would recompute it.
        /// </summary>
        [Test]
        public async Task AFullApcComesBackReadingFull()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var second = (int) Math.Ceiling(1 / server.ResolveDependency<IGameTiming>().TickPeriod.TotalSeconds);
            CodecNotes.Clear();
            LoopFailures.Clear();

            EntityUid apc = default;
            await server.WaitPost(() => apc = entMan.SpawnEntity("APCBasic", map.GridCoords));

            // Past the first batch and the state's 1 s gate (ApcSystem.cs:151), then the update opening the APC runs, so
            // the state stored is Full whether or not a spawn gets it right.
            await pair.RunTicksSync(2 * second);
            var before = default(ApcChargeState);
            float charge = 0, max = 0;
            await server.WaitPost(() =>
            {
                server.System<ApcSystem>().UpdateApcState(apc);
                before = entMan.GetComponent<ApcComponent>(apc).LastChargeState;
                var battery = entMan.GetComponent<BatteryComponent>(apc);
                (charge, max) = (battery.CurrentCharge, battery.MaxCharge);
            });

            // Store straight after a batch: zero the network battery and tick until the batch writes it back
            // (BatterySystem.cs:81). Where the load's first tick falls in the batch cycle is then set by the store's clock
            // gap, not by chance, and the control below checks that it falls before the next batch.
            var batch = false;
            for (var i = 0; i < second && !batch; i++)
            {
                await server.WaitPost(() => entMan.GetComponent<PowerNetworkBatteryComponent>(apc).NetworkBattery.Capacity = 0);
                await pair.RunTicksSync(1);
                await server.WaitPost(() => batch = entMan.GetComponent<PowerNetworkBatteryComponent>(apc).NetworkBattery.Capacity > 0);
            }

            var loaded = await CodecRoundTrip(pair, map.Grid.Owner);
            await pair.RunTicksSync(1);

            var restored = new List<EntityUid>();
            var syncedAtFirstTick = true;
            var atFirstTick = default(ApcChargeState);
            await server.WaitPost(() =>
            {
                var query = entMan.EntityQueryEnumerator<ApcComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out _, out var xform))
                {
                    if (xform.GridUid == loaded)
                        restored.Add(uid);
                }

                if (restored.Count != 1)
                    return;

                syncedAtFirstTick = entMan.GetComponent<PowerNetworkBatteryComponent>(restored[0]).NetworkBattery.Capacity > 0;
                atFirstTick = entMan.GetComponent<ApcComponent>(restored[0]).LastChargeState;
            });

            await pair.RunTicksSync(second);

            (ApcChargeState State, bool Pending, float Charge) after = default;
            await server.WaitPost(() =>
            {
                if (restored.Count != 1)
                    return;

                var comp = entMan.GetComponent<ApcComponent>(restored[0]);
                after = (comp.LastChargeState, comp.NeedStateUpdate, entMan.GetComponent<BatteryComponent>(restored[0]).CurrentCharge);
            });

            foreach (var note in CodecNotes)
                await TestContext.Out.WriteLineAsync(note);

            Assert.Multiple(() =>
            {
                Assert.That(charge, Is.EqualTo(max).And.GreaterThan(0), "The control: the APC's battery has to be full before the store.");
                Assert.That(before, Is.EqualTo(ApcChargeState.Full), "The control: the APC has to read Full before the store.");
                Assert.That(batch, Is.True, "The control: the power net has to run a batch within a second.");
                Assert.That(restored, Has.Count.EqualTo(1), "One APC has to come back.");
                Assert.That(syncedAtFirstTick, Is.False, "The control: the loaded APC's first tick has to come before its battery's first sync.");

                Assert.That(atFirstTick, Is.EqualTo(ApcChargeState.Full), "At its first tick it has to still read the Full it was stored with.");
                Assert.That(after.Charge, Is.EqualTo(max), "Its battery has to come back full.");
                Assert.That(after.Pending, Is.False, "Its deferred update has to have run.");
                Assert.That(after.State, Is.EqualTo(ApcChargeState.Full), "And it has to read Full, not the Lack of a battery not yet synced.");
            });

            await pair.CleanReturnAsync();
            AssertLoopHeld();
        }

        /// <summary>The solution held by an entity's solution entity for <paramref name="name"/>, or null when there is none.</summary>
        private static Content.Shared.Chemistry.Components.Solution? VatEntitySolution(IEntityManager entMan, EntityUid uid, string name) =>
            entMan.System<Robust.Shared.Containers.SharedContainerSystem>().TryGetContainer(uid, $"solution@{name}", out var container)
            && container is Robust.Shared.Containers.ContainerSlot { ContainedEntity: { } solutionEntity }
            && entMan.TryGetComponent<Content.Shared.Chemistry.Components.SolutionComponent>(solutionEntity, out var solution)
                ? solution.Solution
                : null;

        private static async Task<EntityUid> CodecRoundTrip(TestPair pair, EntityUid grid)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var timing = server.ResolveDependency<IGameTiming>();
            var system = server.System<DrydockImageSystem>();

            DrydockImageStoreResult stored = default!;
            var mapUid = EntityUid.Invalid;
            var fidelity = server.System<DrydockFidelitySystem>();
            var mobsBefore = 0;
            var mobsUnsavable = 0;
            var droppedUnder = new Dictionary<string, int>();
            var unsavable = new Dictionary<string, int>();
            DespawnWatch despawn = default!;
            var devicesBefore = new Dictionary<long, DeviceMembership>();
            await server.WaitPost(() =>
            {
                (droppedUnder, unsavable) = UnsavableAtTheStore(entMan, grid);

                // The image carries no minds and no corpses, and keeps pets, so a kept living NPC has to round-trip; the
                // census counts them at each end, because a diff with no line for a mob cannot say which it was.
                foreach (var uid in fidelity.GridTreeList(grid))
                {
                    if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype is not { } proto || !proto.ID.StartsWith("Mob", StringComparison.Ordinal))
                        continue;

                    mobsBefore++;
                    if (!proto.MapSavable)
                        mobsUnsavable++;
                }

                mapUid = entMan.GetComponent<TransformComponent>(grid).MapUid!.Value;
                devicesBefore = CaptureDevices(entMan, system, grid);
                var store = System.Diagnostics.Stopwatch.StartNew();
                stored = StoreTimed(entMan, system, grid);
                LastStoreTime = store.Elapsed;
                despawn = Despawn(pair, grid);
            });

            await pair.RunTicksSync((int) Math.Ceiling(ClockGapSeconds / timing.TickPeriod.TotalSeconds));

            // Before the load, so nothing it brings in is taken for a leftover.
            var leftovers = new List<string>();
            await server.WaitPost(() => leftovers = Leftovers(entMan, despawn));

            EntityUid loaded = default;
            DrydockLoadResult result = default!;
            var mobsAfter = 0;
            var deviceLosses = new List<string>();
            LoadTimes loadTimes = default;
            await server.WaitPost(() =>
            {
                (result, loadTimes) = LoadTimed(system, stored.Image, mapUid);
                loaded = result.Grid;
                deviceLosses = DeviceLosses(entMan, result, devicesBefore);
                LastLoadIds = result.Ids.ToDictionary(entry => entry.Key, entry => entry.Value);
                mobsAfter = fidelity.GridTreeList(loaded)
                    .Count(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID.StartsWith("Mob", StringComparison.Ordinal) == true);
            });

            var image = stored.Image;
            var manifest = result.Manifest;
            LastManifest = manifest;
            var trip = CodecNotes.Count(note => note.Contains(" entities stored (", StringComparison.Ordinal)) + 1;
            var unwritable = stored.Unwritable
                .GroupBy(u => $"{u.Member.Key} on {u.Prototype ?? "(no prototype)"}", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            CodecNotes.Add($"[ladder] codec round trip {trip}: {image.Entities.Count} entities stored ({image.Unsaved} unsavable left out with what they held), "
                           + $"{image.Entities.Sum(e => e.Rows.Count)} rows, {image.Bytes} bytes of JSON text; "
                           + $"tiles {result.TilesStored} stored, {result.TilesRestored} restored, {result.TilesMissing} missing, {result.TilesExtra} extra.");
            CodecNotes.Add($"[ladder] codec round trip {trip}: {result.Overwrote} row(s) copied into a component the prototype had added "
                           + $"(its ComponentAdd saw prototype data), {result.Added} added as read, {result.Removed} prototype component(s) removed before init.");
            CodecNotes.Add($"         overwritten, top: {Top(result.OverwroteByType)}");
            CodecNotes.Add($"         removed: {Top(result.RemovedByType)}");
            CodecNotes.Add($"[ladder] codec round trip {trip}: prototype ids the manifest read that no longer resolve, set as null: "
                           + (result.UnresolvedPrototypes.Count == 0 ? "none." : string.Join(", ", result.UnresolvedPrototypes.Select(u => $"{u.Member.Key} '{u.Id}'")) + "."));
            var severed = result.Severed
                .GroupBy(entry => entry.Nullable ? entry.Member : $"{entry.Member} (not nullable)", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            CodecNotes.Add($"[ladder] codec round trip {trip}: references the image could not keep: "
                           + $"{result.Severed.Count(entry => entry.Nullable)} in nullable members, read as null, and "
                           + $"{result.Severed.Count(entry => !entry.Nullable)} in members that are not, left invalid"
                           + (severed.Count == 0 ? "." : ": " + Top(severed) + "."));
            CodecNotes.Add($"[ladder] codec round trip {trip}: queued lathe batches left out for a recipe that no longer resolves: "
                           + (result.DroppedBatches.Count == 0 ? "none." : Top(result.DroppedBatches.GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)) + "."));
            CodecNotes.Add($"[ladder] codec round trip {trip}: {result.HeldBackSlots} item slot(s) held back from init; at the seam {result.CopiedAtSeam} copied into the slot init re-added; "
                           + $"after startup {result.CopiedAfterStartup} copied into the slot startup re-added, {result.AddedWhole} added whole because nothing re-added them.");
            CodecNotes.Add($"[ladder] codec round trip {trip}: load by phase, {image.Entities.Count} entities: rows parsed {loadTimes.Begin.TotalMilliseconds:F0} ms, "
                           + $"skeleton and CreateEntities {loadTimes.Create.TotalMilliseconds:F0} ms, rows and re-parent {loadTimes.Rows.TotalMilliseconds:F0} ms, "
                           + $"StartEntities {loadTimes.Start.TotalMilliseconds:F0} ms; the store took {LastStoreTime.TotalMilliseconds:F0} ms.");
            var parts = LastStoreTimes;
            CodecNotes.Add($"[ladder] codec round trip {trip}: store parts: id pass {parts.IdPass.TotalMilliseconds:F1} ms (un-sliced), "
                           + $"tile table {parts.Tiles.TotalMilliseconds:F1} ms, appearance rows {parts.Appearance.TotalMilliseconds:F1} ms "
                           + $"(dearest one {parts.DearestAppearance.TotalMilliseconds:F2} ms), {parts.Writes} component write(s), {parts.During} over the store; dearest single writes: "
                           + string.Join(", ", parts.Dearest.Select(d => $"{d.Component} on {d.Prototype ?? "(no prototype)"} {d.Time.TotalMilliseconds:F2} ms ({d.During})"))
                           + ".");
            CodecNotes.Add($"[ladder] codec round trip {trip}: {result.AppearanceApplied} appearance entr(y/ies) set before init; "
                           + $"not stored, by value type: {Top(stored.AppearanceSkipped)}; refused by the gate: {Top(result.AppearanceRefused)}; "
                           + $"{result.AppearanceStillDirty} of {result.AppearanceComponents} appearance component(s) still marked modified in the load's tick.");

            CodecNotes.Add($"[ladder] codec round trip {trip}: manifest members set before init {manifest.Set(DrydockApplyMoment.BeforeInit)}, "
                           + $"at the seam {manifest.Set(DrydockApplyMoment.Seam)}, after startup {manifest.Set(DrydockApplyMoment.AfterStart)} "
                           + $"({Top(manifest.LaterByMember)}); component gone at its moment: {Top(manifest.Missing)}; "
                           + $"not written at the store: {Top(unwritable)}; components stripped at the store: {Top(stored.Stripped)}.");
            CodecNotes.Add($"[ladder] codec round trip {trip}: cable receivers: {manifest.Repaired} re-paired with the stored provider, "
                           + $"{manifest.AlreadyPaired} already on it, {manifest.StoredUnpaired} stored unpaired and still so; refused: {Top(manifest.Refused)}.");
            CodecNotes.Add($"[ladder] codec round trip {trip}: seam members by prototype: {Top(manifest.SeamByPrototype)}.");
            if (ManifestOff.Count > 0)
                CodecNotes.Add($"[ladder] codec round trip {trip}: manifest held off by LADDER_MANIFEST_OFF ({string.Join(",", ManifestOff)}): {Top(manifest.OffByKey)}.");

            // With every member applied, a lost one fails the test once its report has printed, where holding members off
            // is how a loss is shown on purpose.
            if (ManifestOff.Count == 0)
            {
                var lost = stored.Unwritable
                    .GroupBy(u => u.ToString(), StringComparer.Ordinal)
                    .Select(g => $"not written at the store: {g.Key} x{g.Count()}")
                    .Concat(manifest.Missing.Select(m => $"component gone at its moment: {m.Key} x{m.Value}"))
                    .Concat(manifest.Refused.Select(r => $"refused by the load: {r.Key} x{r.Value}"))
                    .Concat(result.AppearanceRefused.Select(a => $"appearance type refused by the gate: {a.Key} x{a.Value}"))
                    .Select(line => $"codec round trip {trip}: {line}")
                    .ToList();
                LoopFailures.AddRange(lost);
                CodecNotes.AddRange(lost.Select(line => $"[ladder] MANIFEST FAILURE {line}"));
            }

            // H18: every device that was in its network at the store is in it again after the load, at the address it had. A
            // device that was out, by a disconnect or an unrequested server, is not required to join.
            CodecNotes.Add($"[ladder] codec round trip {trip}: device network: {devicesBefore.Count} device(s) aboard, "
                           + $"{devicesBefore.Values.Count(d => d.Connected)} in their network at the store, {deviceLosses.Count} not back in it at their address.");
            var deviceFailures = deviceLosses.Select(line => $"codec round trip {trip}: device network: {line}").ToList();
            LoopFailures.AddRange(deviceFailures);
            CodecNotes.AddRange(deviceFailures.Select(line => $"[ladder] DEVICE NETWORK FAILURE {line}"));

            // Whatever the manifest does, a leftover of the despawn fails.
            CodecNotes.Add($"[ladder] codec round trip {trip}: despawn "
                           + (DespawnGridOnly ? "of the grid alone (LADDER_DESPAWN=grid-only, the control)" : "with its staging map")
                           + $": {despawn.Hull.Count} entities in the hull, {despawn.Made.Count} made during it, {leftovers.Count} left over.");
            var left = leftovers.Select(line => $"codec round trip {trip}: despawn: {line}").ToList();
            LoopFailures.AddRange(left);
            CodecNotes.AddRange(left.Select(line => $"[ladder] DESPAWN FAILURE {line}"));

            var mobsStored = image.Entities.Count(e => e.Prototype?.StartsWith("Mob", StringComparison.Ordinal) == true);
            CodecNotes.Add($"[ladder] mob census: {mobsBefore} Mob* entit(y/ies) aboard before the store ({mobsUnsavable} of them unsavable), "
                           + $"{mobsStored} in the image, {mobsAfter} after the load.");
            CodecNotes.Add($"[ladder] dropped with an unsavable parent: {droppedUnder.Values.Sum()} entit(y/ies)"
                           + (droppedUnder.Count == 0 ? "." : ": " + string.Join(", ", droppedUnder.OrderByDescending(d => d.Value).ThenBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key} x{d.Value}")) + "."));
            CodecNotes.Add($"[ladder] unsavable at the store: {unsavable.Values.Sum()} entit(y/ies), by prototype"
                           + (unsavable.Count == 0 ? ": none." : ": " + Top(unsavable) + "."));
            return loaded;
        }

        /// <summary>A device's place in its network at the store: whether it was in it, and the address it held.</summary>
        private readonly record struct DeviceMembership(string Prototype, bool Connected, string Address);

        /// <summary>Every device aboard by the stable id the store will give it, with whether it is in its network now.</summary>
        private static Dictionary<long, DeviceMembership> CaptureDevices(IEntityManager entMan, DrydockImageSystem system, EntityUid grid)
        {
            var networks = entMan.System<DeviceNetworkSystem>();
            var devices = new Dictionary<long, DeviceMembership>();
            foreach (var (uid, id) in system.Walk(grid).Ids)
            {
                if (entMan.TryGetComponent<DeviceNetworkComponent>(uid, out var device))
                    devices[id] = new DeviceMembership(PrototypeOf(entMan, uid), networks.IsDeviceConnected(uid, device), device.Address);
            }

            return devices;
        }

        /// <summary>The devices that were in their network at the store and are not in it after the load, or are at another address.</summary>
        private static List<string> DeviceLosses(IEntityManager entMan, DrydockLoadResult result, Dictionary<long, DeviceMembership> before)
        {
            var networks = entMan.System<DeviceNetworkSystem>();
            var byId = result.Ids.ToDictionary(entry => entry.Value, entry => entry.Key);
            var losses = new List<string>();
            foreach (var (id, was) in before.Where(entry => entry.Value.Connected))
            {
                if (!byId.TryGetValue(id, out var uid) || !entMan.TryGetComponent<DeviceNetworkComponent>(uid, out var device))
                {
                    losses.Add($"{was.Prototype} (id {id}) did not come back");
                    continue;
                }

                if (!networks.IsDeviceConnected(uid, device))
                    losses.Add($"{was.Prototype} (id {id}) is not in its network");
                else if (device.Address != was.Address)
                    losses.Add($"{was.Prototype} (id {id}) is at {device.Address}, not {was.Address}");
            }

            return losses;
        }

        /// <summary>Each phase of the last load, timed here because the server loader times nothing.</summary>
        private readonly record struct LoadTimes(TimeSpan Begin, TimeSpan Create, TimeSpan Rows, TimeSpan Start);

        private static (DrydockLoadResult Result, LoadTimes Times) LoadTimed(DrydockImageSystem system, DrydockImage image, EntityUid mapUid)
        {
            // LADDER_MANIFEST_OFF: members it names are decoded and not set, so a recipe shows its predicted loss.
            var options = new DrydockLoadOptions
            {
                HoldOff = ManifestOff.Count == 0
                    ? null
                    : member => ManifestOff.Contains(member.Component) || ManifestOff.Contains(member.Key),
            };

            var phase = System.Diagnostics.Stopwatch.StartNew();
            var session = system.BeginLoad(image, mapUid, options);
            var begin = phase.Elapsed;
            phase.Restart();
            session.CreateEntities();
            var create = phase.Elapsed;
            phase.Restart();
            session.ApplyRows();
            var rows = phase.Elapsed;
            phase.Restart();
            session.Start();
            var start = phase.Elapsed;
            return (session.Complete(), new LoadTimes(begin, create, rows, start));
        }

        /// <summary>
        /// The write costs the server store leaves to its caller: it calls this probe around each row and times nothing itself.
        /// The dearest component writes (the write and its JSON) are kept, each with the collections that landed inside it,
        /// and the appearance rows are summed and their dearest kept.
        /// </summary>
        private sealed class WriteProbe : IDrydockStoreProbe
        {
            private readonly IEntityManager _entMan;
            private readonly System.Diagnostics.Stopwatch _watch = new();
            private Collections _startCollections;

            public readonly List<DearWrite> Dearest = new();
            public TimeSpan Appearance;
            public TimeSpan DearestAppearance;

            public WriteProbe(IEntityManager entMan) => _entMan = entMan;

            public void Before(EntityUid uid, string row)
            {
                _startCollections = Collections.Now();
                _watch.Restart();
            }

            public void After(EntityUid uid, string row)
            {
                var cost = _watch.Elapsed;
                if (row == DrydockImageSystem.AppearanceRow)
                {
                    Appearance += cost;
                    if (cost > DearestAppearance)
                        DearestAppearance = cost;

                    return;
                }

                if (row == DrydockCodec.ManifestRow)
                    return;

                if (Dearest.Count < DearestKept || cost > Dearest[^1].Time)
                {
                    Dearest.Add(new DearWrite(cost, row, _entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID, Collections.Now().Since(_startCollections)));
                    Dearest.Sort((a, b) => b.Time.CompareTo(a.Time));
                    if (Dearest.Count > DearestKept)
                        Dearest.RemoveAt(DearestKept);
                }
            }
        }

        /// <summary>The server store, timed by its parts: the id pass, every entity's rows through a <see cref="WriteProbe"/>, and the tile table.</summary>
        private static DrydockImageStoreResult StoreTimed(IEntityManager entMan, DrydockImageSystem system, EntityUid grid)
        {
            var storeCollections = Collections.Now();
            var probe = new WriteProbe(entMan);
            var part = System.Diagnostics.Stopwatch.StartNew();
            var session = system.BeginStore(grid, probe);
            var idPass = part.Elapsed;

            foreach (var uid in session.Aboard)
                session.WriteEntity(uid);

            part.Restart();
            session.WriteTiles();
            var tiles = part.Elapsed;

            var result = session.Complete();
            LastStoreTimes = new StoreTimes(idPass, tiles, probe.Appearance, probe.DearestAppearance, result.Writes, probe.Dearest, Collections.Now().Since(storeCollections));
            return result;
        }

        /// <summary>
        /// <c>LADDER_DESPAWN=grid-only</c>: the despawn's control, the hull deleted where it stands, as this loop did before
        /// it despawned the way the store does.
        /// </summary>
        private static readonly bool DespawnGridOnly = Environment.GetEnvironmentVariable("LADDER_DESPAWN") == "grid-only";

        /// <summary>Every entity that was in the hull at the store, with its prototype, and every one made during the despawn.</summary>
        private sealed record DespawnWatch(Dictionary<EntityUid, string> Hull, List<EntityUid> Made);

        /// <summary>
        /// The store's despawn, run by the server loader (<see cref="DrydockImageSystem.Despawn"/>: the hull moved onto a fresh
        /// paused staging map, then the grid and the map deleted), with the watch the leftovers are read against: every entity
        /// that was in the hull, and every one made during the despawn. Whatever a terminate handler throws out of the hull, a
        /// disposal unit's contents (DisposalUnitSystem.cs:41-44) among them, lands on the staging map and goes with it.
        /// </summary>
        private static DespawnWatch Despawn(TestPair pair, EntityUid grid)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var watch = new DespawnWatch(
                server.System<DrydockFidelitySystem>().GridTreeList(grid).ToDictionary(uid => uid, uid => PrototypeOf(entMan, uid)),
                new List<EntityUid>());

            void Made(Entity<MetaDataComponent> entity) => watch.Made.Add(entity.Owner);

            entMan.EntityAdded += Made;
            try
            {
                server.System<DrydockImageSystem>().Despawn(grid, stagingMap: !DespawnGridOnly);
                return watch;
            }
            finally
            {
                entMan.EntityAdded -= Made;
            }
        }

        /// <summary>
        /// What the despawn left, read after the clock gap and before the load: an entity that was in the hull and still
        /// exists, or one made during the despawn that outlives it (a respawn, a puddle, a ghost, debris on another map),
        /// each named with where it is now. Each fails the test (ruled 2026-09-19).
        /// </summary>
        private static List<string> Leftovers(IEntityManager entMan, DespawnWatch watch)
        {
            var left = new List<string>();
            foreach (var (uid, prototype) in watch.Hull)
            {
                if (entMan.EntityExists(uid))
                    left.Add($"{prototype} {uid} was in the hull at the store and is still here, {WhereIs(entMan, uid)}");
            }

            foreach (var uid in watch.Made)
            {
                if (entMan.EntityExists(uid))
                    left.Add($"{PrototypeOf(entMan, uid)} {uid} was made during the despawn and outlives it, {WhereIs(entMan, uid)}");
            }

            return left;
        }

        private static string WhereIs(IEntityManager entMan, EntityUid uid)
        {
            var xform = entMan.GetComponent<TransformComponent>(uid);
            return xform.ParentUid.IsValid()
                ? $"on map {xform.MapID} under {PrototypeOf(entMan, xform.ParentUid)} {xform.ParentUid} at {xform.LocalPosition}"
                : "in nullspace";
        }

        /// <summary>
        /// What the store's walk leaves out without choosing to: every entity under an unsavable one, which goes with its
        /// parent whatever it is itself (an item in a pet's hands, a bag on a wheelchair). The unsavable entities are the
        /// engine's rule; what rides under them is cargo the scope rule does not excuse, so it is counted by prototype.
        ///
        /// <para>The unsavable entities themselves are counted by prototype too, because most of them are the sounds
        /// playing at the store, an alarm or a machine's start-up, and their number moves from run to run. Counted
        /// apart, that movement reads as what it is rather than as the store losing something new.</para>
        /// </summary>
        private static (Dictionary<string, int> Dropped, Dictionary<string, int> Unsavable) UnsavableAtTheStore(IEntityManager entMan, EntityUid grid)
        {
            var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
            var unsavableByPrototype = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new Stack<(EntityUid Uid, bool UnderUnsavable)>();
            stack.Push((grid, false));
            while (stack.TryPop(out var entry))
            {
                var unsavable = entMan.GetComponent<MetaDataComponent>(entry.Uid).EntityPrototype is { MapSavable: false };
                var proto = entMan.GetComponent<MetaDataComponent>(entry.Uid).EntityPrototype?.ID ?? "(no prototype)";
                if (entry.UnderUnsavable)
                    dropped[proto] = dropped.GetValueOrDefault(proto) + 1;
                else if (unsavable)
                    unsavableByPrototype[proto] = unsavableByPrototype.GetValueOrDefault(proto) + 1;

                var children = entMan.GetComponent<TransformComponent>(entry.Uid).ChildEnumerator;
                while (children.MoveNext(out var child))
                    stack.Push((child, entry.UnderUnsavable || unsavable));
            }

            return (dropped, unsavableByPrototype);
        }

        /// <summary>
        /// The deep snapshot's tie-break in codec mode (DrydockFidelitySystem.DeepSnapshot.cs, DeepPaths): before a
        /// store, the id the store's walk will give; after a load, the id the image carried, from the deserializer's
        /// map. Two anchored pipes on one tile tie on everything a path is built from, and without this a load that
        /// walks them the other way swaps their states (112 of 112 such pairs were permutations on their tile,
        /// 2026-09-18). Null outside codec mode, which leaves the walk order as it was.
        /// </summary>
        private static Func<EntityUid, long?>? TieBreakBefore(IEntityManager entMan, EntityUid grid)
        {
            if (!CodecMode)
                return null;

            var ids = entMan.System<DrydockImageSystem>().Walk(grid).Ids;
            return uid => ids.TryGetValue(uid, out var id) ? id : null;
        }

        private static Func<EntityUid, long?>? TieBreakAfter()
        {
            if (!CodecMode || LastLoadIds is not { } ids)
                return null;

            return uid => ids.TryGetValue(uid, out var id) ? id : null;
        }

        /// <summary>The last load's entities by the stable id each was loaded under.</summary>
        private static Dictionary<EntityUid, long>? LastLoadIds;

        private static string Top(IReadOnlyDictionary<string, int> counts) =>
            counts.Count == 0
                ? "none"
                : string.Join(", ", counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(12).Select(kv => $"{kv.Key} x{kv.Value}"));

        /// <summary>One row of the predicted table, written before the run from the census join's rows.</summary>
        private sealed record PredictedRow(string Source, string Group, string Component, string Member);

        /// <summary>
        /// <c>LADDER_PREDICTED</c> names the table. Groups the ladder already sorts (volatile, live, unsaved) are read and
        /// not matched, so a line in one of them is sorted as it always was rather than hidden as predicted.
        /// </summary>
        private static readonly Lazy<List<PredictedRow>> Predicted = new(() =>
        {
            var path = Environment.GetEnvironmentVariable("LADDER_PREDICTED");
            if (string.IsNullOrEmpty(path))
                return new List<PredictedRow>();

            var rows = new List<PredictedRow>();
            string[]? header = null;
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (line.StartsWith('#') || line.Length == 0)
                    continue;

                var cells = line.Split('\t');
                if (header == null)
                {
                    header = cells;
                    continue;
                }

                string Cell(string name) => cells[Array.IndexOf(header, name)];
                rows.Add(new PredictedRow(Cell("source"), Cell("group"), Cell("component"), Cell("member")));
            }

            return rows;
        });

        private static List<PredictedRow> PredictedFor(string line)
        {
            var kind = KindOf(line).Replace("~", string.Empty);
            var member = kind[(kind.IndexOf(' ') + 1)..];

            return Predicted.Value
                .Where(row => !row.Group.StartsWith("may differ", StringComparison.Ordinal))
                .Where(row => member.StartsWith($"{row.Component}.{row.Member}", StringComparison.Ordinal)
                              || (row.Member.Contains('.') && member.Contains(row.Member, StringComparison.Ordinal)))
                .ToList();
        }

        /// <summary>
        /// The predicted rows that appeared, and the ones that did not, split by whether the hull carries the
        /// component at all: a prediction about a component the hull does not have says nothing either way.
        /// </summary>
        private static void AppendPredicted(System.Text.StringBuilder sb, int trip, HashSet<PredictedRow> seen, RoundTripResult result)
        {
            var matchable = Predicted.Value.Where(row => !row.Group.StartsWith("may differ", StringComparison.Ordinal)).ToList();
            var carried = result.Before.Values.Keys
                .Select(key => key[(key.IndexOf('|') + 1)..])
                .Select(member => member.TrimStart('~'))
                .Select(member => member.IndexOf('.') is var dot and > 0 ? member[..dot] : member)
                .ToHashSet(StringComparer.Ordinal);

            var absent = matchable.Where(row => !seen.Contains(row)).ToList();
            var absentAboard = absent.Where(row => carried.Contains(row.Component)).ToList();

            sb.AppendLine($"[ladder] predicted (trip {trip}): {matchable.Count} matchable row(s), {seen.Count} appeared, "
                          + $"{absentAboard.Count} did not though the hull carries the component, {absent.Count - absentAboard.Count} about components the hull does not carry.");

            foreach (var row in seen.OrderBy(r => r.Group, StringComparer.Ordinal).ThenBy(r => r.Source, StringComparer.Ordinal))
                sb.AppendLine($"         appeared: {row.Source} [{row.Group}] {row.Component}.{row.Member}");

            foreach (var row in absentAboard.OrderBy(r => r.Group, StringComparer.Ordinal).ThenBy(r => r.Source, StringComparer.Ordinal))
                sb.AppendLine($"         absent:   {row.Source} [{row.Group}] {row.Component}.{row.Member}");
        }
    }
}
