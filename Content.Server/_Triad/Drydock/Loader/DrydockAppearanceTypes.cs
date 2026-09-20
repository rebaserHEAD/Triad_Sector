using System;
using System.Collections.Generic;
using Robust.Shared.Reflection;
using Robust.Shared.Serialization;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// Which type a stored appearance entry may be read as. The type name comes out of a row, so it names a type the load
/// may instantiate only through this gate, and nothing else picks a type from a string. A stored name is either a key of
/// the closed table below, matched by exact equality, or a name the reflection manager resolves whose type is an enum or
/// carries <see cref="NetSerializableAttribute"/> (appearance keys and values are networked, so every real one is one or the
/// other). Anything else is refused, and the caller counts it.
///
/// <para>The store writes <see cref="KeyOf"/>, the type's <see cref="Type.ToString"/>: the same text as its full name for
/// a plain type, and free of any assembly clause, so a runtime's version never enters a stored image. The reflection
/// manager only searches the content and engine assemblies, which is why a framework type has to be in the table.</para>
/// </summary>
public static class DrydockAppearanceTypes
{
    /// <summary>
    /// Types an appearance entry may carry that the reflection manager does not resolve and that are not networked by
    /// attribute: every C# integer and floating-point primitive, bool, string and Color, and one closed generic, a dictionary
    /// of two strings, which the pipe system writes for a pipe's layer visuals. All inert values (ruled 2026-09-19: the
    /// standing rungs are a sample of what a stored image holds, not the population, so the list is the set and not what a run
    /// happened to show). Named by type. Not here: char, decimal, IntPtr, object, every other generic, and everything else.
    /// </summary>
    private static readonly Type[] TableTypes =
    {
        typeof(bool),
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(float),
        typeof(double),
        typeof(string),
        typeof(Robust.Shared.Maths.Color),
        typeof(Dictionary<string, string>),
    };

    private static readonly Dictionary<string, Type> TableByKey = BuildTableByKey();

    private static Dictionary<string, Type> BuildTableByKey()
    {
        var byKey = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in TableTypes)
            byKey[KeyOf(type)] = type;

        return byKey;
    }

    /// <summary>The types in the closed table.</summary>
    public static IReadOnlyList<Type> Table => TableTypes;

    /// <summary>The text the store writes for a value of <paramref name="type"/>, and the text <see cref="TryResolve"/> reads back.</summary>
    public static string KeyOf(Type type) => type.ToString();

    /// <summary>Whether a type the reflection manager resolved may be read as an appearance value.</summary>
    public static bool IsAdmissible(Type type) =>
        type.IsEnum || type.IsDefined(typeof(NetSerializableAttribute), inherit: false);

    /// <summary>
    /// The type a stored name (as <see cref="KeyOf"/> wrote it) may be read as, or false when the load must not read it.
    /// A name that is not a table key and contains a bracket or a comma, which is any generic and any assembly-qualified
    /// name, is refused before the reflection manager sees it.
    /// </summary>
    public static bool TryResolve(IReflectionManager reflection, string storedName, out Type type)
    {
        type = null!;
        if (string.IsNullOrEmpty(storedName))
            return false;

        if (TableByKey.TryGetValue(storedName, out var listed))
        {
            type = listed;
            return true;
        }

        if (storedName.Contains('[') || storedName.Contains(','))
            return false;

        if (reflection.GetType(storedName) is not { } resolved || !IsAdmissible(resolved))
            return false;

        type = resolved;
        return true;
    }
}
