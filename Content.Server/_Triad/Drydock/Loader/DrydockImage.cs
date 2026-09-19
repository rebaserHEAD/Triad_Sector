using System.Collections.Generic;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// One stored entity: its stable id, its prototype, the life stage and pause state it is loaded into, and its rows.
/// Rows are JSON text keyed by component name, plus the appearance and manifest rows, so an image is exactly what a
/// database would hold.
/// </summary>
public sealed record DrydockImageEntity(
    long Id,
    string? Prototype,
    bool MapInitialized,
    bool Paused,
    IReadOnlyDictionary<string, string> Rows);

/// <summary>
/// A ship as rows: every stored entity from the grid down, and the tile table. <see cref="Entities"/> is in load order,
/// parents before children, which the store's walk gives. The load cannot start on part of one, because the engine
/// allocates every entity before any row is read.
/// </summary>
/// <param name="GridId">The stable id of the grid entity.</param>
/// <param name="Tiles">The tile table (<see cref="Codec.DrydockTileTable"/>) as JSON text.</param>
/// <param name="Unsaved">Entities the walk left out, with everything under them, because their prototype is not savable.</param>
/// <param name="Bytes">The JSON text held, rows and tiles.</param>
public sealed record DrydockImage(
    long GridId,
    IReadOnlyList<DrydockImageEntity> Entities,
    string Tiles,
    int Unsaved,
    int Bytes);
