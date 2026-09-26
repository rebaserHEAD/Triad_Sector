using System.Collections.Generic;
using System.Linq;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// One stored entity: its id in the image, its prototype, the life stage it is loaded into, and its rows. Rows are JSON
/// text keyed by component name, plus the appearance and manifest rows, so an image is exactly what a database would
/// hold. Pause is not kept: a load takes it from the map it loads onto (<see cref="DrydockLoadSession"/>).
/// </summary>
public sealed record DrydockImageEntity(
    long Id,
    string? Prototype,
    bool MapInitialized,
    IReadOnlyDictionary<string, string> Rows);

/// <summary>
/// A ship as rows: every stored entity from the grid down, and the tile table. <see cref="Entities"/> is in load order,
/// parents before children, which the store's walk gives. The load cannot start on part of one, because the engine
/// allocates every entity before any row is read.
/// </summary>
/// <param name="GridId">The grid entity's id in the image.</param>
/// <param name="Tiles">The tile table (<see cref="Codec.DrydockTileTable"/>) as JSON text.</param>
/// <param name="Unsaved">Entities the walk left out, with everything under them, because their prototype is not savable.</param>
/// <param name="Bytes">The JSON text held, rows and tiles, in UTF-8 bytes.</param>
public sealed record DrydockImage(
    long GridId,
    IReadOnlyList<DrydockImageEntity> Entities,
    string Tiles,
    int Unsaved,
    int Bytes)
{
    /// <summary>What the store counted and did not write. <see cref="DrydockLeftOut.None"/> unless the store set it.</summary>
    public DrydockLeftOut LeftOut { get; init; } = DrydockLeftOut.None;

    /// <summary>
    /// Entities other than the grid stored below MapInitialized, which the load puts back at that stage
    /// (<see cref="DrydockLoadSession"/> writes each record's flag as the skeleton's <c>mapInit</c>). The grid is left
    /// out because the engine never map-initialises a grid it creates at runtime (SharedMapSystem.Grid.cs:64-65), so
    /// every hull begun in the round stores one. A hull from a map file holds none.
    /// </summary>
    public IEnumerable<DrydockImageEntity> BelowMapInit => Entities.Where(e => !e.MapInitialized && e.Id != GridId);
}

/// <summary>
/// What a store counted and did not write, kept with its image. A value the image was asked to hold and could not is not
/// here, because a store with one is refused (<see cref="DrydockImageStoreResult.Whole"/>).
/// </summary>
/// <param name="UnsavedByPrototype">The entities the walk stopped at because their prototype is not savable, by prototype.</param>
/// <param name="DroppedByPrototype">Everything under those, left out with them, by prototype.</param>
/// <param name="Stripped">Components the manifest strips (a round, a crew member, a live link), by name.</param>
/// <param name="AppearanceSkipped">Appearance values with no serializer, by type name.</param>
public sealed record DrydockLeftOut(
    IReadOnlyDictionary<string, int> UnsavedByPrototype,
    IReadOnlyDictionary<string, int> DroppedByPrototype,
    IReadOnlyDictionary<string, int> Stripped,
    IReadOnlyDictionary<string, int> AppearanceSkipped)
{
    private static readonly IReadOnlyDictionary<string, int> Empty = new Dictionary<string, int>();

    /// <summary>Nothing counted: what an image built by hand carries, and a fixture file that holds no counts reads as.</summary>
    public static readonly DrydockLeftOut None = new(Empty, Empty, Empty, Empty);

    /// <summary>The four counts by name, in declaration order, for a writer, a reader and a compare that treat them alike.</summary>
    public IEnumerable<(string Name, IReadOnlyDictionary<string, int> Counts)> ByName()
    {
        yield return (UnsavedByPrototypeKey, UnsavedByPrototype);
        yield return (DroppedByPrototypeKey, DroppedByPrototype);
        yield return (StrippedKey, Stripped);
        yield return (AppearanceSkippedKey, AppearanceSkipped);
    }

    public const string UnsavedByPrototypeKey = "unsavedByPrototype";
    public const string DroppedByPrototypeKey = "droppedByPrototype";
    public const string StrippedKey = "stripped";
    public const string AppearanceSkippedKey = "appearanceSkipped";

    /// <summary>The four names <see cref="ByName"/> gives, in its order.</summary>
    public static readonly IReadOnlyList<string> Keys = new[] { UnsavedByPrototypeKey, DroppedByPrototypeKey, StrippedKey, AppearanceSkippedKey };

    /// <summary>Built from counts read by name, as <see cref="ByName"/> gives them; a name absent reads as no counts.</summary>
    public static DrydockLeftOut FromNames(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> byName) => new(
        byName.GetValueOrDefault(UnsavedByPrototypeKey) ?? Empty,
        byName.GetValueOrDefault(DroppedByPrototypeKey) ?? Empty,
        byName.GetValueOrDefault(StrippedKey) ?? Empty,
        byName.GetValueOrDefault(AppearanceSkippedKey) ?? Empty);
}
