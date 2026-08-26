using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Snapshots a value to a portable <see cref="DataNode"/> and rebuilds it later, for the state the
/// engine serializer cannot round-trip. It is not a replacement for that serializer, which owns the
/// ship's structure: which prototypes exist and where. This owns only the state that falls out.
///
/// <para>The strategy keeps reflection to a minimum. First ask the
/// <see cref="ISerializationManager"/> to write the value natively, because when it can, that is
/// both faithful and free. Only when it refuses do we decompose one level, as a dictionary, an
/// enumerable, or an object's instance fields, and recurse. Reflection therefore reaches exactly
/// as far as the engine cannot, and no further.</para>
///
/// <para>Some values cannot be round-tripped at all: a <see cref="Type"/> key, a boxed
/// <see cref="object"/>, a delegate, a native handle. Those come back as null from
/// <see cref="TryCapture"/> and the caller strips them, which is right, because they are without
/// exception round-scoped or derived state.</para>
/// </summary>
public sealed class DrydockReflectiveCapture
{
    private readonly ISerializationManager _serialization;

    /// <summary>
    /// Reflective object mappings tag their concrete type so restore can rebuild the right class
    /// through a base-typed field. It is a reserved key that real data cannot collide with, since
    /// it is not a valid C# member name.
    ///
    /// <para>This string is part of a persisted format. Changing it invalidates every stored
    /// revision written before the change, so it moves only behind a
    /// <see cref="DrydockFormat"/> bump with a ladder step to migrate the old spelling.</para>
    /// </summary>
    private const string TypeTag = "$drydock_type";

    public DrydockReflectiveCapture(ISerializationManager serialization)
    {
        _serialization = serialization;
    }

    /// <summary>
    /// Snapshot <paramref name="value"/>, or return null if it is structurally uncapturable and the
    /// caller should strip it instead. <paramref name="value"/> must be non-null.
    /// </summary>
    public DataNode? TryCapture(object value)
    {
        var type = value.GetType();

        // Let the engine do it if it can. We catch this ourselves rather than letting it reach the
        // map serializer, which logs and then aborts the whole grid.
        try
        {
            return _serialization.WriteValue(type, value, alwaysWrite: true);
        }
        catch
        {
            // Fall through to reflective decomposition.
        }

        // Types whose fields are meaningless to persist. IsAssignableFrom rather than equality,
        // because a Type instance is really a RuntimeType and a delegate is a concrete subclass;
        // the base check catches each whole family.
        if (typeof(Type).IsAssignableFrom(type)
            || typeof(Delegate).IsAssignableFrom(type)
            || typeof(MemberInfo).IsAssignableFrom(type)
            || type == typeof(object)
            || type == typeof(nint) || type == typeof(nuint) || type.IsPointer)
            return null;

        // Dictionary, as a sequence of key and value pairs, which handles any key type that is
        // itself capturable.
        if (value is IDictionary dict)
        {
            var seq = new SequenceDataNode();
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is null || TryCapture(entry.Key) is not { } keyNode)
                    return null;
                if (entry.Value is null)
                    return null; // Bail rather than half-capture.
                if (TryCapture(entry.Value) is not { } valNode)
                    return null;

                var pair = new MappingDataNode();
                pair.Add("k", keyNode);
                pair.Add("v", valNode);
                seq.Add(pair);
            }

            return seq;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var seq = new SequenceDataNode();
            foreach (var element in enumerable)
            {
                if (element is null || TryCapture(element) is not { } elemNode)
                    return null;
                seq.Add(elemNode);
            }

            return seq;
        }

        // Object, as a mapping of every instance field rather than only the [DataField]s. The
        // reason we are here at all is that the serializer's view of this type's fields is not
        // usable, so trusting the same attribute set would reproduce the gap.
        var mapping = new MappingDataNode();
        mapping.Add(TypeTag, new ValueDataNode(type.AssemblyQualifiedName!));
        foreach (var field in InstanceFields(type))
        {
            var fieldValue = field.GetValue(value);
            if (fieldValue is null)
                continue; // An absent key restores as the field's default.
            if (TryCapture(fieldValue) is not { } node)
                return null;

            mapping.Add(field.Name, node);
        }

        return mapping;
    }

    /// <summary>
    /// Rebuild a value of <paramref name="declaredType"/> from a node
    /// <see cref="TryCapture"/> produced.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The node names a type that no longer exists. Thrown rather than swallowed so the caller can
    /// record which field was lost and to what, but caught per field by the caller: a ship that
    /// comes back with one machine's stock missing is better than a ship that will not come back.
    /// </exception>
    public object? Restore(Type declaredType, DataNode node)
    {
        // Object mappings carry their own concrete type. Dictionaries and enumerables use the
        // declared type. Anything else was written natively and goes back the same way.
        if (node is MappingDataNode map && map.TryGet(TypeTag, out var typeNode))
        {
            var name = ((ValueDataNode)typeNode).Value;
            var concrete = ResolveType(name)
                           ?? throw new InvalidDataException($"Captured state names a type that no longer resolves: {name}");

            var obj = RuntimeHelpers.GetUninitializedObject(concrete);
            foreach (var field in InstanceFields(concrete))
            {
                if (!map.TryGet(field.Name, out var fieldNode))
                    continue;

                field.SetValue(obj, Restore(field.FieldType, fieldNode));
            }

            return obj;
        }

        if (node is SequenceDataNode seq)
        {
            if (IsDictionary(declaredType, out var keyType, out var valType))
            {
                var dict = (IDictionary) Activator.CreateInstance(declaredType)!;
                foreach (var pairNode in seq)
                {
                    var pair = (MappingDataNode) pairNode;
                    var key = Restore(keyType, pair["k"])!;
                    var val = Restore(valType, pair["v"]);
                    dict[key] = val;
                }

                return dict;
            }

            if (IsEnumerable(declaredType, out var elemType))
            {
                var listType = typeof(List<>).MakeGenericType(elemType);
                var list = (IList) Activator.CreateInstance(listType)!;
                foreach (var elemNode in seq)
                    list.Add(Restore(elemType, elemNode));

                if (declaredType.IsArray)
                {
                    var arr = Array.CreateInstance(elemType, list.Count);
                    list.CopyTo(arr, 0);
                    return arr;
                }

                // HashSet<T>, List<T> and friends all take an IEnumerable<T> constructor argument.
                return Activator.CreateInstance(declaredType, list) ?? list;
            }
        }

        return _serialization.Read(declaredType, node, notNullableOverride: true);
    }

    /// <summary>
    /// An assembly-qualified name embeds an assembly version, and ours moves whenever the engine
    /// pin does. So try the qualified name first, and fall back to matching the bare full name
    /// across loaded assemblies, which is what makes a revision written under an older engine
    /// still restorable under a newer one.
    /// </summary>
    private static Type? ResolveType(string assemblyQualifiedName)
    {
        if (Type.GetType(assemblyQualifiedName) is { } direct)
            return direct;

        var comma = assemblyQualifiedName.IndexOf(',');
        var fullName = comma < 0 ? assemblyQualifiedName : assemblyQualifiedName[..comma].Trim();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetType(fullName) is { } found)
                return found;
        }

        return null;
    }

    private static FieldInfo[] InstanceFields(Type type)
    {
        var fields = new List<FieldInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            // Includes readonly fields on purpose: constructor-assigned state is still state, and
            // reflection can set it back.
            fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        }

        return fields.ToArray();
    }

    private static bool IsDictionary(Type type, out Type keyType, out Type valType)
    {
        var iface = new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (iface != null)
        {
            keyType = iface.GetGenericArguments()[0];
            valType = iface.GetGenericArguments()[1];
            return true;
        }

        keyType = valType = typeof(object);
        return false;
    }

    private static bool IsEnumerable(Type type, out Type elemType)
    {
        if (type.IsArray)
        {
            elemType = type.GetElementType()!;
            return true;
        }

        var iface = new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (iface != null)
        {
            elemType = iface.GetGenericArguments()[0];
            return true;
        }

        elemType = typeof(object);
        return false;
    }
}
