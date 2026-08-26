using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

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
public sealed class DrydockFidelitySystem : EntitySystem
{
    [Dependency] private readonly ISerializationManager _serialization = default!;

    private DrydockReflectiveCapture _capture = default!;

    /// <summary>
    /// The probe's context, which gives the base serialization manager the one extra capability the
    /// map serializer has over it: writing <see cref="EntityUid"/> references. Without it every
    /// field that reaches an entity reference (transform parents, container graphs, action lists,
    /// deed uids) reads as unserializable to the probe, which is a false negative, since the map
    /// serializer round-trips all of them through exactly this kind of context. Probing with it
    /// leaves those alone and flags only genuine gaps.
    /// </summary>
    private DrydockEntityRefProbe _probe = default!;

    /// <summary>
    /// Cached per type, since serializability is structural. A failure is cached unconditionally,
    /// because a type with no serializer never acquires one at runtime. A success is cached only
    /// when it was proven against a non-empty value: an empty collection writes fine even when its
    /// element type cannot, and caching that would poison every later probe of the same type.
    /// </summary>
    private readonly Dictionary<Type, bool> _serializable = new();

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
    /// returned ledger holds the original in-memory values of both buckets, and the caller's abort
    /// path hands it to <see cref="RestoreSnapshot"/> to put them straight back with no
    /// serialization round trip in the way.</para>
    ///
    /// <para>The caller's protective <c>try</c> must already be open when this is called. Clearing
    /// happens here, and anything that throws between here and the commit leaves a live ship with
    /// blanked fields if nothing catches it.</para>
    /// </summary>
    public DrydockFidelityCapture CaptureAndStrip(EntityUid grid)
    {
        var capture = new DrydockFidelityCapture();

        foreach (var uid in GridTree(grid))
        {
            DrydockCapturedStateComponent? sidecar = null;

            foreach (var comp in EntityManager.GetComponents(uid).ToList())
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
                            sidecar = EnsureComp<DrydockCapturedStateComponent>(uid);
                            capture.Sidecarred.Add(uid);
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
                    Dirty(uid, comp);
                }
            }
        }

        return capture;
    }

    /// <summary>
    /// Abort path, called from the store's failure branch: put every field
    /// <see cref="CaptureAndStrip"/> cleared back to its original live value and remove the
    /// sidecars it added, leaving the still-live ship exactly as usable as it was. This works on
    /// the same live entities, with no serialization involved, which is what separates it from
    /// <see cref="RestoreCaptured"/>.
    /// </summary>
    public void RestoreSnapshot(DrydockFidelityCapture capture)
    {
        foreach (var (uid, comp, member, original) in capture.Snapshot)
        {
            SetMember(comp, member, original);
            Dirty(uid, comp);
        }

        foreach (var uid in capture.Sidecarred)
            RemComp<DrydockCapturedStateComponent>(uid);
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
        var report = new DrydockFidelityRestore();

        foreach (var uid in GridTree(grid).ToList())
        {
            if (!TryComp<DrydockCapturedStateComponent>(uid, out var sidecar))
                continue;

            // Built once per entity rather than scanned per key. Component names are unique in the
            // registry, so the simple type name the key carries identifies one component.
            var byName = EntityManager.GetComponents(uid).ToDictionary(c => c.GetType().Name, c => c);

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
                    Dirty(uid, comp);
                    report.Applied++;
                }
                catch (Exception e)
                {
                    report.Skip(key, e.Message);
                }
            }

            RemComp<DrydockCapturedStateComponent>(uid);
        }

        if (report.Skipped.Count > 0)
        {
            Log.Warning(
                $"drydock fidelity restore skipped {report.Skipped.Count} captured field(s) on grid {ToPrettyString(grid)}: "
                + string.Join("; ", report.Skipped.Select(s => $"{s.Key} ({s.Reason})")));
        }

        return report;
    }

    private bool IsSerializable(Type type, object value)
    {
        if (_serializable.TryGetValue(type, out var cached))
            return cached;

        try
        {
            _serialization.WriteValue(type, value, alwaysWrite: true, context: _probe);

            if (value is not ICollection { Count: 0 })
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
    /// transform children of their container's owner.
    /// </summary>
    private IEnumerable<EntityUid> GridTree(EntityUid grid)
    {
        var stack = new Stack<EntityUid>();
        stack.Push(grid);
        while (stack.Count > 0)
        {
            var uid = stack.Pop();
            yield return uid;

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
        }
    }

    /// <summary>
    /// Fields carrying a custom type serializer are skipped on the assumption that serializer
    /// handles them, which is the same rule the serializability audit applies.
    /// </summary>
    private static IEnumerable<MemberInfo> DataFields(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var m in t.GetFields(flags).Cast<MemberInfo>().Concat(t.GetProperties(flags)))
            {
                var attr = m.GetCustomAttribute<DataFieldBaseAttribute>();
                if (attr != null && attr.CustomTypeSerializer == null)
                    yield return m;
            }
        }
    }

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
        else if (typeof(IEnumerable).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
            cleared = Activator.CreateInstance(type); // An empty collection rather than null: no NRE, and it writes fine.
        else
            cleared = null;

        SetMember(obj, m, cleared);
    }
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
internal sealed class DrydockEntityRefProbe : ISerializationContext, ITypeWriter<EntityUid>
{
    private static readonly ValueDataNode Stub = new("0");

    public SerializationManager.SerializerProvider SerializerProvider { get; }

    public bool WritingReadingPrototypes => false;

    // The provider takes the manager as of engine 287; the reference this was ported from was
    // written against a version where it did not. Nothing about the probe's behaviour changes.
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
}
