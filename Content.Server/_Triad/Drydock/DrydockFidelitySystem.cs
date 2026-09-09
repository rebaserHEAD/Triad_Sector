using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The fidelity layer. The engine serializer owns a ship's structure; this owns the state it cannot
/// write. At store it walks the grid and, for every populated <c>[DataField]</c> the serializer
/// refuses, either captures the value into a <see cref="DrydockCapturedStateComponent"/> sidecar or
/// strips it, and either way clears the live field so the grid can be written. At retrieve it
/// re-applies what it captured over the reborn entities.
///
/// <para>It touches no content, and the only fork-specific knob is
/// <see cref="DrydockSerializationGap.CapturedTypes"/>. The default for anything the serializer
/// cannot write is to strip it, which is the safe direction: a stripped field comes back at its
/// default, where a forgotten entity would come back not at all.</para>
/// </summary>
public sealed partial class DrydockFidelitySystem : EntitySystem
{
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    /// <summary>
    /// The two keys the appearance sidecar wraps each value in. Both are part of a persisted
    /// format, like <c>DrydockReflectiveCapture</c>'s type tag: renaming either invalidates the
    /// appearance of every revision written before the change, so they move only behind a
    /// <see cref="DrydockFormat"/> bump.
    /// </summary>
    private const string AppearanceValueType = "t";

    /// <inheritdoc cref="AppearanceValueType"/>
    private const string AppearanceValue = "v";

    private DrydockReflectiveCapture _capture = default!;

    /// <summary>
    /// The probe's context, which gives the base serialization manager the extra capability the
    /// map serializer has over it: writing <see cref="EntityUid"/> and <see cref="NetEntity"/>
    /// references. Without it every field that reaches an entity reference (transform parents,
    /// container graphs, action lists, deed uids, artifact node graphs) reads as unserializable to
    /// the probe, which is a false negative, since the map serializer round-trips all of them
    /// through exactly this kind of context. Probing with it leaves those alone and flags only
    /// genuine gaps.
    /// </summary>
    private DrydockEntityRefProbe _probe = default!;

    /// <summary>
    /// Cached per type, since serializability is structural. A failure is cached unconditionally,
    /// because a type with no serializer never acquires one at runtime. A success is cached only
    /// when it was proven against a non-empty value: an empty collection writes fine even when its
    /// element type cannot, and caching that would poison every later probe of the same type.
    /// </summary>
    private readonly Dictionary<Type, bool> _serializable = new();

    /// <inheritdoc cref="_serializable"/>
    /// <remarks>
    /// The empty half, kept apart on purpose. Membership means "writes when empty", which is all an
    /// empty sample can establish; it never satisfies a probe of a populated value of the same type.
    /// </remarks>
    private readonly HashSet<Type> _emptyWritable = new();

    public override void Initialize()
    {
        base.Initialize();
        _capture = new DrydockReflectiveCapture(_serialization);
        _probe = new DrydockEntityRefProbe(_serialization);
    }

    /// <summary>
    /// Store step, called before the grid is serialized: capture or strip every unserializable
    /// populated field across the grid so the serializer can write it.
    ///
    /// <para>This mutates live components, and unlike the copying sidecars it must clear the live
    /// field, because the field choking the serializer is the entire reason it is here. So the
    /// ledger holds the original in-memory values of both buckets, and the caller's abort path hands
    /// it to <see cref="RestoreSnapshot"/> to put them straight back with no serialization round trip
    /// in the way.</para>
    ///
    /// <para>The ledger belongs to the caller, not to this walk. That is why the sliced form takes it
    /// as a parameter and why this synchronous form creates it before it starts: clearing happens
    /// per field, per entity, and the walk it happens in can now be abandoned half-way, so a ledger
    /// that only became visible on return would leave a ship blanked with no record of what was taken
    /// off it. Anything that throws between here and the commit leaves a live ship with blanked
    /// fields unless the caller restores from that ledger.</para>
    /// </summary>
    /// <remarks>
    /// The synchronous entry point, kept for callers that are not pipelines. It is the sliced walk
    /// driven by a slice that never suspends, so its task is always already completed and reading the
    /// result cannot block.
    /// </remarks>
    public DrydockFidelityCapture CaptureAndStrip(EntityUid grid)
    {
        var capture = new DrydockFidelityCapture();
        CaptureAndStripSliced(grid, capture, new DrydockSyncSlice(DrydockPhases.Store)).GetAwaiter().GetResult();
        return capture;
    }

    /// <inheritdoc cref="CaptureAndStrip"/>
    /// <remarks>
    /// The tick-budgeted form. Every snapshot entry is appended before the field it records is
    /// cleared, so an abort part-way through still restores every field already blanked, and the
    /// caller has assigned <paramref name="capture"/> to its own context before calling.
    ///
    /// <para>The tree is materialised first. The lazy walk reads each entity's transform after it has
    /// yielded the previous one, which is fine inside one tick and is not fine across fifty, and each
    /// entity is re-checked as it is consumed.</para>
    /// </remarks>
    public async Task CaptureAndStripSliced(EntityUid grid, DrydockFidelityCapture capture, IDrydockSlice slice)
    {
        var tree = GridTreeList(grid);
        await slice.Begin(DrydockPhase.Capture, tree.Count);

        for (var i = 0; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (!TerminatingOrDeleted(uid))
                CaptureAndStripEntity(uid, capture);

            await slice.Step(i);
        }
    }

    /// <summary>One entity's worth of the capture walk. Never yields, so a component's field set is
    /// always taken whole.</summary>
    private void CaptureAndStripEntity(EntityUid uid, DrydockFidelityCapture capture)
    {
        DrydockCapturedStateComponent? sidecar = null;

        foreach (var comp in AllComps(uid).ToList())
        {
            if (comp is DrydockCapturedStateComponent)
                continue;

            var compType = comp.GetType();
            foreach (var member in DataFields(compType))
            {
                var value = GetMember(comp, member);
                if (value == null)
                    continue;

                var memberType = MemberType(member);
                if (IsSerializable(memberType, value))
                    continue;

                if (IsCaptureType(memberType) && _capture.TryCapture(value) is { } node)
                {
                    if (sidecar == null)
                    {
                        // On the ledger before the component goes on, so an abort between the two
                        // still knows to take it back off.
                        capture.Sidecarred.Add(uid);
                        sidecar = EnsureComp<DrydockCapturedStateComponent>(uid);
                    }

                    var key = $"{compType.Name}|{member.Name}";
                    sidecar.Fields[key] = Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToString()));
                    capture.CapturedKeys.Add(key);
                }
                else
                {
                    capture.Stripped++;
                }

                // Snapshot the live value before clearing, so an aborted store puts it back
                // exactly. Captured or stripped, both were cleared and both restore.
                capture.Snapshot.Add((uid, comp, member, value));

                ClearMember(comp, member, memberType);
                DirtyIfNetworked(uid, comp);
            }
        }
    }

    /// <summary>
    /// Abort path, called from the store's unwind: put every field <see cref="CaptureAndStrip"/>
    /// cleared back to its original live value and remove the sidecars it added, leaving the
    /// still-live ship exactly as usable as it was. This works on the same live entities, with no
    /// serialization involved, which is what separates it from <see cref="RestoreCaptured"/>.
    /// </summary>
    /// <remarks>
    /// Synchronous, and it has to stay that way: it runs from an unwind, and an unwind that could
    /// suspend is an unwind a cancelled pipeline could never finish. The per-entry guards are what
    /// slicing added. The component references in the ledger can now be many ticks old and an entity
    /// that died in the meantime would throw on the dirty call - inside the unwind, which would
    /// abandon the ship on its staging map with no return leg.
    /// </remarks>
    public void RestoreSnapshot(DrydockFidelityCapture capture)
    {
        foreach (var (uid, comp, member, original) in capture.Snapshot)
        {
            if (comp.Deleted || TerminatingOrDeleted(uid))
                continue;

            SetMember(comp, member, original);
            DirtyIfNetworked(uid, comp);
        }

        foreach (var uid in capture.Sidecarred)
        {
            if (!TerminatingOrDeleted(uid))
                RemComp<DrydockCapturedStateComponent>(uid);
        }
    }

    /// <summary>
    /// Retrieve step, called after the grid is reloaded: re-apply captured state over the reborn
    /// entities and remove the sidecars.
    ///
    /// <para>Every failure here degrades to a counted, named skip rather than an exception,
    /// deliberately: a ship that comes back with one machine's stock missing beats a ship that will
    /// not come back. The returned report is what makes those skips visible, and a skip count above
    /// zero after an upstream merge is the signature of a rename orphaning a key.</para>
    /// </summary>
    public DrydockFidelityRestore RestoreCaptured(EntityUid grid)
    {
        return RestoreCapturedSliced(grid, new DrydockSyncSlice(DrydockPhases.Retrieve)).GetAwaiter().GetResult();
    }

    /// <inheritdoc cref="RestoreCaptured"/>
    /// <remarks>
    /// The tick-budgeted form. Snapshot-then-apply over a materialised tree, re-checking each entity
    /// as it is consumed, and it opens its own phase rather than stepping inside one the caller
    /// opened: two restores run back to back and neither can know the other's item count.
    /// </remarks>
    public async Task<DrydockFidelityRestore> RestoreCapturedSliced(EntityUid grid, IDrydockSlice slice)
    {
        var report = new DrydockFidelityRestore();
        var tree = GridTreeList(grid);
        await slice.Begin(DrydockPhase.Fidelity, tree.Count);

        for (var i = 0; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (TerminatingOrDeleted(uid) || !TryComp<DrydockCapturedStateComponent>(uid, out var sidecar))
            {
                await slice.Step(i);
                continue;
            }

            // Built once per entity rather than scanned per key. Component names are unique in the
            // registry, so the simple type name the key carries identifies one component.
            var byName = AllComps(uid).ToDictionary(c => c.GetType().Name, c => c);

            foreach (var (key, encoded) in sidecar.Fields)
            {
                var sep = key.IndexOf('|');
                if (sep < 0)
                {
                    report.Skip(key, "malformed key");
                    continue;
                }

                var compName = key[..sep];
                var fieldName = key[(sep + 1)..];

                if (!byName.TryGetValue(compName, out var comp))
                {
                    report.Skip(key, "no such component on the restored entity");
                    continue;
                }

                var member = DataFields(comp.GetType()).FirstOrDefault(m => m.Name == fieldName);
                if (member == null)
                {
                    report.Skip(key, "component no longer has that field");
                    continue;
                }

                try
                {
                    var yaml = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    using var reader = new StringReader(yaml);
                    var node = DataNodeParser.ParseYamlStream(reader).First().Root;

                    SetMember(comp, member, _capture.Restore(MemberType(member), node));
                    DirtyIfNetworked(uid, comp);
                    report.Applied++;
                }
                catch (Exception e)
                {
                    report.Skip(key, e.Message);
                }
            }

            RemComp<DrydockCapturedStateComponent>(uid);
            await slice.Step(i);
        }

        if (report.Skipped.Count > 0)
        {
            Log.Warning(
                $"drydock fidelity restore skipped {report.Skipped.Count} captured field(s) on grid {ToPrettyString(grid)}: "
                + string.Join("; ", report.Skipped.Select(s => $"{s.Key} ({s.Reason})")));
        }

        return report;
    }

    /// <summary>
    /// Store step for the appearance carrier: copy every entity's live appearance data into a
    /// <see cref="DrydockAppearanceComponent"/> sidecar, leaving the live data alone.
    ///
    /// <para>This is the one carrier the probe above could never have found, because appearance
    /// data is not a <c>[DataField]</c> at all and the probe only ever asks about declared fields.
    /// See the sidecar's own summary for what that costs a retrieved ship.</para>
    ///
    /// <para>Most of what this captures is redundant, because most systems set their appearance in
    /// <c>ComponentStartup</c>, which does re-run on load, and those entities re-derive the same
    /// values a moment later. The capture is for the rest: keys written only on map init or only on
    /// a state change that will not happen again. Capturing both is deliberate, since nothing at
    /// store time can tell them apart, and re-applying a value the system was about to derive
    /// identically costs nothing.</para>
    /// </summary>
    /// <remarks>
    /// A key whose value cannot be captured at all is dropped rather than failing the store: an
    /// appearance value is a visual, and no visual is worth refusing a ship over.
    /// </remarks>
    public List<EntityUid> CaptureAppearance(EntityUid grid)
    {
        var injected = new List<EntityUid>();
        CaptureAppearanceSliced(grid, injected, new DrydockSyncSlice(DrydockPhases.Store)).GetAwaiter().GetResult();
        return injected;
    }

    /// <inheritdoc cref="CaptureAppearance"/>
    /// <remarks>
    /// The tick-budgeted form. Like the ledgers on the store's context, <paramref name="injected"/>
    /// belongs to the caller and is appended to before the sidecar it records is added, so an abort
    /// part-way through the walk still takes back off every sidecar already put on.
    /// </remarks>
    public async Task CaptureAppearanceSliced(EntityUid grid, List<EntityUid> injected, IDrydockSlice slice)
    {
        var tree = GridTreeList(grid);
        await slice.Begin(DrydockPhase.Sidecars, tree.Count);

        for (var i = 0; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (TerminatingOrDeleted(uid) || !TryComp<AppearanceComponent>(uid, out var appearance))
            {
                await slice.Step(i);
                continue;
            }

            DrydockAppearanceComponent? sidecar = null;
            foreach (var (key, value) in LiveAppearance(appearance))
            {
                if (_capture.TryCapture(value) is not { } node)
                    continue;

                // The value's own concrete type rides alongside it. An appearance value is declared
                // as object, so unlike a data field there is no declared type for the restore to
                // read it back as.
                var wrapped = new MappingDataNode();
                wrapped.Add(AppearanceValueType, new ValueDataNode(value.GetType().AssemblyQualifiedName!));
                wrapped.Add(AppearanceValue, node);

                if (sidecar == null)
                {
                    injected.Add(uid);
                    sidecar = EnsureComp<DrydockAppearanceComponent>(uid);
                }

                sidecar.Data[$"{key.GetType().FullName}|{key}"] =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(wrapped.ToString()));
            }

            await slice.Step(i);
        }
    }

    /// <summary>
    /// Retrieve step for the appearance carrier: put every captured key back through the appearance
    /// system and drop the sidecars.
    ///
    /// <para>Runs early in the rehydration block, before the steps that correct specific machines.
    /// That order is load-bearing in one direction: a lathe's captured appearance says it was
    /// mid-production, and the marker that made that true does not ride the save, so the lathe step
    /// afterwards is what settles the animation against the marker the ship actually came back
    /// with. Restoring appearance last would reinstate the frozen animation this carrier exists to
    /// fix.</para>
    /// </summary>
    public DrydockFidelityRestore RestoreAppearance(EntityUid grid)
    {
        return RestoreAppearanceSliced(grid, new DrydockSyncSlice(DrydockPhases.Retrieve)).GetAwaiter().GetResult();
    }

    /// <inheritdoc cref="RestoreAppearance"/>
    /// <remarks>Same shape as <see cref="RestoreCapturedSliced"/>: materialised tree, re-checked per
    /// entity, its own phase.</remarks>
    public async Task<DrydockFidelityRestore> RestoreAppearanceSliced(EntityUid grid, IDrydockSlice slice)
    {
        var report = new DrydockFidelityRestore();
        var tree = GridTreeList(grid);
        await slice.Begin(DrydockPhase.Fidelity, tree.Count);

        for (var i = 0; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (TerminatingOrDeleted(uid) || !TryComp<DrydockAppearanceComponent>(uid, out var sidecar))
            {
                await slice.Step(i);
                continue;
            }

            foreach (var (key, encoded) in sidecar.Data)
            {
                try
                {
                    var sep = key.IndexOf('|');
                    if (sep < 0)
                    {
                        report.Skip(key, "malformed key");
                        continue;
                    }

                    var keyType = DrydockReflectiveCapture.ResolveType(key[..sep]);
                    if (keyType is not { IsEnum: true })
                    {
                        report.Skip(key, "no such appearance key type");
                        continue;
                    }

                    if (!Enum.TryParse(keyType, key[(sep + 1)..], out var parsed) || parsed is not Enum enumKey)
                    {
                        report.Skip(key, "appearance key type no longer has that member");
                        continue;
                    }

                    var yaml = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    using var reader = new StringReader(yaml);
                    var node = (MappingDataNode) DataNodeParser.ParseYamlStream(reader).First().Root;

                    var valueType = DrydockReflectiveCapture.ResolveType(
                        ((ValueDataNode) node[AppearanceValueType]).Value);

                    if (valueType == null)
                    {
                        report.Skip(key, "no such appearance value type");
                        continue;
                    }

                    if (_capture.Restore(valueType, node[AppearanceValue]) is not { } value)
                    {
                        report.Skip(key, "value restored as null");
                        continue;
                    }

                    _appearance.SetData(uid, enumKey, value);
                    report.Applied++;
                }
                catch (Exception e)
                {
                    report.Skip(key, e.Message);
                }
            }

            RemComp<DrydockAppearanceComponent>(uid);
            await slice.Step(i);
        }

        if (report.Skipped.Count > 0)
        {
            Log.Warning(
                $"drydock appearance restore skipped {report.Skipped.Count} key(s) on grid {ToPrettyString(grid)}: "
                + string.Join("; ", report.Skipped.Select(s => $"{s.Key} ({s.Reason})")));
        }

        return report;
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

    private bool IsSerializable(Type type, object value)
    {
        if (_serializable.TryGetValue(type, out var cached))
            return cached;

        // An empty collection cannot prove its type serializable, so the success below is not
        // cached in _serializable. It can still be remembered on its own: a type that writes when
        // empty writes when empty every time, and the verdict that matters for a populated value is
        // still taken the first time one turns up. Without this the refusal to cache costs a probe
        // per occurrence, and empty collections are the most common field state on a ship.
        var empty = value is ICollection { Count: 0 };
        if (empty && _emptyWritable.Contains(type))
            return true;

        try
        {
            _serialization.WriteValue(type, value, alwaysWrite: true, context: _probe);

            if (empty)
                _emptyWritable.Add(type);
            else
                _serializable[type] = true;

            return true;
        }
        catch (Exception e)
        {
            // The classification is shared with the audit gate on purpose. The gate's control is
            // what proves it still recognises a real gap, and that proof only covers this code
            // while this code is the same code.
            if (!DrydockSerializationGap.IsNoCoverage(e))
                return true; // Some other failure. Not a gap; leave it to the serializer.

            _serializable[type] = false;
            return false;
        }
    }

    /// <summary>
    /// A type is worth capturing when it, an element of it, or any generic argument anywhere inside
    /// it is on the manifest, so a dictionary of market data is captured because its value type is.
    /// </summary>
    private static bool IsCaptureType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (DrydockSerializationGap.CapturedTypes.Contains(type))
            return true;
        if (type.IsArray)
            return IsCaptureType(type.GetElementType()!);

        return type.IsGenericType && type.GetGenericArguments().Any(IsCaptureType);
    }

    /// <summary>
    /// Every entity on the grid, including entities inside containers, since contained entities are
    /// transform children of their container's owner. Materialised, never lazy: every walk here is
    /// now sliced, and a lazy walk reads the next entity's transform after the consumer has already
    /// parked on the previous one, which is harmless inside one tick and is not harmless across
    /// fifty.
    /// </summary>
    private List<EntityUid> GridTreeList(EntityUid grid)
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
    /// Memoized because the answer is a property of the type and the store asks it once per
    /// component per entity: uncached it built two reflection arrays and asked
    /// <see cref="MemberInfo.GetCustomAttribute{T}"/> per member every time, which on a capital ship
    /// was most of the store's capture phase.
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

    private static void ClearMember(object obj, MemberInfo m, Type type)
    {
        object? cleared;
        if (type.IsValueType)
            cleared = Activator.CreateInstance(type);
        else if (type.IsArray)
            cleared = Array.CreateInstance(type.GetElementType()!, 0); // An empty array: a null one is refused by the serializer on any non-nullable field.
        else if (typeof(IEnumerable).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
            cleared = Activator.CreateInstance(type); // An empty collection rather than null: no NRE, and it writes fine.
        else
            cleared = null;

        SetMember(obj, m, cleared);
    }
    /// <summary>
    /// Dirty only what the engine will accept being dirtied.
    ///
    /// <para>This layer walks every component carrying a populated field the serializer
    /// cannot write, and nothing about that description says the component is networked.
    /// <c>CargoMarketDataComponent</c> is a live example: it holds one of the two captured
    /// types and carries no <c>[NetworkedComponent]</c>. Dirtying it trips a debug assert in
    /// the entity manager, which on a development server throws inside the capture, aborts
    /// the store, and refuses the ship.</para>
    ///
    /// <para>The attribute test is the same one the engine asserts on. Results are cached by
    /// type because this is called once per cleared field.</para>
    /// </summary>
    private void DirtyIfNetworked(EntityUid uid, IComponent comp)
    {
        var type = comp.GetType();

        if (!NetworkedCache.TryGetValue(type, out var networked))
        {
            networked = type.GetCustomAttribute<NetworkedComponentAttribute>() != null;
            NetworkedCache[type] = networked;
        }

        if (networked)
            Dirty(uid, comp);
    }

    private static readonly Dictionary<Type, bool> NetworkedCache = new();
}

/// <summary>
/// The ledger a single <see cref="DrydockFidelitySystem.CaptureAndStrip"/> hands back: the live
/// values it cleared, to put back verbatim on abort, and the entities it gave a sidecar, to take
/// back off. Discarded on a successful store, since the grid despawns and there is nothing to undo.
/// </summary>
public sealed class DrydockFidelityCapture
{
    /// <summary>Every field cleared, with the exact live value to restore on abort.</summary>
    public readonly List<(EntityUid Uid, IComponent Comp, MemberInfo Member, object? Original)> Snapshot = new();

    /// <summary>Entities that received a fresh sidecar, removed again on abort.</summary>
    public readonly List<EntityUid> Sidecarred = new();

    /// <summary>
    /// Every <c>Component|Field</c> key written, sorted, so the hash below is stable regardless of
    /// walk order.
    /// </summary>
    public readonly SortedSet<string> CapturedKeys = new(StringComparer.Ordinal);

    /// <summary>How many fields were cleared without being captured. Recorded, not alarming.</summary>
    public int Stripped;

    /// <summary>
    /// The revision's <c>captured_key_hash</c>. Comparing it against the key set a later build
    /// produces is how a C# rename that would silently orphan a key becomes a detected drift rather
    /// than a field that quietly comes back at its default.
    /// </summary>
    public byte[] ComputeCapturedKeyHash()
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', CapturedKeys)));
    }
}

/// <summary>
/// What a retrieve actually managed to re-apply. Every skip is named, because the counter is the
/// alert: a skip after an upstream merge means a rename orphaned a captured key.
/// </summary>
public sealed class DrydockFidelityRestore
{
    public int Applied;

    public readonly List<(string Key, string Reason)> Skipped = new();

    public void Skip(string key, string reason) => Skipped.Add((key, reason));
}

/// <summary>
/// A minimal serialization context whose only job is to make the probe's answer match the map
/// serializer's. <c>EntitySerializer</c> supplies its own <see cref="EntityUid"/> writer, and the
/// base serialization manager has none, so a naked probe throws on any field touching an entity
/// reference and the fidelity pass would strip state the serializer round-trips perfectly well.
/// This registers a no-op writer that returns a stub node, with no logging and no uid mapping, so
/// the probe succeeds on exactly what the map serializer succeeds on.
/// </summary>
internal sealed class DrydockEntityRefProbe : ISerializationContext, ITypeWriter<EntityUid>, ITypeWriter<NetEntity>
{
    private static readonly ValueDataNode Stub = new("0");

    public SerializationManager.SerializerProvider SerializerProvider { get; }

    public bool WritingReadingPrototypes => false;

    // The provider takes the manager as of engine 287.
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

    // The map serializer writes a NetEntity exactly the way it writes an EntityUid, remapped to a
    // document id, and the base manager has no writer for it either. Without this stub every
    // NetEntity-bearing field read as a gap and was blanked before the save: an artifact's node
    // graph came out with its vertex array set to null, which the serializer then refused, so a
    // ship carrying an artifact could not be stored at all (reported on Damascus),
    // and an analysis console lost the analyzer it was linked to.
    public DataNode Write(
        ISerializationManager serializationManager,
        NetEntity value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null) => Stub;
}
