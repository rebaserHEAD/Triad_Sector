#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock.Codec;
using Content.Server.Chemistry.Components;
using Content.Shared.Chemistry;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.EntitySerialization;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
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
    /// <para>LOCAL SCAFFOLDING. The loader lives here until it is ruled into the server, and nothing in it is the
    /// drydock's. Rows travel as JSON text in memory; there is no database, no store and no retrieve.</para>
    ///
    /// <para>The load, in order. The engine's steps are numbered as in
    /// <c>resources/2026-09-18-engine-loader-ordering.md</c>.
    /// <list type="number">
    /// <item>A skeleton document: every stored entity under its prototype, with its stable id as the yaml uid and its
    /// recorded <c>mapInit</c> and <c>paused</c>, and the grid's own grid component carrying the tile table's chunks.
    /// Nothing else, because every other component comes from the image's rows.</item>
    /// <item><c>TryProcessData</c> and <c>CreateEntities</c> (<c>EntityDeserializer.cs:153</c>, <c>:183</c>): the
    /// engine allocates everyone and adds each prototype's components (steps 4 and 6b), and reads the tiles with its
    /// own chunk reader, tile-change and collision work suppressed.</item>
    /// <item>The rows, through <see cref="DrydockCodec"/> under its own context, whose references resolve through the
    /// engine's <c>UidMap</c> (<c>:93</c>). A component the entity already has, which the prototype put there, is
    /// read into a temporary and copied in, which is the engine's own path for one (<c>:693-694</c>); one it lacks is
    /// added as read, or added fresh and copied into when it has serialization hooks, as the engine does
    /// (<c>:670-685</c>); and a prototype component with no row is removed.</item>
    /// <item>The grid re-parented onto the map with <c>SetCoordinates</c>, in the same gap the engine's own merge
    /// uses (<c>MapLoaderSystem.Load.cs:192</c>, <c>MapLoaderSystem.LoadMap.cs:254-274</c>).</item>
    /// <item><c>StartEntities</c> (<c>:213</c>): parents first, init then startup per entity, then the map-init
    /// stamp with no event (<c>:1019-1036</c>) and the pause stamp.</item>
    /// </list></para>
    ///
    /// <para>One departure from the design stop: the load goes back onto the hull's own map, as the engine mode does,
    /// and not onto a paused staging map. The whole load runs inside one <c>WaitPost</c>, so no tick can run between
    /// its phases, which is what the pause was for, and a fresh map would lack the atmosphere the rung gives this
    /// one.</para>
    ///
    /// <para>The seam between each entity's init and its startup (<c>EntityInitialized</c>) carries one thing today:
    /// the item slots held back from init (<see cref="HoldBackSlots"/>). The manifest's members that are not data
    /// fields (F33) are not carried yet; a copy only ever moves data fields, so such a member would get in by being
    /// set before init, or at the seam for one an init handler resets.</para>
    /// </summary>
    public sealed partial class DrydockFidelityLadderLocalTest
    {
        private static readonly bool CodecMode = Environment.GetEnvironmentVariable("LADDER_MODE") == "codec";

        /// <summary>What the codec round trips measured, printed after the rung's report.</summary>
        private static readonly List<string> CodecNotes = new();

        private sealed record CodecEntity(long Id, string? Prototype, bool MapInitialized, bool Paused, Dictionary<string, string> Rows);

        private sealed record CodecImage(long GridId, List<CodecEntity> Entities, string Tiles, int Unsaved, int Bytes);

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
        }

        private static async Task<EntityUid> CodecRoundTrip(TestPair pair, EntityUid grid)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var timing = server.ResolveDependency<IGameTiming>();

            CodecImage image = default!;
            var mapUid = EntityUid.Invalid;
            await server.WaitPost(() =>
            {
                mapUid = entMan.GetComponent<TransformComponent>(grid).MapUid!.Value;
                image = CodecStore(pair, grid);
                entMan.DeleteEntity(grid);
            });

            await pair.RunTicksSync((int) Math.Ceiling(ClockGapSeconds / timing.TickPeriod.TotalSeconds));

            EntityUid loaded = default;
            await server.WaitPost(() => loaded = CodecLoad(pair, image, mapUid));
            return loaded;
        }

        /// <summary>
        /// Every saved entity aboard, walked from the grid as the corpus harness walks it, each component the engine
        /// would save written by the codec and carried as JSON text. An entity whose prototype is not savable is left
        /// out with everything under it, as the engine's own save does, and a reference to it is off the image.
        /// </summary>
        private static CodecImage CodecStore(TestPair pair, EntityUid grid)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var factory = server.ResolveDependency<IComponentFactory>();
            var tileDefs = server.ResolveDependency<ITileDefinitionManager>();

            var aboard = new List<EntityUid>();
            var unsaved = 0;
            var stack = new Stack<EntityUid>();
            stack.Push(grid);
            while (stack.TryPop(out var uid))
            {
                if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype is { MapSavable: false })
                {
                    unsaved++;
                    continue;
                }

                aboard.Add(uid);
                var children = entMan.GetComponent<TransformComponent>(uid).ChildEnumerator;
                while (children.MoveNext(out var child))
                    stack.Push(child);
            }

            var ids = new Dictionary<EntityUid, long>();
            for (var i = 0; i < aboard.Count; i++)
                ids[aboard[i]] = i + 1;

            var codec = new DrydockCodec(
                server.ResolveDependency<ISerializationManager>(),
                entMan,
                server.ResolveDependency<IGameTiming>(),
                uid => ids.TryGetValue(uid, out var id) ? id : null,
                _ => throw new InvalidOperationException("The store resolves nothing."));

            var entities = new List<CodecEntity>();
            var bytes = 0;
            foreach (var uid in aboard)
            {
                var meta = entMan.GetComponent<MetaDataComponent>(uid);
                var rows = new Dictionary<string, string>();
                foreach (var component in entMan.GetComponents(uid))
                {
                    var registration = factory.GetRegistration(component.GetType());
                    if (registration.Unsaved)
                        continue;

                    var text = DrydockNodeJson.Encode(codec.Write((uid, meta), component)).ToJsonString();
                    rows[registration.Name] = text;
                    bytes += text.Length;
                }

                entities.Add(new CodecEntity(
                    ids[uid],
                    meta.EntityPrototype?.ID,
                    meta.EntityLifeStage >= EntityLifeStage.MapInitialized,
                    meta.EntityPaused,
                    rows));
            }

            // The chunk size is an internal data field, so it is taken from the grid component's own row, which carries it
            // when it is not the default (MapGridComponent.cs:45-46).
            var gridComp = entMan.GetComponent<MapGridComponent>(grid);
            var gridRow = (MappingDataNode) DrydockNodeJson.Decode(JsonNode.Parse(entities.Single(e => e.Id == ids[grid]).Rows["MapGrid"])!);
            var chunkSize = gridRow.TryGet<ValueDataNode>("chunkSize", out var sizeNode)
                ? ushort.Parse(sizeNode.Value, System.Globalization.CultureInfo.InvariantCulture)
                : MapGridComponent.DefaultChunkSize;

            var tiles = DrydockTileTable.Write(
                chunkSize,
                server.System<SharedMapSystem>().GetAllTiles(grid, gridComp).Select(tile => (tile.GridIndices, tile.Tile)),
                id => tileDefs[id].ID);

            var tilesText = DrydockNodeJson.Encode(tiles).ToJsonString();
            return new CodecImage(ids[grid], entities, tilesText, unsaved, bytes + tilesText.Length);
        }

        private static EntityUid CodecLoad(TestPair pair, CodecImage image, EntityUid mapUid)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var factory = server.ResolveDependency<IComponentFactory>();
            var serialization = server.ResolveDependency<ISerializationManager>();
            var tileDefs = server.ResolveDependency<ITileDefinitionManager>();

            var tileTable = (MappingDataNode) DrydockNodeJson.Decode(JsonNode.Parse(image.Tiles)!);
            var rows = image.Entities.ToDictionary(
                entity => entity.Id,
                entity => entity.Rows.ToDictionary(
                    row => row.Key,
                    row => (MappingDataNode) DrydockNodeJson.Decode(JsonNode.Parse(row.Value)!)));

            // 1. The skeleton.
            var groups = new SortedDictionary<string, SequenceDataNode>(StringComparer.Ordinal);
            foreach (var entity in image.Entities)
            {
                var node = new MappingDataNode
                {
                    ["uid"] = new ValueDataNode(entity.Id.ToString()),
                    ["mapInit"] = new ValueDataNode(entity.MapInitialized ? "true" : "false"),
                    ["paused"] = new ValueDataNode(entity.Paused ? "true" : "false"),
                };

                if (entity.Id == image.GridId)
                {
                    var gridRow = rows[entity.Id]["MapGrid"].Copy();
                    gridRow["type"] = new ValueDataNode("MapGrid");
                    gridRow["chunks"] = tileTable.Get<MappingDataNode>(DrydockTileTable.ChunksKey).Copy();
                    node["components"] = new SequenceDataNode { gridRow };
                }

                var key = entity.Prototype ?? string.Empty;
                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = new SequenceDataNode();

                group.Add(node);
            }

            var entityGroups = new SequenceDataNode();
            foreach (var (prototype, members) in groups)
                entityGroups.Add(new MappingDataNode { ["proto"] = new ValueDataNode(prototype), ["entities"] = members });

            var gridYamlId = new ValueDataNode(image.GridId.ToString());
            var document = new MappingDataNode
            {
                ["meta"] = new MappingDataNode { ["format"] = new ValueDataNode("7"), ["category"] = new ValueDataNode("Grid") },
                ["maps"] = new SequenceDataNode(),
                ["grids"] = new SequenceDataNode { gridYamlId },
                ["orphans"] = new SequenceDataNode { gridYamlId.Copy() },
                ["nullspace"] = new SequenceDataNode(),
                ["tilemap"] = tileTable.Get<MappingDataNode>(DrydockTileTable.TileMapKey).Copy(),
                ["entities"] = entityGroups,
            };

            // 2. The engine's allocation and component pass.
            // The entity-system collection, not the root one: the deserializer injects systems (SharedMapSystem among
            // them), and MapLoaderSystem hands it its own injected collection, which is this one.
            var deserializer = new EntityDeserializer(
                server.ResolveDependency<IEntitySystemManager>().DependencyCollection,
                document,
                new DeserializationOptions());
            if (!deserializer.TryProcessData())
                throw new InvalidOperationException("Codec loop: the engine refused the skeleton document.");

            deserializer.CreateEntities();
            var gridUid = deserializer.UidMap[(int) image.GridId];

            // 3. The rows.
            var codec = new DrydockCodec(
                serialization,
                entMan,
                server.ResolveDependency<IGameTiming>(),
                _ => throw new InvalidOperationException("The load allocates nothing."),
                id => deserializer.UidMap.TryGetValue((int) id, out var uid)
                    ? uid
                    : throw new FormatException($"Codec loop: a row names stable id {id}, which the image does not hold."));

            var overwrote = 0;
            var added = 0;
            var removed = 0;
            var overwroteByType = new Dictionary<string, int>(StringComparer.Ordinal);
            var removedByType = new Dictionary<string, int>(StringComparer.Ordinal);
            var heldBack = new Dictionary<EntityUid, Dictionary<string, ItemSlot>>();

            foreach (var (uid, data) in deserializer.Entities)
            {
                var entityRows = rows[data.YamlId];
                foreach (var (name, row) in entityRows)
                {
                    // The grid's own grid component came in through the skeleton with its chunks; a copy of the row,
                    // which has none, would empty it.
                    if (uid == gridUid && name == "MapGrid")
                        continue;

                    var registration = factory.GetRegistration(name);
                    var read = codec.Read(registration.Type, name == ItemSlotsName ? HoldBackSlots(entMan, codec, uid, row, heldBack) : row);

                    if (entMan.TryGetComponent(uid, registration.Type, out var existing))
                    {
                        serialization.CopyTo(read, ref existing, codec.Context, notNullableOverride: true);
                        overwrote++;
                        overwroteByType[name] = overwroteByType.GetValueOrDefault(name) + 1;
                        continue;
                    }

                    if (read is ISerializationHooks)
                    {
                        var fresh = factory.GetComponent(registration);
                        entMan.AddComponent(uid, fresh);
                        serialization.CopyTo(read, ref fresh, codec.Context, notNullableOverride: true);
                    }
                    else
                    {
                        entMan.AddComponent(uid, read);
                    }

                    added++;
                }

                if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype is not { } prototype)
                    continue;

                foreach (var name in prototype.Components.Keys)
                {
                    var registration = factory.GetRegistration(name);
                    if (entityRows.ContainsKey(name) || registration.Unsaved || !entMan.HasComponent(uid, registration.Type))
                        continue;

                    entMan.RemoveComponent(uid, registration.Type);
                    removed++;
                    removedByType[name] = removedByType.GetValueOrDefault(name) + 1;
                }
            }

            // 4. Onto the map, as the engine's merge does in the same gap.
            var xformSystem = server.System<SharedTransformSystem>();
            var gridXform = entMan.GetComponent<TransformComponent>(gridUid);
            xformSystem.SetCoordinates(
                (gridUid, gridXform, entMan.GetComponent<MetaDataComponent>(gridUid)),
                new EntityCoordinates(mapUid, gridXform.LocalPosition),
                rotation: gridXform.LocalRotation,
                newParent: entMan.GetComponent<TransformComponent>(mapUid));
            deserializer.Result.Orphans.Clear();

            // 5. The engine's startup, with the silent map-init stamp, and the seam between each entity's init and its
            // startup (EntityManager.cs:1060, before StartEntity at EntityDeserializer.cs:977-982), where the held-back
            // slots go in: into the slot an init handler re-added, or added whole where nothing re-added it.
            var itemSlots = server.System<ItemSlotsSystem>();
            var copiedAtSeam = 0;
            var addedAtSeam = 0;

            void AtSeam(Entity<MetaDataComponent> entity)
            {
                if (!heldBack.Remove(entity.Owner, out var held))
                    return;

                foreach (var (key, stored) in held)
                {
                    if (itemSlots.TryGetSlot(entity.Owner, key, out var live))
                    {
                        // Into the live instance, not in place of it: the component that re-added the slot holds a
                        // reference to that instance as its own data field. CopyFrom is ItemSlotsSystem's alone
                        // (RA0002), so the scaffolding calls it by name; a loader in the server needs a ruling on
                        // how it gets that access.
                        CopySlot.Invoke(live, new object[] { stored });
                        copiedAtSeam++;
                    }
                    else
                    {
                        itemSlots.AddItemSlot(entity.Owner, key, stored);
                        addedAtSeam++;
                    }
                }
            }

            var heldBackSlots = heldBack.Values.Sum(held => held.Count);
            entMan.EntityInitialized += AtSeam;
            try
            {
                deserializer.StartEntities();
            }
            finally
            {
                entMan.EntityInitialized -= AtSeam;
            }

            // The engine's reading of the tiles against the image's own.
            var stored = DrydockTileTable.Read(tileTable, name => tileDefs[name].TileId).ToHashSet();
            var restored = server.System<SharedMapSystem>()
                .GetAllTiles(gridUid, entMan.GetComponent<MapGridComponent>(gridUid))
                .Select(tile => (tile.GridIndices, tile.Tile))
                .ToHashSet();

            var trip = CodecNotes.Count(note => note.Contains(" entities stored (", StringComparison.Ordinal)) + 1;
            CodecNotes.Add($"[ladder] codec round trip {trip}: {image.Entities.Count} entities stored ({image.Unsaved} unsavable left out with what they held), "
                           + $"{image.Entities.Sum(e => e.Rows.Count)} rows, {image.Bytes} bytes of JSON text; "
                           + $"tiles {stored.Count} stored, {restored.Count} restored, {stored.Except(restored).Count()} missing, {restored.Except(stored).Count()} extra.");
            CodecNotes.Add($"[ladder] codec round trip {trip}: {overwrote} row(s) copied into a component the prototype had added "
                           + $"(its ComponentAdd saw prototype data), {added} added as read, {removed} prototype component(s) removed before init.");
            CodecNotes.Add($"         overwritten, top: {Top(overwroteByType)}");
            CodecNotes.Add($"         removed: {Top(removedByType)}");
            CodecNotes.Add($"[ladder] codec round trip {trip}: {heldBackSlots} item slot(s) held back from init; at the seam {copiedAtSeam} copied into the slot init re-added, "
                           + $"{addedAtSeam} added whole, {heldBack.Count} entit(y/ies) whose held-back slots the seam never reached.");

            return gridUid;
        }

        private const string ItemSlotsName = "ItemSlots";

        private static readonly System.Reflection.MethodInfo CopySlot =
            typeof(ItemSlot).GetMethod("CopyFrom", new[] { typeof(ItemSlot) })
            ?? throw new InvalidOperationException("ItemSlot.CopyFrom(ItemSlot) is gone.");

        /// <summary>
        /// The item-slot registry is readOnly on purpose: a slot a component adds at init is kept out of a save so
        /// that the add does not duplicate it (ItemSlotsComponent.cs:36-39), and the codec writes it anyway, because
        /// a slot's own state (a lock, for one) lives nowhere else. So before init only the keys the prototype's own
        /// registry holds go in, which is what the registry's init expects, and the rest wait for the seam. Ruled
        /// 2026-09-18: no list of who re-adds what, because the seam finds out.
        /// </summary>
        private static MappingDataNode HoldBackSlots(
            IEntityManager entMan,
            DrydockCodec codec,
            EntityUid uid,
            MappingDataNode row,
            Dictionary<EntityUid, Dictionary<string, ItemSlot>> heldBack)
        {
            if (!row.TryGet<MappingDataNode>("slots", out var slots))
                return row;

            var prototypeKeys = entMan.TryGetComponent<ItemSlotsComponent>(uid, out var registry)
                ? registry.Slots.Keys.ToHashSet()
                : new HashSet<string>();

            // The stored slots as objects, read once whole, so the seam hands init's slot the stored one.
            var stored = codec.Read<ItemSlotsComponent>(row).Slots;
            var filtered = row.Copy();
            var filteredSlots = filtered.Get<MappingDataNode>("slots");
            var held = new Dictionary<string, ItemSlot>();

            foreach (var (key, _) in slots)
            {
                if (prototypeKeys.Contains(key))
                    continue;

                filteredSlots.Remove(key);
                held[key] = stored[key];
            }

            if (held.Count > 0)
                heldBack[uid] = held;

            return filtered;
        }

        private static string Top(Dictionary<string, int> counts) =>
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
