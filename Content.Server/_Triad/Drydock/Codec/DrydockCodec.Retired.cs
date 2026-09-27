using System.Collections.Generic;
using System.Collections.Immutable;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.Server._Triad.Drydock.Codec;

public sealed partial class DrydockCodec
{
    /// <summary>
    /// The retirements this codec's reads honour: <see cref="DrydockRetiredKeys.All"/>, unless the caller names others, as
    /// a control does.
    /// </summary>
    public ImmutableArray<DrydockRetiredKey> Retirements { get; init; } = DrydockRetiredKeys.All;

    /// <summary>
    /// Every retired key this codec's reads dropped, with how many times: once per row that carried it.
    /// </summary>
    public Dictionary<DrydockRetiredKey, int> RetiredDropped { get; } = new();

    /// <summary>The retirement in <see cref="Retirements"/> naming <paramref name="key"/>, or null.</summary>
    public DrydockRetiredKey? FindRetired(DrydockRetiredKind kind, string key) =>
        DrydockRetiredKeys.Find(Retirements, kind, key);

    /// <summary>Counts one row's drop of <paramref name="retired"/>.</summary>
    public void CountRetired(DrydockRetiredKey retired) =>
        RetiredDropped[retired] = RetiredDropped.GetValueOrDefault(retired) + 1;

    /// <summary>
    /// A stored component row as the reader may take it: a key the component declares (<see cref="DrydockDeclaredKeys"/>)
    /// stays, a retired data field is taken out and counted, and any other key throws. The row is copied before a key is
    /// taken out, because the caller still holds it, and a row with nothing to take out comes back as itself, so a row
    /// already held is read again without counting twice.
    /// </summary>
    /// <exception cref="FormatException">The row carries a key the component does not declare and nothing retires.</exception>
    internal MappingDataNode HoldToDeclared(Type componentType, MappingDataNode row)
    {
        var declared = DrydockDeclaredKeys.Of(componentType);
        MappingDataNode? kept = null;

        foreach (var (key, _) in row)
        {
            if (declared.Contains(key))
                continue;

            var name = _factory.GetComponentName(componentType);
            if (FindRetired(DrydockRetiredKind.DataField, $"{name}.{key}") is not { } retired)
                throw new FormatException($"Drydock codec: a {name} row carries '{key}', which {componentType.Name} does not declare and no retirement names.");

            kept ??= row.Copy();
            kept.Remove(key);
            CountRetired(retired);
        }

        return kept ?? row;
    }
}
