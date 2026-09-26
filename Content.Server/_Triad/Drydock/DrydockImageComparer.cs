using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Content.Server._Triad.Drydock.Loader;

namespace Content.Server._Triad.Drydock;

/// <summary>An image read back from the store differs from the one filed. The wiring reports it as a failed validation.</summary>
public sealed class DrydockImageMismatchException : Exception
{
    /// <summary>What differs, first difference first, capped at <see cref="DrydockImageComparer.MaxReported"/>.</summary>
    public IReadOnlyList<string> Differences { get; }

    public DrydockImageMismatchException(IReadOnlyList<string> differences)
        : base($"Drydock: the stored image differs from the one filed: {string.Join("; ", differences)}")
    {
        Differences = differences;
    }
}

/// <summary>
/// Whether two images hold the same ship: the grid, the entities in the same order, per entity the id, the prototype,
/// whether it is map-initialised, the set of row names and each row's value, the unsaved count, each of the left-out
/// counts (<see cref="DrydockImage.LeftOut"/>), and the tile table.
/// Map-initialisation is per entity because a grid made at runtime (a floor tile placed in space, a split) is started
/// but never map-initialised (<c>SharedMapSystem.Grid.cs:63-65</c>). Values are compared as JSON with
/// <see cref="JsonElement.DeepEquals"/>: a mapping's keys in any order, a list in order, a string ordinally. That is
/// the equality a <c>jsonb</c> round trip keeps, since <c>jsonb</c> reorders keys and a mapping's key order carries no
/// content (<c>DrydockNodeJson.cs:26-29</c>).
///
/// <para>Not compared: <see cref="DrydockImage.Bytes"/>, which measures the text as written and a <c>jsonb</c> read
/// spells differently.</para>
/// </summary>
public static class DrydockImageComparer
{
    public const int MaxReported = 20;

    /// <summary>Throws <see cref="DrydockImageMismatchException"/> naming what differs, or returns when nothing does.</summary>
    public static void AssertSame(DrydockImage expected, DrydockImage actual)
    {
        var differences = Differences(expected, actual);
        if (differences.Count > 0)
            throw new DrydockImageMismatchException(differences);
    }

    /// <summary>What differs between the two images, first difference first, at most <see cref="MaxReported"/> of them.</summary>
    public static List<string> Differences(DrydockImage expected, DrydockImage actual)
    {
        var differences = new List<string>();

        void Add(string difference)
        {
            if (differences.Count < MaxReported)
                differences.Add(difference);
        }

        if (expected.GridId != actual.GridId)
            Add($"grid {expected.GridId} became {actual.GridId}");

        if (expected.Unsaved != actual.Unsaved)
            Add($"unsaved count {expected.Unsaved} became {actual.Unsaved}");

        foreach (var (want, got) in expected.LeftOut.ByName().Zip(actual.LeftOut.ByName()))
        {
            if (!SameCounts(want.Counts, got.Counts))
                Add($"left-out {want.Name} counts differ");
        }

        if (!JsonEqual(expected.Tiles, actual.Tiles, out var tileFault))
            Add($"tile table differs{tileFault}");

        var actualById = new Dictionary<long, DrydockImageEntity>();
        foreach (var entity in actual.Entities)
        {
            if (!actualById.TryAdd(entity.Id, entity))
                Add($"entity {entity.Id} read back twice");
        }

        var expectedIds = new HashSet<long>();
        foreach (var entity in expected.Entities)
        {
            if (!expectedIds.Add(entity.Id))
            {
                Add($"entity {entity.Id} filed twice");
                continue;
            }

            if (!actualById.TryGetValue(entity.Id, out var back))
            {
                Add($"entity {entity.Id} ({entity.Prototype ?? "no prototype"}) is missing");
                continue;
            }

            if (entity.Prototype != back.Prototype)
                Add($"entity {entity.Id}: prototype {entity.Prototype ?? "none"} became {back.Prototype ?? "none"}");

            if (entity.MapInitialized != back.MapInitialized)
                Add($"entity {entity.Id}: map-initialised {entity.MapInitialized} became {back.MapInitialized}");

            foreach (var name in entity.Rows.Keys.Except(back.Rows.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
                Add($"entity {entity.Id}: row {name} is missing");

            foreach (var name in back.Rows.Keys.Except(entity.Rows.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
                Add($"entity {entity.Id}: row {name} appeared");

            foreach (var (name, value) in entity.Rows.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (back.Rows.TryGetValue(name, out var backValue) && !JsonEqual(value, backValue, out var fault))
                    Add($"entity {entity.Id}: row {name} differs{fault}");
            }
        }

        foreach (var id in actualById.Keys.Where(id => !expectedIds.Contains(id)).Order())
            Add($"entity {id} appeared");

        // The load takes entities in list order, parents before children (DrydockImage.cs:18-20), so order is content.
        if (differences.Count == 0 && !expected.Entities.Select(e => e.Id).SequenceEqual(actual.Entities.Select(e => e.Id)))
            Add("the entities are the same but in another order");

        return differences;
    }

    private static bool SameCounts(IReadOnlyDictionary<string, int> expected, IReadOnlyDictionary<string, int> actual) =>
        expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var count) && count == pair.Value);

    private static bool JsonEqual(string expected, string actual, out string fault)
    {
        fault = "";
        if (string.Equals(expected, actual, StringComparison.Ordinal))
            return true;

        try
        {
            using var left = JsonDocument.Parse(expected);
            using var right = JsonDocument.Parse(actual);
            return JsonElement.DeepEquals(left.RootElement, right.RootElement);
        }
        catch (JsonException e)
        {
            fault = $" (not JSON: {e.Message})";
            return false;
        }
    }
}
