using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The drydock's fidelity instruments: the grid walks the store's and the retrieve's sweeps share
/// (<see cref="GridTreeList"/>), the snapshots that compare a hull's state across a round trip (the
/// Snapshot and DeepSnapshot partials), and the legacy import's map-init transaction (the MapInit
/// partial).
/// </summary>
public sealed partial class DrydockFidelitySystem : EntitySystem
{
    [Dependency] private ISerializationManager _serialization = default!;

    /// <summary>
    /// The snapshot render's context (<see cref="DrydockEntityRefProbe"/>): it writes every
    /// <see cref="EntityUid"/> and <see cref="NetEntity"/> as a stub, so a render succeeds on what the
    /// map serializer writes and never follows a reference.
    /// </summary>
    private DrydockEntityRefProbe _probe = default!;

    public override void Initialize()
    {
        base.Initialize();
        _probe = new DrydockEntityRefProbe(_serialization);
        _refWriter = new DrydockEntityRefWriter(_serialization);
    }

    /// <summary>
    /// The live appearance dictionary. It is internal to the engine and its component is
    /// access-locked to the appearance system, so the component state is the supported read: the
    /// server's handler hands back the whole live dictionary whatever tick is asked for.
    /// </summary>
    private Dictionary<Enum, object> LiveAppearance(AppearanceComponent appearance)
    {
        return EntityManager.GetComponentState(EntityManager.EventBus, appearance, null, GameTick.Zero)
            is AppearanceComponentState state
            ? state.Data
            : new Dictionary<Enum, object>();
    }

    /// <summary>
    /// Every entity on the grid, including entities inside containers, since contained entities are
    /// transform children of their container's owner. Materialised, never lazy: every walk here is
    /// sliced, and a lazy walk reads the next entity's transform after the consumer has already
    /// parked on the previous one, which is harmless inside one tick and is not harmless across
    /// fifty.
    ///
    /// <para>The same set as an <c>AllEntityQuery</c> filtered on <c>GridUid</c>, because the engine
    /// sets <c>GridUid</c> from the parent chain and from nothing else, at the cost of the ship
    /// rather than the cost of the sector. The store's purge scan and the retrieve's sweeps walk
    /// this instead of querying the world: a world query is the one un-yieldable cost
    /// that grows with the round rather than with the hull. It has no paused check either, which the
    /// frozen ship needs.</para>
    /// </summary>
    public List<EntityUid> GridTreeList(EntityUid grid)
    {
        var result = new List<EntityUid>();
        if (TerminatingOrDeleted(grid))
            return result;

        // Index-walked rather than stack-popped, so the list is its own frontier and the grid comes
        // out first.
        result.Add(grid);
        for (var i = 0; i < result.Count; i++)
        {
            var children = Transform(result[i]).ChildEnumerator;
            while (children.MoveNext(out var child))
                result.Add(child);
        }

        return result;
    }

    /// <summary>
    /// Same walk as <see cref="GridTreeList"/>, paired with the index of each entity's parent in the
    /// returned list (the root's parent is null), for a caller that needs to rebuild parent links
    /// without a second pass over the tree.
    /// </summary>
    public List<(EntityUid Uid, int? Parent)> GridTreeListWithParents(EntityUid grid)
    {
        var result = new List<(EntityUid Uid, int? Parent)>();
        if (TerminatingOrDeleted(grid))
            return result;

        result.Add((grid, null));
        for (var i = 0; i < result.Count; i++)
        {
            var children = Transform(result[i].Uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                result.Add((child, i));
        }

        return result;
    }

    /// <summary>
    /// Memoized because the answer is a property of the type and a snapshot asks it once per
    /// component per entity; uncached, each ask builds two reflection arrays and calls
    /// <see cref="MemberInfo.GetCustomAttribute{T}"/> per member.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, MemberInfo[]> DataFieldCache = new();

    /// <summary>
    /// Which members of a component type the walk has to look at. Fields carrying a custom type
    /// serializer are skipped on the assumption that serializer handles them, which is the same rule
    /// the serializability audit applies.
    /// </summary>
    private static MemberInfo[] DataFields(Type type) => DataFieldCache.GetOrAdd(type, static t =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var members = new List<MemberInfo>();

        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var m in cur.GetFields(flags).Cast<MemberInfo>().Concat(cur.GetProperties(flags)))
            {
                var attr = m.GetCustomAttribute<DataFieldBaseAttribute>();
                if (attr != null && attr.CustomTypeSerializer == null)
                    members.Add(m);
            }
        }

        return members.ToArray();
    });

    private static Type MemberType(MemberInfo m) =>
        m is FieldInfo fi ? fi.FieldType : ((PropertyInfo) m).PropertyType;

    private static object? GetMember(object obj, MemberInfo m) =>
        m is FieldInfo fi ? fi.GetValue(obj) : ((PropertyInfo) m).GetValue(obj);

    private static void SetMember(object obj, MemberInfo m, object? value)
    {
        if (m is FieldInfo fi)
            fi.SetValue(obj, value);
        else if (m is PropertyInfo { CanWrite: true } pi)
            pi.SetValue(obj, value);
    }

    /// <summary>
    /// Dirty only what the engine will accept being dirtied. The map-init transaction puts a field
    /// back on whatever component map init rewrote, networked or not, and dirtying one that is not
    /// (<c>CargoMarketDataComponent</c> carries no <c>[NetworkedComponent]</c>) trips the entity
    /// manager's debug assert (<c>EntityManager.cs:437-438</c>).
    ///
    /// <para>The test is the component registration's <c>Networked</c> flag, which the factory
    /// sets from that same attribute when it registers the type, so it is the answer the engine
    /// asserts on, already cached per type.</para>
    /// </summary>
    private void DirtyIfNetworked(EntityUid uid, IComponent comp)
    {
        if (Factory.GetRegistration(comp).Networked)
            Dirty(uid, comp);
    }
}

/// <summary>
/// The serialization context a snapshot renders through (<c>DrydockFidelitySystem.RenderValue</c>). The base
/// serialization manager has no <see cref="EntityUid"/> or <see cref="NetEntity"/> writer, where the map serializer
/// has both, so without one a render throws on any field touching an entity reference. This writes each as a stub
/// node, with no logging and no uid mapping, so a render succeeds on exactly what the map serializer writes and never
/// follows a reference back through the container graph.
/// </summary>
internal sealed class DrydockEntityRefProbe : ISerializationContext, ITypeWriter<EntityUid>, ITypeWriter<NetEntity>
{
    private static readonly ValueDataNode Stub = new("0");

    public SerializationManager.SerializerProvider SerializerProvider { get; }

    public bool WritingReadingPrototypes => false;

    public DrydockEntityRefProbe(ISerializationManager serialization)
    {
        SerializerProvider = new SerializationManager.SerializerProvider(serialization);
        SerializerProvider.RegisterSerializer(this);
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        EntityUid value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null) => Stub;

    public DataNode Write(
        ISerializationManager serializationManager,
        NetEntity value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null) => Stub;
}
