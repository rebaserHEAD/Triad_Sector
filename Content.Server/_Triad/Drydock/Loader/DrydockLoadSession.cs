using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Content.Server._Mono.ScuttleDevice;
using Content.Server._Triad.Drydock.Codec;
using Content.Server.Power.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Pinpointer;
using Robust.Shared.EntitySerialization;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// One load, in phases, in order: <see cref="CreateEntities"/>, <see cref="ApplyRows"/>, <see cref="Start"/>,
/// <see cref="Complete"/>. The engine's own deserializer allocates, adds each prototype's components and starts; the
/// image's rows are applied between its component pass and its startup.
/// <list type="number">
/// <item>A skeleton document: every stored entity under its prototype, with its stable id as the yaml uid and its
/// recorded <c>mapInit</c> and <c>paused</c>, and the grid's own grid component carrying the tile table's chunks.
/// Nothing else, because every other component comes from the image's rows.</item>
/// <item><c>TryProcessData</c> and <c>CreateEntities</c> (<c>EntityDeserializer.cs:153</c>, <c>:183</c>): the
/// engine allocates everyone and adds each prototype's components, and reads the tiles with its
/// own chunk reader, tile-change and collision work suppressed. (<see cref="CreateEntities"/>.)</item>
/// <item>The rows, through <see cref="DrydockCodec"/> under its own context, whose references resolve through the
/// engine's <c>UidMap</c> (<c>:93</c>). A component the entity already has, which the prototype put there, is
/// read into a temporary and copied in, which is the engine's own path for one (<c>:693-694</c>); one it lacks is
/// added as read, or added fresh and copied into when it has serialization hooks, as the engine does
/// (<c>:670-685</c>); and a prototype component with no row is removed. (<see cref="ApplyRows"/>.)</item>
/// <item>The grid re-parented onto the map with <c>SetCoordinates</c>, in the same gap the engine's own merge
/// uses (<c>MapLoaderSystem.Load.cs:192</c>, <c>MapLoaderSystem.LoadMap.cs:254-274</c>). (<see cref="ApplyRows"/>.)</item>
/// <item><c>StartEntities</c> (<c>:213</c>): parents first, init then startup per entity, then the map-init
/// stamp with no event (<c>:1019-1036</c>) and the pause stamp. (<see cref="Start"/>.)</item>
/// </list>
///
/// <para>The seam between each entity's init and its startup (<c>EntityInitialized</c>) carries the item slots held
/// back from init (<see cref="HoldBackSlots"/>) and the manifest's seam members. The manifest
/// (<see cref="DrydockCodecManifestMembers"/>) carries what a copy of data fields cannot, in a row of its own, and
/// sets each member at its moment: before init with the rows, at the seam for one an init handler resets, and after
/// every entity has started (a cable receiver's provider, through the cable system). A data field listed as
/// carried-and-reapplied is taken off its component after the rows and set back at its moment. What the first power
/// solve re-arms has no moment.</para>
/// </summary>
public sealed class DrydockLoadSession
{
    private const string ItemSlotsName = "ItemSlots";

    private static readonly DrydockManifestMember[] ReapplyCarried =
        DrydockCodecManifestMembers.Members.Where(m => m.Kind == DrydockMemberKind.ReapplyCarried).ToArray();

    private readonly DrydockImageSystem _system;
    private readonly DrydockImage _image;
    private readonly EntityUid _mapUid;
    private readonly DrydockLoadOptions _options;
    private readonly MappingDataNode _tileTable;
    private readonly Dictionary<long, Dictionary<string, MappingDataNode>> _rows;
    private readonly DrydockManifestApply _manifest = new();
    private readonly Dictionary<EntityUid, Dictionary<string, ItemSlot>> _heldBack = new();
    private readonly Dictionary<string, int> _overwroteByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _removedByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _appearanceRefused = new(StringComparer.Ordinal);

    private EntityDeserializer? _deserializer;
    private DrydockCodec? _codec;
    private EntityUid _gridUid;
    private Dictionary<EntityUid, long>? _ids;
    private int _phase;
    private int _overwrote;
    private int _added;
    private int _removed;
    private int _appearanceApplied;
    private int _heldBackSlots;
    private int _copiedAtSeam;
    private int _copiedAfterStartup;
    private int _addedWhole;

    internal DrydockLoadSession(DrydockImageSystem system, DrydockImage image, EntityUid mapUid, DrydockLoadOptions options)
    {
        _system = system;
        _image = image;
        _mapUid = mapUid;
        _options = options;

        _tileTable = (MappingDataNode) DrydockNodeJson.Decode(JsonNode.Parse(image.Tiles)!);
        _rows = image.Entities.ToDictionary(
            entity => entity.Id,
            entity => entity.Rows.ToDictionary(
                row => row.Key,
                row => (MappingDataNode) DrydockNodeJson.Decode(JsonNode.Parse(row.Value)!)));
    }

    /// <summary>The grid, once <see cref="CreateEntities"/> has allocated it.</summary>
    public EntityUid Grid => _gridUid;

    /// <summary>Phases 1 and 2: the skeleton document, then the engine's allocation and component pass.</summary>
    public void CreateEntities()
    {
        Expect(0);

        var groups = new SortedDictionary<string, SequenceDataNode>(StringComparer.Ordinal);
        foreach (var entity in _image.Entities)
        {
            var node = new MappingDataNode
            {
                ["uid"] = new ValueDataNode(entity.Id.ToString()),
                ["mapInit"] = new ValueDataNode(entity.MapInitialized ? "true" : "false"),
                ["paused"] = new ValueDataNode(entity.Paused ? "true" : "false"),
            };

            if (entity.Id == _image.GridId)
            {
                var gridRow = _rows[entity.Id]["MapGrid"].Copy();
                gridRow["type"] = new ValueDataNode("MapGrid");
                gridRow["chunks"] = _tileTable.Get<MappingDataNode>(DrydockTileTable.ChunksKey).Copy();
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

        var gridYamlId = new ValueDataNode(_image.GridId.ToString());
        var document = new MappingDataNode
        {
            ["meta"] = new MappingDataNode { ["format"] = new ValueDataNode("7"), ["category"] = new ValueDataNode("Grid") },
            ["maps"] = new SequenceDataNode(),
            ["grids"] = new SequenceDataNode { gridYamlId },
            ["orphans"] = new SequenceDataNode { gridYamlId.Copy() },
            ["nullspace"] = new SequenceDataNode(),
            ["tilemap"] = _tileTable.Get<MappingDataNode>(DrydockTileTable.TileMapKey).Copy(),
            ["entities"] = entityGroups,
        };

        // The entity-system collection, not the root one: the deserializer injects systems (SharedMapSystem among
        // them), and MapLoaderSystem hands it its own injected collection, which is this one.
        var deserializer = new EntityDeserializer(
            _system.Entities.EntitySysManager.DependencyCollection,
            document,
            new DeserializationOptions());
        if (!deserializer.TryProcessData())
            throw new InvalidOperationException("Drydock load: the engine refused the skeleton document.");

        deserializer.CreateEntities();
        _deserializer = deserializer;
        _gridUid = deserializer.UidMap[(int) _image.GridId];
        _ids = deserializer.UidMap.ToDictionary(entry => entry.Value, entry => (long) entry.Key);

        _codec = new DrydockCodec(
            _system.Serialization,
            _system.Entities,
            _system.Prototypes,
            _system.Timing,
            _ => throw new InvalidOperationException("The load allocates nothing."),
            id => deserializer.UidMap.TryGetValue((int) id, out var uid)
                ? uid
                : throw new FormatException($"Drydock load: a row names stable id {id}, which the image does not hold."));

        _phase = 1;
    }

    /// <summary>Phases 3 and 4: the rows, then the grid onto the map.</summary>
    public void ApplyRows()
    {
        Expect(1);
        var entMan = _system.Entities;
        var factory = _system.ComponentFactory;
        var serialization = _system.Serialization;
        var codec = _codec!;
        var deserializer = _deserializer!;

        foreach (var (uid, data) in deserializer.Entities)
        {
            var entityRows = _rows[data.YamlId];
            foreach (var (name, row) in entityRows)
            {
                // The grid's own grid component came in through the skeleton with its chunks; a copy of the row,
                // which has none, would empty it. A row named with a leading ~ is not a component: the appearance and
                // manifest rows go in after the components, and the carried row is read at Complete.
                if ((uid == _gridUid && name == "MapGrid") || name.StartsWith('~'))
                    continue;

                var registration = factory.GetRegistration(name);
                var read = codec.Read(registration.Type, name == ItemSlotsName ? HoldBackSlots(uid, row) : row);

                if (entMan.TryGetComponent(uid, registration.Type, out var existing))
                {
                    serialization.CopyTo(read, ref existing, codec.Context, notNullableOverride: true);
                    _overwrote++;
                    _overwroteByType[name] = _overwroteByType.GetValueOrDefault(name) + 1;
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

                _added++;
            }

            // Before init, after the components: an init or startup handler that recomputes a key then overwrites the
            // stored value with the truth, and a key nothing recomputes keeps it.
            if (entityRows.TryGetValue(DrydockImageSystem.AppearanceRow, out var appearanceRow))
                _appearanceApplied += _system.ReadAppearance(codec, uid, appearanceRow, _appearanceRefused);

            // The manifest: the members before init set now, the rest decoded now and held for their moment.
            if (entityRows.TryGetValue(DrydockCodec.ManifestRow, out var manifestRow))
            {
                foreach (var (member, value) in codec.ReadManifest(manifestRow, DrydockApplyMoment.BeforeInit, factory))
                {
                    if (!HeldOff(member))
                        SetManifestMember(uid, member, value);
                }

                foreach (var moment in new[] { DrydockApplyMoment.Seam, DrydockApplyMoment.AfterStart })
                {
                    foreach (var (member, value) in codec.ReadManifest(manifestRow, moment, factory))
                    {
                        if (!HeldOff(member))
                            _manifest.Held.Add(new DrydockHeldMember(uid, member, value));
                    }
                }
            }

            // A member the rows carry but an init, startup or power handler resets: taken off the component as the row
            // left it, to be set back at its moment.
            foreach (var member in ReapplyCarried)
            {
                if (!entityRows.ContainsKey(member.Component)
                    || !entMan.TryGetComponent(uid, factory.GetRegistration(member.Component).Type, out var carrier)
                    || HeldOff(member))
                    continue;

                _manifest.Held.Add(new DrydockHeldMember(uid, member, DrydockCodec.GetMember(carrier, member)));
            }

            if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype is not { } prototype)
                continue;

            foreach (var name in prototype.Components.Keys)
            {
                var registration = factory.GetRegistration(name);
                if (entityRows.ContainsKey(name) || registration.Unsaved || !entMan.HasComponent(uid, registration.Type))
                    continue;

                entMan.RemoveComponent(uid, registration.Type);
                _removed++;
                _removedByType[name] = _removedByType.GetValueOrDefault(name) + 1;
            }
        }

        // Onto the map, as the engine's merge does in the same gap.
        var gridXform = entMan.GetComponent<TransformComponent>(_gridUid);
        _system.Xforms.SetCoordinates(
            (_gridUid, gridXform, entMan.GetComponent<MetaDataComponent>(_gridUid)),
            new EntityCoordinates(_mapUid, gridXform.LocalPosition),
            rotation: gridXform.LocalRotation,
            newParent: entMan.GetComponent<TransformComponent>(_mapUid));
        deserializer.Result.Orphans.Clear();

        _phase = 2;
    }

    /// <summary>
    /// Phase 5: the engine's startup, with the silent map-init stamp, and the seam between each entity's init and its
    /// startup (EntityManager.cs:1060, before StartEntity at EntityDeserializer.cs:977-982), where the held-back slots
    /// go in: into the slot an init handler re-added, or added whole where nothing re-added it. Then the manifest's
    /// after-start members, once every entity has started.
    /// </summary>
    public void Start()
    {
        Expect(2);
        var entMan = _system.Entities;
        var factory = _system.ComponentFactory;
        var itemSlots = _system.ItemSlots;
        var deserializer = _deserializer!;

        // Pass (i), at the seam: into the slot init re-added, so a startup handler already sees the restored state.
        // A key nothing re-added yet waits, because a startup handler may still add it (a gas canister does,
        // SharedGasCanisterSystem.cs:33-37), and adding the stored one now would make that a duplicate.
        var seamMembers = _manifest.Held.Where(h => h.Member.Moment == DrydockApplyMoment.Seam).ToLookup(h => h.Uid);

        void AtSeam(Entity<MetaDataComponent> entity)
        {
            // The manifest's seam members: after the init handler that resets them, before any startup handler reads them.
            foreach (var member in seamMembers[entity.Owner])
            {
                _manifest.SeamOn($"{member.Member.Key} on {entity.Comp.EntityPrototype?.ID ?? "(no prototype)"}");

                if (member.Member is { Component: "MetaData", Member: nameof(MetaDataComponent.EntityName) })
                {
                    // Through the system, which raises the rename the name's other readers follow.
                    if (member.Value is string name)
                    {
                        _system.Meta.SetEntityName(entity.Owner, name, entity.Comp);
                        _manifest.Count(member.Member);
                    }

                    continue;
                }

                SetManifestMember(member.Uid, member.Member, member.Value);
            }

            if (!_heldBack.TryGetValue(entity.Owner, out var held))
                return;

            foreach (var key in held.Keys.ToList())
            {
                if (!itemSlots.TryGetSlot(entity.Owner, key, out var live))
                    continue;

                // Into the live instance, not in place of it: the component that re-added the slot holds a
                // reference to that instance as its own data field.
                itemSlots.RestoreSlot(live, held[key]);
                held.Remove(key);
                _copiedAtSeam++;
            }

            if (held.Count == 0)
                _heldBack.Remove(entity.Owner);
        }

        _heldBackSlots = _heldBack.Values.Sum(held => held.Count);
        entMan.EntityInitialized += AtSeam;
        try
        {
            deserializer.StartEntities();
        }
        finally
        {
            entMan.EntityInitialized -= AtSeam;
        }

        // Pass (ii), once after startup, since the engine raises nothing per entity after StartEntity
        // (IEntityManager.cs:55-56): into the slot startup re-added, or the stored slot added whole where nothing
        // re-added it (a dispenser's, whose adders run at map init and from its parts, neither of which runs).
        foreach (var (uid, held) in _heldBack)
        {
            foreach (var (key, storedSlot) in held)
            {
                if (itemSlots.TryGetSlot(uid, key, out var live))
                {
                    itemSlots.RestoreSlot(live, storedSlot);
                    _copiedAfterStartup++;
                }
                else
                {
                    itemSlots.AddItemSlot(uid, key, storedSlot);
                    _addedWhole++;
                }
            }
        }

        _heldBack.Clear();

        // The manifest's after-start members, once every entity has started: a receiver's provider, set back through
        // the cable system, because startup paired it with whichever provider was nearest and connectable then; and a
        // pinpointer's target, through the pinpointer system.
        var cables = _system.Cables;
        var pinpointers = _system.Pinpointers;
        foreach (var held in _manifest.Held.Where(h => h.Member.Moment == DrydockApplyMoment.AfterStart))
        {
            // A pinpointer's target through SetTarget, which sets the target's name with it and, when active, the direction.
            if (held.Member is { Component: "Pinpointer", Member: nameof(PinpointerComponent.Target) })
            {
                if (!entMan.TryGetComponent<PinpointerComponent>(held.Uid, out var pinpointer))
                {
                    _manifest.Miss(held.Member, PrototypeOf(held.Uid));
                    continue;
                }

                pinpointers.SetTarget(held.Uid, held.Value as EntityUid?, pinpointer);
                _manifest.Count(held.Member);
                continue;
            }

            // A scuttle device's armed map, worked out again: the map it has loaded onto.
            if (held.Member is { Component: "ScuttleDevice", Member: nameof(ScuttleDeviceComponent.ArmedMap) })
            {
                if (!entMan.TryGetComponent<ScuttleDeviceComponent>(held.Uid, out var scuttle))
                {
                    _manifest.Miss(held.Member, PrototypeOf(held.Uid));
                    continue;
                }

                scuttle.ArmedMap = entMan.GetComponent<TransformComponent>(held.Uid).MapID;
                _manifest.Count(held.Member);
                continue;
            }

            if (held.Member is not { Component: "ExtensionCableReceiver", Member: nameof(ExtensionCableReceiverComponent.Provider) })
            {
                SetManifestMember(held.Uid, held.Member, held.Value);
                continue;
            }

            var receiverProto = PrototypeOf(held.Uid);
            if (!entMan.TryGetComponent<ExtensionCableReceiverComponent>(held.Uid, out var receiver))
            {
                _manifest.Miss(held.Member, receiverProto);
                continue;
            }

            if (held.Value is not EntityUid providerUid)
            {
                if (receiver.Provider != null)
                    _manifest.Refuse(held.Member, receiverProto, "stored unpaired, paired at startup");
                else
                    _manifest.StoredUnpaired++;

                continue;
            }

            if (!entMan.TryGetComponent<ExtensionCableProviderComponent>(providerUid, out var provider))
            {
                _manifest.Refuse(held.Member, receiverProto, "its stored provider is not on the image");
                continue;
            }

            if (receiver.Provider?.Owner == providerUid)
            {
                _manifest.AlreadyPaired++;
                continue;
            }

            if (cables.TryPairReceiver((held.Uid, receiver), (providerUid, provider)))
            {
                _manifest.Repaired++;
                _manifest.Count(held.Member);
            }
            else
            {
                _manifest.Refuse(held.Member, receiverProto, $"the cable system would not pair it with {PrototypeOf(providerUid)}");
            }
        }

        _phase = 3;
    }

    /// <summary>
    /// The tiles compared against the image's own, the appearance components counted, then <see cref="GridRestoringEvent"/>
    /// and <see cref="GridRestoredEvent"/> raised with the image's carried values (<see cref="DrydockCarried"/>, closed
    /// when they return) and the power seam armed. The result names everything the load changed, missed or refused.
    /// </summary>
    public DrydockLoadResult Complete()
    {
        Expect(3);
        var entMan = _system.Entities;
        var deserializer = _deserializer!;
        var ids = _ids!;

        // The engine's reading of the tiles against the image's own.
        var stored = DrydockTileTable.Read(_tileTable, name => _system.TileDefinitions[name].TileId).ToHashSet();
        var restored = _system.Maps
            .GetAllTiles(_gridUid, entMan.GetComponent<MapGridComponent>(_gridUid))
            .Select(tile => (tile.GridIndices, tile.Tile))
            .ToHashSet();

        // SetData dirties; the engine's ResetNetTicks runs after it inside startup. What is still marked modified in
        // this tick after the load is what PVS sends again, a network cost rather than a correctness one.
        var now = _system.Timing.CurTick;
        var appearances = 0;
        var stillDirty = 0;
        foreach (var uid in deserializer.Entities.Keys)
        {
            if (!entMan.TryGetComponent<AppearanceComponent>(uid, out var appearance))
                continue;

            appearances++;
            if (appearance.LastModifiedTick >= now)
                stillDirty++;
        }

        var result = new DrydockLoadResult
        {
            Grid = _gridUid,
            Ids = ids,
            Manifest = _manifest,
            Overwrote = _overwrote,
            OverwroteByType = _overwroteByType,
            Added = _added,
            Removed = _removed,
            RemovedByType = _removedByType,
            HeldBackSlots = _heldBackSlots,
            CopiedAtSeam = _copiedAtSeam,
            CopiedAfterStartup = _copiedAfterStartup,
            AddedWhole = _addedWhole,
            AppearanceApplied = _appearanceApplied,
            AppearanceRefused = _appearanceRefused,
            AppearanceComponents = appearances,
            AppearanceStillDirty = stillDirty,
            UnresolvedPrototypes = _codec!.Unresolved.ToList(),
            Severed = _codec.Severed.ToList(),
            DroppedBatches = _codec.Context.DroppedBatches.ToList(),
            TilesStored = stored.Count,
            TilesRestored = restored.Count,
            TilesMissing = stored.Except(restored).Count(),
            TilesExtra = restored.Except(stored).Count(),
        };

        // After the last entity has started and the after-start members are set, in ascending stable id. Two distinct steps,
        // so that when this phase is sliced the head event completes whole before the first directed raise starts: a slice
        // boundary goes between them and between entities inside the second, never inside the first.
        var inOrder = ids.OrderBy(entry => entry.Value).Select(entry => entry.Key).Where(entMan.EntityExists).ToList();

        // The carried values, for these raises only: closed after them, so a handler that keeps the lookup fails loudly.
        var carriedRows = new Dictionary<EntityUid, (string?, MappingDataNode)>();
        foreach (var entity in _image.Entities)
        {
            if (_rows[entity.Id].TryGetValue(DrydockImageSystem.CarriedRow, out var carriedRow))
                carriedRows[deserializer.UidMap[(int) entity.Id]] = (entity.Prototype, carriedRow);
        }

        var carried = new DrydockCarried(_codec!, carriedRows);
        try
        {
            // Step 1: the head, once, for a rebuild that has to finish for the whole grid before any entity's own runs.
            var restoringEvent = new GridRestoringEvent(_gridUid, inOrder, carried);
            _system.RaiseRestoring(ref restoringEvent);

            // Step 2: the directed raise at each entity, then once for the grid. An entity a handler deleted earlier is skipped.
            foreach (var uid in inOrder)
            {
                if (!entMan.EntityExists(uid))
                    continue;

                var directed = new GridRestoredEvent(_gridUid, uid, carried);
                _system.RaiseRestored(uid, ref directed);
            }

            var restoredEvent = new GridRestoredEvent(_gridUid);
            _system.RaiseRestored(ref restoredEvent);
        }
        finally
        {
            carried.Close();
        }

        _system.ArmPowerEdge(result);

        _phase = 4;
        return result;
    }

    private void Expect(int phase)
    {
        if (_phase != phase)
            throw new InvalidOperationException($"Drydock load: phase called out of order (at {_phase}, expected {phase}).");
    }

    private string PrototypeOf(EntityUid uid) =>
        _system.Entities.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID ?? "(no prototype)";

    /// <summary>Whether the caller's options hold this member off; counted when they do.</summary>
    private bool HeldOff(DrydockManifestMember member)
    {
        if (_options.HoldOff?.Invoke(member) != true)
            return false;

        _manifest.OffByKey[member.Key] = _manifest.OffByKey.GetValueOrDefault(member.Key) + 1;
        return true;
    }

    /// <summary>
    /// A manifest member set straight onto its component. A member that goes through a system is the caller's; one
    /// whose component the entity no longer has at its moment is counted, not set.
    /// </summary>
    private void SetManifestMember(EntityUid uid, DrydockManifestMember member, object? value)
    {
        if (member.Kind == DrydockMemberKind.ViaSystem)
            throw new InvalidOperationException($"Drydock load: {member.Component}.{member.Member} goes through its system, and the load has no path for it.");

        var registration = _system.ComponentFactory.GetRegistration(member.Component);
        if (!_system.Entities.TryGetComponent(uid, registration.Type, out var component))
        {
            _manifest.Miss(member, PrototypeOf(uid));
            return;
        }

        DrydockCodec.SetMember(component, member, value);
        _manifest.Count(member);
    }

    /// <summary>
    /// The item-slot registry is readOnly on purpose: a slot a component adds at init is kept out of a save so
    /// that the add does not duplicate it (ItemSlotsComponent.cs:36-39), and the codec writes it anyway, because
    /// a slot's own state (a lock, for one) lives nowhere else. So before init only the keys the prototype's own
    /// registry holds go in, which is what the registry's init expects, and the rest wait for the seam. Ruled
    /// 2026-09-18: no list of who re-adds what, because the seam finds out.
    /// </summary>
    private MappingDataNode HoldBackSlots(EntityUid uid, MappingDataNode row)
    {
        if (!row.TryGet<MappingDataNode>("slots", out var slots))
            return row;

        var prototypeKeys = _system.Entities.TryGetComponent<ItemSlotsComponent>(uid, out var registry)
            ? registry.Slots.Keys.ToHashSet()
            : new HashSet<string>();

        // The stored slots as objects, read once whole, so the seam hands init's slot the stored one.
        var stored = _codec!.Read<ItemSlotsComponent>(row).Slots;
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
            _heldBack[uid] = held;

        return filtered;
    }
}
