using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Content.Server._Triad.Drydock.Codec;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// One entity's carried values during a store (<see cref="GridStoringEvent.Carry{T}"/>), each encoded the moment it is
/// carried, so a subscriber that changes its object afterwards changes nothing stored.
/// </summary>
public sealed class DrydockCarrySink
{
    private readonly DrydockCodec _codec;
    private readonly Entity<MetaDataComponent> _entity;
    private readonly ICollection<DrydockUnwritableCarried> _unwritable;
    private readonly SortedDictionary<string, DataNode> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);

    internal DrydockCarrySink(DrydockCodec codec, Entity<MetaDataComponent> entity, ICollection<DrydockUnwritableCarried> unwritable)
    {
        _codec = codec;
        _entity = entity;
        _unwritable = unwritable;
    }

    internal void Add(string key, Type type, object value)
    {
        if (_values.ContainsKey(key) || _refused.Contains(key))
            throw new InvalidOperationException($"Drydock store: '{key}' is carried twice on {Prototype ?? "(no prototype)"} {_entity.Owner}.");

        var refusal = DrydockCarriedGuard.Refusal(type);
        if (refusal == null && value.GetType() != type)
            refusal = DrydockCarriedGuard.Refusal(value.GetType());

        if (refusal != null)
        {
            Refuse(key, "refused", refusal);
            return;
        }

        try
        {
            _values[key] = _codec.WriteValue(type, value);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Refuse(key, e.GetType().Name, e.Message);
        }
    }

    /// <summary>The row, keys in ordinal order whatever order the subscribers ran in, or null when nothing was carried.</summary>
    internal MappingDataNode? ToRow()
    {
        if (_values.Count == 0)
            return null;

        var row = new MappingDataNode();
        foreach (var (key, node) in _values)
            row[key] = node;

        return row;
    }

    private string? Prototype => _entity.Comp.EntityPrototype?.ID;

    private void Refuse(string key, string exception, string message)
    {
        _refused.Add(key);
        _unwritable.Add(new DrydockUnwritableCarried(_entity.Owner, Prototype, key, exception, message));
    }
}

/// <summary>
/// Whether a type can be carried: nothing inside it, through data fields, array elements and generic arguments, may be
/// an entity reference, coordinates, a component, a <see cref="TimeSpan"/> or an untyped <see cref="object"/>. A
/// reference would name an entity of another round and an absolute time a moment on another clock; an
/// <see cref="object"/> hides what it holds. Answered once per type.
/// </summary>
internal static class DrydockCarriedGuard
{
    private const int MaxDepth = 8;

    private static readonly Type[] Refused =
    {
        typeof(EntityUid), typeof(NetEntity), typeof(EntityCoordinates), typeof(NetCoordinates), typeof(TimeSpan), typeof(object),
    };

    private static readonly ConcurrentDictionary<Type, string?> Refusals = new();

    /// <summary>Why <paramref name="type"/> cannot be carried, naming the path to what refuses it, or null when it can.</summary>
    public static string? Refusal(Type type) =>
        Refusals.GetOrAdd(type, t => Walk(t, t.Name, 0, new HashSet<Type>()));

    private static string? Walk(Type type, string path, int depth, HashSet<Type> seen)
    {
        if (Array.IndexOf(Refused, type) >= 0)
            return $"{path} is a {type.Name}";

        if (typeof(IComponent).IsAssignableFrom(type))
            return $"{path} is a component";

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || !seen.Add(type))
            return null;

        // Past the limit the guard cannot see what the type holds, so it refuses rather than passes.
        if (depth >= MaxDepth)
            return $"{path} is nested deeper than {MaxDepth}";

        if (type.GetElementType() is { } element && Walk(element, $"{path}[]", depth + 1, seen) is { } inElement)
            return inElement;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                if (Walk(argument, $"{path}<{argument.Name}>", depth + 1, seen) is { } inArgument)
                    return inArgument;
            }
        }

        for (var declaring = type; declaring != null && declaring != typeof(object); declaring = declaring.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var field in declaring.GetFields(flags))
            {
                if (field.GetCustomAttribute<DataFieldBaseAttribute>() != null
                    && Walk(field.FieldType, $"{path}.{field.Name}", depth + 1, seen) is { } inField)
                    return inField;
            }

            foreach (var property in declaring.GetProperties(flags))
            {
                if (property.GetCustomAttribute<DataFieldBaseAttribute>() != null
                    && Walk(property.PropertyType, $"{path}.{property.Name}", depth + 1, seen) is { } inProperty)
                    return inProperty;
            }
        }

        return null;
    }
}
