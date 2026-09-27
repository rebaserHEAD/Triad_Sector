using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>Where a <see cref="DrydockRetiredKey"/> sits in a stored image.</summary>
public enum DrydockRetiredKind
{
    /// <summary>A manifest row's key, spelled as <see cref="DrydockManifestMember.Key"/> spells it.</summary>
    ManifestMember,

    /// <summary>A component row, by the component's registration name.</summary>
    Component,

    /// <summary>A key at the top of a component row, <c>Component.tag</c>, the tag as the serializer writes it.</summary>
    DataField,
}

/// <param name="Kind">Where the key sits.</param>
/// <param name="Key">The key exactly as a stored image spells it.</param>
/// <param name="Reason">Why dropping the stored value loses nothing, with its receipt.</param>
/// <param name="Commit">The commit after which the stored value is no longer needed.</param>
public sealed record DrydockRetiredKey(DrydockRetiredKind Kind, string Key, string Reason, string Commit);

/// <summary>
/// Keys a stored image may carry that the codec no longer reads, each dropped on purpose and counted. A key that is
/// neither live nor listed here is a defect, and the reader throws on it.
///
/// <para>A retirement changes what a newer reader accepts and nothing any key means, so it sits within
/// <see cref="DrydockFormat.Current"/>: the writer never writes a retired key, and an older reader still listing the
/// member finds it absent and sets nothing. A value that is still needed under another key or with another meaning is a
/// format version, never a retirement, and a retired key is never reused for a new meaning. The build-time audit keeps
/// every entry out of the live members, the registered components and the keys a component declares.</para>
/// </summary>
public static class DrydockRetiredKeys
{
    public static readonly ImmutableArray<DrydockRetiredKey> All = ImmutableArray.Create(
        new DrydockRetiredKey(
            DrydockRetiredKind.ManifestMember,
            "Wires.StateData[PowerWireActionKey.CutWires]",
            "the count of cut power wires: the wires' rebuild at restore sets it to zero (PowerWireAction.cs:180-183), "
            + "and the restore then sets it from the wires that came back cut (WiresCarrySystem.OnRestored)",
            "f2ebe8ee61"));

    /// <summary>The retirement in <see cref="All"/> naming <paramref name="key"/>, or null when nothing retires it.</summary>
    public static DrydockRetiredKey? Find(DrydockRetiredKind kind, string key) => Find(All, kind, key);

    /// <summary>The retirement in <paramref name="list"/> naming <paramref name="key"/>, or null when nothing retires it.</summary>
    public static DrydockRetiredKey? Find(IEnumerable<DrydockRetiredKey> list, DrydockRetiredKind kind, string key) =>
        list.FirstOrDefault(retired => retired.Kind == kind && retired.Key == key);
}
