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
/// <param name="Bytes">The JSON text held, rows and tiles.</param>
public sealed record DrydockImage(
    long GridId,
    IReadOnlyList<DrydockImageEntity> Entities,
    string Tiles,
    int Unsaved,
    int Bytes)
{
    /// <summary>
    /// Entities other than the grid stored below MapInitialized, which the load puts back at that stage
    /// (<see cref="DrydockLoadSession"/> writes each record's flag as the skeleton's <c>mapInit</c>). The grid is left
    /// out because the engine never map-initialises a grid it creates at runtime (SharedMapSystem.Grid.cs:64-65), so
    /// every hull begun in the round stores one. A hull from a map file holds none.
    /// </summary>
    public IEnumerable<DrydockImageEntity> BelowMapInit => Entities.Where(e => !e.MapInitialized && e.Id != GridId);
}
