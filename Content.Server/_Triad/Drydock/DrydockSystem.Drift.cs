using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The production inputs to <see cref="DrydockDrift.Detect"/>: the migration mappings this server
/// ships and the prototypes it has loaded. Kept apart from the detector so the detector stays a pure
/// function a test can hand anything.
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private IResourceManager _resources = default!;

    private DrydockMigrationTable? _migrationTable;

    /// <summary>
    /// The four entity migration files as the loader applies them, read once on first use. Holiday
    /// renames are deliberately absent: a holiday swap does not apply to a stored ship.
    /// Not thread-safe to build, so it is read only on the main thread.
    /// </summary>
    internal DrydockMigrationTable MigrationTable => _migrationTable ??= DrydockMigrationTable.Load(_resources);

    /// <summary>
    /// Classifies one stored revision's prototype ids against the mappings and the prototypes loaded
    /// now, with its two format versions against the windows a retrieve reads.
    /// </summary>
    /// <param name="drydockFormatVer">The revision's <c>drydock_format_ver</c> column.</param>
    internal DrydockDriftVerdict DetectDrift(SortedSet<string> ids, int engineFormatVer, int drydockFormatVer)
    {
        return DrydockDrift.Detect(
            ids,
            MigrationTable,
            id => _protoMan.HasIndex<EntityPrototype>(id),
            engineFormatVer,
            DrydockDrift.EngineWindow,
            drydockFormatVer,
            DrydockDrift.DrydockWindow);
    }
}
