using System;
using System.Collections.Generic;
using Robust.Shared.Reflection;
using Robust.Shared.Serialization;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// Which type a stored appearance entry may be read as. The type name comes out of a row, so it names a type the load
/// may instantiate only through this gate, and nothing else picks a type from a string: the name resolves through the
/// reflection manager, and the type is admitted only when it is an enum or carries <see cref="NetSerializableAttribute"/>
/// (appearance keys and values are networked, so every real one is one or the other), or is on the short list of engine
/// value types named below. Anything else is refused, and the caller counts it.
/// </summary>
public static class DrydockAppearanceTypes
{
    /// <summary>
    /// Plain value types an appearance entry carries that are neither an enum nor networked by attribute. Named by type,
    /// never by namespace or assembly, and each here because a stored image held it (the 2026-09-19 baseline over the
    /// standing rungs: bool, int, float, string and Color, none of them networked by attribute).
    /// </summary>
    private static readonly Type[] EngineValueTypes =
    {
        typeof(bool),
        typeof(int),
        typeof(float),
        typeof(string),
        typeof(Robust.Shared.Maths.Color),
    };

    private static readonly Dictionary<string, Type> EngineValueTypesByName = BuildEngineValueTypesByName();

    private static Dictionary<string, Type> BuildEngineValueTypesByName()
    {
        var byName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in EngineValueTypes)
            byName[type.FullName!] = type;

        return byName;
    }

    /// <summary>Whether a type the reflection manager resolved may be read as an appearance value.</summary>
    public static bool IsAdmissible(Type type) =>
        type.IsEnum || type.IsDefined(typeof(NetSerializableAttribute), inherit: false);

    /// <summary>
    /// The type a stored name (as the store writes it, the assembly-qualified name) may be read as, or false when the load
    /// must not read it. A generic type name is refused: no appearance value is one.
    /// </summary>
    public static bool TryResolve(IReflectionManager reflection, string storedName, out Type type)
    {
        type = null!;
        if (string.IsNullOrEmpty(storedName) || storedName.Contains('['))
            return false;

        var comma = storedName.IndexOf(',');
        var fullName = comma < 0 ? storedName : storedName[..comma].Trim();

        if (EngineValueTypesByName.TryGetValue(fullName, out var engine))
        {
            type = engine;
            return true;
        }

        if (reflection.GetType(fullName) is not { } resolved || !IsAdmissible(resolved))
            return false;

        type = resolved;
        return true;
    }
}
