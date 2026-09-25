using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Content.Server._Triad.Atmos.EntitySystems;
using Content.Server.Atmos;
using Content.Server.Atmos.Components;
using Content.Server.NodeContainer.NodeGroups;
using Content.Shared.NodeContainer;
using Content.Shared.NodeContainer.NodeGroups;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The fidelity ladder's detector: every component on every entity of a grid, rendered to keys that
/// survive a rebuild. Test-only, like <see cref="SnapshotGrid"/>, and kept apart from it because five
/// fixtures assert on the narrow snapshot's exact key set.
///
/// <para><c>save: false</c> entities are rendered like every other, with
/// <c>MetaDataComponent.savable</c> false. The serializer never writes them, so one that comes back was
/// respawned by its owner and is compared in full, and one that does not is a loss the caller must
/// classify: a transient effect, or player property the store deleted.</para>
///
/// <para>Keys share the narrow snapshot's shape, <c>path|Component.Member</c>, so
/// <see cref="DrydockStateSnapshot.Diff"/> reads both. Paths do not: every entity gets one
/// (<see cref="DeepPaths"/>), where the narrow snapshot drops entities that share a path.</para>
/// <list type="bullet">
/// <item><c>Component.&lt;present&gt;</c>: one key per component, so a component with no data
/// fields that vanishes still produces a line.</item>
/// <item><c>Component.Member</c>: a data field, rendered as the serializer writes it (<see cref="WriteField"/>).
/// A field neither its type nor its own serializer can write is named in
/// <see cref="DrydockStateSnapshot.UncapturableMembers"/> and rendered by reflection (<see cref="RenderByReflection"/>).</item>
/// <item><c>GridAtmosphereComponent.Tiles.*</c>: the deck's gas, tile by tile (<see cref="RenderTileAtmos"/>).</item>
/// <item><c>Component.~member</c>: a field that is not a data field and not engine plumbing
/// (<see cref="IsBookkeeping"/>). Scalars render in full; collections of up to
/// <see cref="CollectionCap"/> elements render element by element (sorted when unordered); other
/// objects render their scalar fields one level deep.</item>
/// <item><c>Component.Member~time</c>: every <see cref="TimeSpan"/>, rendered <c>raw|relative</c>
/// where relative is the value minus <c>CurTime</c>. A duration keeps its raw value across a round
/// trip and an absolute time keeps its relative one, so a time that keeps neither is the finding;
/// <see cref="TimeKeepsItsMeaning"/> is that comparison.</item>
/// <item><c>NodeContainerComponent.node.NAME.group</c>, <c>grid|NodeGroup.*</c> and
/// <c>grid|PipeNetAir.*</c>: the node networks' shape and pipe gas, which no component holds
/// (<see cref="RenderNetworks"/>).</item>
/// </list>
///
/// <para>The components the loader rebuilds by design (<see cref="Normalized"/>) are rendered through
/// <see cref="RenderNormalized"/>, which keeps what a player would notice (anchoring, rotation, tiles,
/// fixture layers, joints, container settings, names) and drops what a rebuild changes on purpose
/// (proxies, velocities, lifestage, the grid's own world placement).</para>
///
/// <para>Entity references render as the path of the entity they point at, <c>offgrid:proto</c>
/// for one off the ship and <c>invalid</c> for none, so a reference that comes back pointing
/// somewhere else is a difference and a correctly remapped one is not. The same holds for the
/// uid-as-text fields in <see cref="TextEntityReferences"/>.</para>
/// </summary>
public sealed partial class DrydockFidelitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private ITileDefinitionManager _tileDefs = default!;

    public const string TimeSuffix = "~time";

    /// <summary>Collections larger than this render as their count only.</summary>
    private const int CollectionCap = 64;

    /// <summary>Scalar fields rendered from one runtime object before the rest are elided.</summary>
    private const int NestedFieldCap = 16;

    /// <summary>
    /// Data fields that hold an entity reference as the uid's text, which the loader cannot remap
    /// (<c>ShuttleConsoleLockSystem</c> writes <c>EntityUid.ToString()</c> into both). Rendered as the
    /// path of the entity the text names, so a lock re-pointed at the retrieved grid compares equal.
    /// </summary>
    private static readonly HashSet<string> TextEntityReferences = new(StringComparer.Ordinal)
    {
        "ShuttleConsoleLockComponent.ShuttleId",
        "ShipGridLockComponent.ShuttleId",
    };

    /// <summary>Components rendered by <see cref="RenderNormalized"/> instead of the field walk.</summary>
    private static readonly HashSet<string> Normalized = new(StringComparer.Ordinal)
    {
        nameof(TransformComponent),
        nameof(MetaDataComponent),
        nameof(PhysicsComponent),
        nameof(FixturesComponent),
        nameof(JointComponent),
        nameof(MapGridComponent),
        nameof(ContainerManagerComponent),
    };

    private static readonly ConcurrentDictionary<Type, MemberInfo[]> AllDataFieldCache = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> RuntimeFieldCache = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> NestedFieldCache = new();

    /// <summary>
    /// Every data field of a component type, read-only and custom-serialized ones included: the
    /// narrow snapshot's <see cref="DataFields"/> leaves custom-serialized fields out, which drops
    /// every offset-serialized time from the comparison.
    /// </summary>
    private static MemberInfo[] AllDataFields(Type type) => AllDataFieldCache.GetOrAdd(type, static t =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var members = new List<MemberInfo>();

        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var m in cur.GetFields(flags).Cast<MemberInfo>().Concat(cur.GetProperties(flags)))
            {
                if (m.GetCustomAttribute<DataFieldBaseAttribute>() != null)
                    members.Add(m);
            }
        }

        return members.ToArray();
    });

    /// <summary>
    /// Every instance field of a component type that is not a data field and not the backing field of
    /// one. Fields declared on <see cref="Component"/> itself are lifecycle bookkeeping (owner,
    /// life stage, ticks) and differ across any rebuild by design, so the walk stops above it.
    /// </summary>
    private static FieldInfo[] RuntimeFields(Type type) => RuntimeFieldCache.GetOrAdd(type, static t =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var fields = new List<FieldInfo>();

        for (var cur = t; cur != null && cur != typeof(Component) && cur != typeof(object); cur = cur.BaseType)
        {
            var dataBacking = cur.GetProperties(flags)
                .Where(p => p.GetCustomAttribute<DataFieldBaseAttribute>() != null)
                .Select(p => $"<{p.Name}>k__BackingField")
                .ToHashSet();

            foreach (var f in cur.GetFields(flags))
            {
                if (f.GetCustomAttribute<DataFieldBaseAttribute>() != null
                    || dataBacking.Contains(f.Name)
                    || IsBookkeeping(f))
                {
                    continue;
                }

                fields.Add(f);
            }
        }

        return fields.ToArray();
    });

    /// <summary>
    /// Fields that are engine plumbing rather than the component's state: injected services and
    /// interface-typed handles (their render is the service's own internals), systems, and the network
    /// dirty-tracking ticks every networked component keeps.
    /// </summary>
    private static bool IsBookkeeping(FieldInfo field)
    {
        var type = field.FieldType;
        var element = type.IsArray ? type.GetElementType()! : type;

        return field.GetCustomAttribute<DependencyAttribute>() != null
               || type.IsInterface
               || typeof(IEntitySystem).IsAssignableFrom(type)
               || element == typeof(GameTick);
    }

    /// <summary>The scalar instance fields of a runtime object's type, for the one-level render.</summary>
    private static FieldInfo[] NestedFields(Type type) => NestedFieldCache.GetOrAdd(type, static t =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return t.GetFields(flags)
            .Where(f => IsScalar(f.FieldType))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();
    });

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
               || type == typeof(TimeSpan) || type == typeof(EntityUid) || type == typeof(NetEntity)
               || typeof(IPrototype).IsAssignableFrom(type);
    }

    /// <param name="tieBreak">
    /// An identity that survives the round trip, used only to order siblings that tie on everything a path is built
    /// from; null leaves them in walk order. The codec ladder passes the image's stable id, because two anchored pipes
    /// on one tile tie, and a load walks them in a different order and swaps their states.
    /// </param>
    public DrydockStateSnapshot DeepSnapshotGrid(EntityUid grid, Func<EntityUid, long?>? tieBreak = null)
    {
        var snapshot = new DrydockStateSnapshot();
        var pathOf = DeepPaths(grid, snapshot, tieBreak);
        var writer = new DrydockEntityPathWriter(_serialization, target => RefPath(target, pathOf));
        var now = _timing.CurTime;

        foreach (var (uid, path) in pathOf)
        {
            snapshot.Entities++;
            RenderDeep(uid, path, snapshot, writer, pathOf, now);
        }

        RenderNetworks(pathOf, snapshot);
        return snapshot;
    }

    /// <summary>
    /// The shape of every node network on the grid, which no component field holds: node groups live in
    /// <c>NodeGroupSystem</c> and are remade on load (<c>EntityDeserializer.SetPaused</c> notes node nets are
    /// not serialized). A group has no identity across a rebuild, so it is named by its first member in
    /// ordinal order, <c>path:node</c>.
    /// <list type="bullet">
    /// <item><c>path|NodeContainerComponent.node.NAME.group</c>: the group that node belongs to, or null.</item>
    /// <item><c>grid|NodeGroup.LABEL</c>: the group's type, its members on this grid, and its members in total
    /// (a docked pipe can join a group across grids).</item>
    /// <item><c>grid|PipeNetAir.LABEL.moles.GAS</c>: each gas a pipe net holds at 0.005 mol or more, to two decimals, so a
    /// change of composition shows where a total would hide it; and <c>.temperature</c>, only for a net that holds gas by
    /// <see cref="PipeGasCarrySystem.HoldsGas"/>, the carry's own test.</item>
    /// </list>
    /// </summary>
    private void RenderNetworks(Dictionary<EntityUid, string> pathOf, DrydockStateSnapshot snapshot)
    {
        var members = new Dictionary<INodeGroup, List<string>>();
        var nodeKeys = new List<(string Key, INodeGroup? Group)>();

        foreach (var (uid, path) in pathOf)
        {
            if (!TryComp<NodeContainerComponent>(uid, out var container))
                continue;

            foreach (var (name, node) in container.Nodes)
            {
                nodeKeys.Add(($"{path}|{nameof(NodeContainerComponent)}.node.{name}.group", node.NodeGroup));
                if (node.NodeGroup is not { } group)
                    continue;

                if (!members.TryGetValue(group, out var list))
                    members[group] = list = new List<string>();

                list.Add($"{path}:{name}");
            }
        }

        var labels = new Dictionary<INodeGroup, string>(members.Count);
        foreach (var (group, list) in members)
        {
            list.Sort(StringComparer.Ordinal);
            var label = list[0];
            labels[group] = label;

            snapshot.Values[$"grid|NodeGroup.{label}"] = $"{group.GetType().Name} here={list.Count} total={group.Nodes.Count}";

            if (group is PipeNet pipeNet)
            {
                var air = pipeNet.Air;
                foreach (var (gas, moles) in air)
                {
                    if (moles >= 0.005f)
                        snapshot.Values[$"grid|PipeNetAir.{label}.moles.{gas}"] = moles.ToString("F2", CultureInfo.InvariantCulture);
                }

                // An empty net's temperature is no state, and the carry agrees on what empty is.
                if (PipeGasCarrySystem.HoldsGas(air))
                    snapshot.Values[$"grid|PipeNetAir.{label}.temperature"] = air.Temperature.ToString("F1", CultureInfo.InvariantCulture);
            }
        }

        foreach (var (key, group) in nodeKeys)
            snapshot.Values[key] = group == null ? "null" : labels[group];
    }

    /// <summary>
    /// The deck's gas, which <c>TileAtmosCollectionSerializer</c> saves per tile and no data definition can render,
    /// since <c>TileAtmosphere</c> is a plain class. The tile follows a colon, so one facet over every tile is one
    /// report kind.
    /// <list type="bullet">
    /// <item><c>GridAtmosphereComponent.Tiles.moles:X,Y.GAS</c>: each gas the tile holds at 0.005 mol or more, to two decimals.</item>
    /// <item><c>GridAtmosphereComponent.Tiles.temperature:X,Y</c>: the tile's gas temperature.</item>
    /// <item><c>GridAtmosphereComponent.Tiles.flags:X,Y</c>: whichever of blocked (no air), immutable, space and
    /// map-atmosphere hold, present only when one does. The serializer saves the air alone; atmos rebuilds these.</item>
    /// </list>
    /// </summary>
    private static void RenderTileAtmos(string path, Dictionary<Vector2i, TileAtmosphere> tiles, DrydockStateSnapshot snapshot)
    {
        const string prefix = $"{nameof(GridAtmosphereComponent)}.{nameof(GridAtmosphereComponent.Tiles)}";
        var flags = new List<string>(4);

        foreach (var (indices, tile) in tiles)
        {
            var at = $"{indices.X},{indices.Y}";
            flags.Clear();

            if (tile.Air is { } air)
            {
                snapshot.Values[$"{path}|{prefix}.temperature:{at}"] = air.Temperature.ToString("F1", CultureInfo.InvariantCulture);
                foreach (var (gas, moles) in air)
                {
                    if (moles >= 0.005f)
                        snapshot.Values[$"{path}|{prefix}.moles:{at}.{gas}"] = moles.ToString("F2", CultureInfo.InvariantCulture);
                }

                if (air.Immutable)
                    flags.Add("immutable");
            }
            else
            {
                flags.Add("blocked");
            }

            if (tile.Space)
                flags.Add("space");
            if (tile.MapAtmosphere)
                flags.Add("map-atmosphere");

            if (flags.Count > 0)
                snapshot.Values[$"{path}|{prefix}.flags:{at}"] = string.Join(',', flags);
        }
    }

    private static readonly ConcurrentDictionary<(Type Value, Type Serializer), MethodInfo> SerializerWrites = new();

    /// <summary>
    /// A data field written by its runtime type or, when that fails, through the field's own
    /// <c>customTypeSerializer</c>: the only writer a field has when its type is not a data definition.
    /// </summary>
    private DataNode WriteField(MemberInfo member, object value, DrydockEntityPathWriter writer)
    {
        try
        {
            return _serialization.WriteValue(value.GetType(), value, alwaysWrite: true, context: writer);
        }
        catch (Exception byType) when (member.GetCustomAttribute<DataFieldBaseAttribute>()?.CustomTypeSerializer is { } serializer)
        {
            try
            {
                var write = SerializerWrites.GetOrAdd((MemberType(member), serializer), static key =>
                    typeof(ISerializationManager).GetMethods()
                        .First(m => m.Name == nameof(ISerializationManager.WriteValue) && m.GetGenericArguments().Length == 2)
                        .MakeGenericMethod(key.Value, key.Serializer));

                return (DataNode) write.Invoke(_serialization, new object?[] { value, true, writer, false })!;
            }
            catch (Exception bySerializer)
            {
                throw new InvalidOperationException(
                    $"by type: {Reason(byType)}; by {serializer.Name}: {Reason(bySerializer)}", bySerializer);
            }
        }
    }

    /// <summary>A failure's type and the first line of its message, unwrapped from reflection and cut to 200 characters.</summary>
    private static string Reason(Exception e)
    {
        if (e is TargetInvocationException { InnerException: { } inner })
            e = inner;

        var message = e.Message;
        var newline = message.IndexOf('\n');
        if (newline >= 0)
            message = message[..newline];

        var reason = $"{e.GetType().Name}: {message.TrimEnd()}";
        return reason.Length <= 200 ? reason : reason[..200];
    }

    /// <summary>
    /// A unique path for every entity on the grid. A direct child of the grid is named by its prototype
    /// and tile; a contained entity by its container and prototype; any other child by its prototype.
    /// Siblings that share a name are suffixed <c>#n</c> in container order when contained, else anchored
    /// first and then in local-position order, so removing one entity renumbers only its own prototype's
    /// siblings. Uncontained siblings whose anchoring and position both tie are paired by
    /// <paramref name="tieBreak"/> when one is given, else by walk order, and counted in
    /// <see cref="DrydockStateSnapshot.TieBroken"/> either way.
    /// </summary>
    private Dictionary<EntityUid, string> DeepPaths(EntityUid grid, DrydockStateSnapshot snapshot, Func<EntityUid, long?>? tieBreak)
    {
        var nodes = GridTreeListWithParents(grid);
        var pathOf = new Dictionary<EntityUid, string>(nodes.Count);
        if (nodes.Count == 0)
            return pathOf;

        var children = new List<int>[nodes.Count];
        for (var i = 1; i < nodes.Count; i++)
        {
            var parent = nodes[i].Parent!.Value;
            (children[parent] ??= new List<int>()).Add(i);
        }

        pathOf[grid] = "grid";
        var frontier = new Queue<int>();
        frontier.Enqueue(0);

        while (frontier.TryDequeue(out var index))
        {
            if (children[index] is not { } kids)
                continue;

            var parentUid = nodes[index].Uid;
            var parentPath = pathOf[parentUid];

            var named = new List<(int Node, string Base, int Slot, bool Anchored, System.Numerics.Vector2 Pos)>(kids.Count);
            foreach (var kid in kids)
            {
                var uid = nodes[kid].Uid;
                var proto = MetaData(uid).EntityPrototype?.ID ?? "?";
                var xform = Transform(uid);
                var local = xform.LocalPosition;

                if (_containers.TryGetContainingContainer((uid, null, null), out var container)
                    && container.Owner == parentUid)
                {
                    var slot = 0;
                    while (slot < container.ContainedEntities.Count && container.ContainedEntities[slot] != uid)
                        slot++;

                    named.Add((kid, $"{parentPath}/{container.ID}/{proto}", slot, xform.Anchored, local));
                }
                else if (index == 0)
                {
                    named.Add((kid, $"{proto}@{(int) MathF.Floor(local.X)},{(int) MathF.Floor(local.Y)}", -1, xform.Anchored, local));
                }
                else
                {
                    named.Add((kid, $"{parentPath}/{proto}", -1, xform.Anchored, local));
                }
            }

            foreach (var group in named.GroupBy(n => n.Base))
            {
                // Anchored first: two of one prototype on one tile, one anchored and one loose, share a
                // position, and pairing them by walk order swaps their states across a round trip.
                // The tie-break is last and stable, so with none the order is the walk's, as it always was.
                var ordered = group
                    .OrderBy(n => n.Slot)
                    .ThenBy(n => n.Anchored ? 0 : 1)
                    .ThenBy(n => n.Pos.X)
                    .ThenBy(n => n.Pos.Y)
                    .ThenBy(n => tieBreak?.Invoke(nodes[n.Node].Uid) ?? 0)
                    .ToList();

                for (var i = 0; i < ordered.Count; i++)
                {
                    var (node, baseName, slot, anchored, pos) = ordered[i];
                    pathOf[nodes[node].Uid] = ordered.Count == 1 ? baseName : $"{baseName}#{i}";

                    if (i > 0 && slot < 0 && ordered[i - 1].Anchored == anchored && ordered[i - 1].Pos == pos)
                        snapshot.TieBroken++;

                    frontier.Enqueue(node);
                }
            }
        }

        return pathOf;
    }

    private string RefPath(EntityUid target, Dictionary<EntityUid, string> pathOf)
    {
        if (!target.IsValid())
            return "invalid";

        if (pathOf.TryGetValue(target, out var path))
            return path;

        return TerminatingOrDeleted(target)
            ? "deleted"
            : $"offgrid:{MetaData(target).EntityPrototype?.ID ?? "?"}";
    }

    private void RenderDeep(
        EntityUid uid,
        string path,
        DrydockStateSnapshot snapshot,
        DrydockEntityPathWriter writer,
        Dictionary<EntityUid, string> pathOf,
        TimeSpan now)
    {
        foreach (var comp in AllComps(uid).ToList())
        {
            var compType = comp.GetType();

            if (Normalized.Contains(compType.Name))
            {
                RenderNormalized(uid, comp, path, snapshot, pathOf);
                continue;
            }

            if (UncomparableComponents.Contains(compType.Name))
                continue;

            snapshot.Values[$"{path}|{compType.Name}.<present>"] = "1";

            foreach (var member in AllDataFields(compType))
            {
                if (comp is GridAtmosphereComponent gridAtmos && member.Name == nameof(GridAtmosphereComponent.Tiles))
                {
                    RenderTileAtmos(path, gridAtmos.Tiles, snapshot);
                    continue;
                }

                var key = $"{path}|{compType.Name}.{member.Name}";
                object? value;
                try
                {
                    value = GetMember(comp, member);
                }
                catch (Exception e)
                {
                    snapshot.NoteUncapturable($"{compType.Name}.{member.Name}", $"read: {Reason(e)}");
                    continue;
                }

                if (value is TimeSpan span)
                {
                    snapshot.Values[key + TimeSuffix] = RenderTime(span, now);
                    continue;
                }

                if (value == null)
                {
                    snapshot.Values[IsTime(MemberType(member)) ? key + TimeSuffix : key] = "null";
                    continue;
                }

                if (value is EntityUid ent)
                {
                    snapshot.Values[key] = RefPath(ent, pathOf);
                    continue;
                }

                if (value is string text
                    && TextEntityReferences.Contains($"{compType.Name}.{member.Name}")
                    && EntityUid.TryParse(text, out var textRef))
                {
                    snapshot.Values[key] = RefPath(textRef, pathOf);
                    continue;
                }

                try
                {
                    snapshot.Values[key] = WriteField(member, value, writer).ToString();
                }
                catch (Exception e)
                {
                    snapshot.NoteUncapturable($"{compType.Name}.{member.Name}", Reason(e));
                    snapshot.Values[key] = RenderByReflection(value, pathOf);
                }
            }

            foreach (var field in RuntimeFields(compType))
            {
                var key = $"{path}|{compType.Name}.~{CleanFieldName(field.Name)}";
                var value = field.GetValue(comp);

                if (value is TimeSpan span)
                {
                    snapshot.Values[key + TimeSuffix] = RenderTime(span, now);
                    continue;
                }

                if (value == null && IsTime(field.FieldType))
                {
                    snapshot.Values[key + TimeSuffix] = "null";
                    continue;
                }

                snapshot.Values[key] = RenderRuntime(value, pathOf, nested: true);
            }
        }

        if (TryComp<AppearanceComponent>(uid, out var appearance))
        {
            foreach (var (dataKey, value) in LiveAppearance(appearance))
            {
                var rendered = RenderValue(value);
                if (rendered == null)
                {
                    snapshot.NoteUncapturable($"Appearance.{dataKey.GetType().Name}.{dataKey}", $"no render for {value.GetType().Name}");
                    rendered = RenderByReflection(value, pathOf);
                }

                snapshot.Values[$"{path}|Appearance.{dataKey.GetType().Name}.{dataKey}"] = rendered;
            }
        }
    }

    /// <summary>
    /// The player-visible state of a component the loader rebuilds by design. The grid's own transform
    /// is its placement in the world, which the retrieve chooses, so only its anchoring is kept.
    /// </summary>
    private void RenderNormalized(
        EntityUid uid,
        IComponent comp,
        string path,
        DrydockStateSnapshot snapshot,
        Dictionary<EntityUid, string> pathOf)
    {
        var prefix = $"{path}|{comp.GetType().Name}.";
        snapshot.Values[prefix + "<present>"] = "1";

        switch (comp)
        {
            case TransformComponent xform:
                snapshot.Values[prefix + "anchored"] = Bool(xform.Anchored);
                if (path != "grid")
                {
                    snapshot.Values[prefix + "rotation"] = xform.LocalRotation.Degrees.ToString("F1", CultureInfo.InvariantCulture);
                    snapshot.Values[prefix + "parent"] = RefPath(xform.ParentUid, pathOf);
                }
                break;

            case MetaDataComponent meta:
                snapshot.Values[prefix + "name"] = meta.EntityName;
                snapshot.Values[prefix + "description"] = meta.EntityDescription;
                snapshot.Values[prefix + "prototype"] = meta.EntityPrototype?.ID ?? "null";
                snapshot.Values[prefix + "paused"] = Bool(meta.EntityPaused);
                snapshot.Values[prefix + "savable"] = Bool(meta.EntityPrototype?.MapSavable != false);
                break;

            case PhysicsComponent body:
                // Copied out first: the physics members are read-only to content, and a method call on
                // the member access counts as an execute (RA0002).
                var bodyType = body.BodyType;
                var bodyStatus = body.BodyStatus;
                snapshot.Values[prefix + "bodyType"] = bodyType.ToString();
                snapshot.Values[prefix + "canCollide"] = Bool(body.CanCollide);
                snapshot.Values[prefix + "fixedRotation"] = Bool(body.FixedRotation);
                snapshot.Values[prefix + "bodyStatus"] = bodyStatus.ToString();
                break;

            case FixturesComponent fixtures:
                foreach (var (id, fixture) in fixtures.Fixtures.OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    var density = fixture.Density;
                    snapshot.Values[prefix + "fixture." + id] =
                        $"hard={Bool(fixture.Hard)} layer={fixture.CollisionLayer} mask={fixture.CollisionMask} "
                        + $"density={density.ToString("F2", CultureInfo.InvariantCulture)}";
                }
                break;

            case JointComponent joints:
                foreach (var (id, joint) in joints.GetJoints.OrderBy(j => j.Key, StringComparer.Ordinal))
                {
                    snapshot.Values[prefix + "joint." + id] =
                        $"{joint.JointType} {RefPath(joint.BodyAUid, pathOf)} <-> {RefPath(joint.BodyBUid, pathOf)}";
                }
                break;

            case MapGridComponent grid:
                var tiles = _mapSystem.GetAllTiles(uid, grid);
                while (tiles.MoveNext(out var tileRef))
                {
                    if (tileRef is not { } tile)
                        continue;

                    var t = tile.Tile;
                    snapshot.Values[$"{prefix}tile@{tile.GridIndices.X},{tile.GridIndices.Y}"] =
                        $"{_tileDefs[t.TypeId].ID} variant={t.Variant} rot={t.RotationMirroring} flags={t.Flags}";
                }
                break;

            case ContainerManagerComponent containers:
                foreach (var (id, container) in containers.Containers.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    snapshot.Values[prefix + "container." + id] =
                        $"{container.GetType().Name} occludes={Bool(container.OccludesLight)} "
                        + $"show={Bool(container.ShowContents)} count={container.Count}";
                }
                break;
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// A value no writer accepts, rendered by reflection so its differences still surface: a list or set element by
    /// element with each element's scalar fields, anything else with its own, one level deep. State below that level
    /// is not compared, which is why the member stays in <see cref="DrydockStateSnapshot.UncapturableMembers"/>.
    /// </summary>
    private string RenderByReflection(object value, Dictionary<EntityUid, string> pathOf)
    {
        if (value is not ICollection collection || value is IDictionary)
            return RenderRuntime(value, pathOf, nested: true);

        if (collection.Count > CollectionCap)
            return $"count={collection.Count}";

        var items = new List<string>(collection.Count);
        foreach (var item in collection)
            items.Add(RenderRuntime(item, pathOf, nested: true));

        var type = collection.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
            items.Sort(StringComparer.Ordinal);

        return $"count={collection.Count} [{string.Join(", ", items)}]";
    }

    private string RenderRuntime(object? value, Dictionary<EntityUid, string> pathOf, bool nested)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return s;
            case IPrototype prototype:
                return prototype.ID;
            case EntityUid ent:
                return RefPath(ent, pathOf);
            case NetEntity net:
                return RefPath(GetEntity(net), pathOf);
            case TimeSpan span:
                return span.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
            case Delegate:
                return "<delegate>";
            case IComponent:
                return $"<{value.GetType().Name}>";
            case ICollection collection:
                return RenderCollection(collection, pathOf);
            case IFormattable formattable when value.GetType().IsValueType:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";

        if (type.IsGenericType && type.Namespace == "Robust.Shared.GameObjects" && type.Name.StartsWith("Entity`", StringComparison.Ordinal)
            && type.GetField("Owner") is { } owner)
        {
            return RefPath((EntityUid) owner.GetValue(value)!, pathOf);
        }

        if (!nested || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            return $"<{type.Name}>";

        var fields = NestedFields(type);
        if (fields.Length == 0)
            return $"<{type.Name}>";

        var parts = fields
            .Take(NestedFieldCap)
            .Select(f => $"{CleanFieldName(f.Name)}={RenderRuntime(f.GetValue(value), pathOf, nested: false)}");

        var elided = fields.Length > NestedFieldCap ? $", +{fields.Length - NestedFieldCap}" : "";
        return $"<{type.Name}>{{{string.Join(", ", parts)}{elided}}}";
    }

    /// <summary>
    /// A collection element by element when it holds at most <see cref="CollectionCap"/> of them:
    /// dictionaries and sets sorted, since their enumeration order is not state, lists and arrays in
    /// order. Always prefixed <c>count=N</c> so a count comparison still reads the value.
    /// </summary>
    private string RenderCollection(ICollection collection, Dictionary<EntityUid, string> pathOf)
    {
        if (collection.Count > CollectionCap)
            return $"count={collection.Count}";

        List<string> items;
        var unordered = false;

        if (collection is IDictionary dictionary)
        {
            items = new List<string>(dictionary.Count);
            foreach (DictionaryEntry entry in dictionary)
                items.Add($"{RenderRuntime(entry.Key, pathOf, nested: false)}={RenderRuntime(entry.Value, pathOf, nested: false)}");
            unordered = true;
        }
        else
        {
            items = new List<string>(collection.Count);
            foreach (var item in collection)
                items.Add(RenderRuntime(item, pathOf, nested: false));

            var type = collection.GetType();
            unordered = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>);
        }

        if (unordered)
            items.Sort(StringComparer.Ordinal);

        return $"count={collection.Count} [{string.Join(", ", items)}]";
    }

    /// <summary>A <see cref="TimeSpan"/> or nullable one, whose null keeps the <see cref="TimeSuffix"/> key so it pairs with a set value.</summary>
    private static bool IsTime(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(TimeSpan);

    private static string RenderTime(TimeSpan value, TimeSpan now)
    {
        var raw = value.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        var relative = (value - now).TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        return $"{raw}|{relative}";
    }

    private static string CleanFieldName(string name)
    {
        return name.StartsWith('<') && name.IndexOf('>') is > 1 and var close
            ? name[1..close]
            : name;
    }

    /// <summary>
    /// Whether a <c>~time</c> value kept its meaning: its raw value or its value relative to the
    /// clock is within <paramref name="toleranceSeconds"/> of what it was. A MaxValue or MinValue
    /// sentinel keeps its raw value, so it passes as long as it stays a sentinel.
    /// </summary>
    public static bool TimeKeepsItsMeaning(string before, string after, double toleranceSeconds)
    {
        if (!TryParseTime(before, out var rawBefore, out var relBefore)
            || !TryParseTime(after, out var rawAfter, out var relAfter))
        {
            return before == after;
        }

        return Math.Abs(rawBefore - rawAfter) <= toleranceSeconds
               || Math.Abs(relBefore - relAfter) <= toleranceSeconds;
    }

    private static bool TryParseTime(string value, out double raw, out double relative)
    {
        raw = relative = 0;
        var bar = value.IndexOf('|');
        return bar > 0
               && double.TryParse(value[..bar], NumberStyles.Float, CultureInfo.InvariantCulture, out raw)
               && double.TryParse(value[(bar + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out relative);
    }
}

/// <summary>
/// A serialization context that writes every entity reference as the path of the entity it names,
/// so two renders of the same state on two different sets of uids compare equal.
/// </summary>
internal sealed class DrydockEntityPathWriter : ISerializationContext, ITypeWriter<EntityUid>, ITypeWriter<NetEntity>
{
    private readonly Func<EntityUid, string> _pathOf;

    public SerializationManager.SerializerProvider SerializerProvider { get; }

    public bool WritingReadingPrototypes => false;

    public DrydockEntityPathWriter(ISerializationManager serialization, Func<EntityUid, string> pathOf)
    {
        _pathOf = pathOf;
        SerializerProvider = new SerializationManager.SerializerProvider(serialization);
        SerializerProvider.RegisterSerializer(this);
    }

    public DataNode Write(ISerializationManager serializationManager, EntityUid value, IDependencyCollection dependencies,
        bool alwaysWrite = false, ISerializationContext? context = null)
        => new ValueDataNode(_pathOf(value));

    public DataNode Write(ISerializationManager serializationManager, NetEntity value, IDependencyCollection dependencies,
        bool alwaysWrite = false, ISerializationContext? context = null)
        => new ValueDataNode(_pathOf(dependencies.Resolve<IEntityManager>().GetEntity(value)));
}
