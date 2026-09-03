namespace Content.Server._Triad.Drydock;

/// <summary>
/// The outcome of a store attempt. A refusal names its reason so the console can tell the player
/// what to fix, and so a refusal is visibly a refusal rather than a silent no-op. Every value other
/// than <see cref="Success"/> leaves the live grid exactly as usable as it was.
/// </summary>
public enum DrydockStoreResult : byte
{
    /// <summary>Serialized, filed, and the grid despawned.</summary>
    Success,

    /// <summary>The engine map serializer could not write the grid.</summary>
    SerializeFailed,

    /// <summary>A living, sapient occupant is aboard, by player session or live mind.</summary>
    OrganicsAboard,

    /// <summary>An armed nuke, an active countdown, or a singularity is aboard.</summary>
    HazardAboard,

    /// <summary>
    /// The round-trip check found the freshly written document disagreeing with the live grid. The
    /// store aborts before any revision is filed, so a serializer regression cannot half-commit a
    /// ship or quietly drop part of one.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// The drydock is off, or in read-only mode. Read-only exists so a build suspected of writing
    /// bad revisions can be stopped from writing any more without grounding the fleet.
    /// </summary>
    Disabled,

    /// <summary>
    /// The owner has no free berth. Checked before the first mutation so a full garage refuses
    /// cheaply, and again inside the filing transaction, where the unique index on the berth column
    /// makes the answer final. A store that loses that race reports this too: nothing was filed,
    /// and the fix is the same.
    /// </summary>
    NoBerth,

    /// <summary>
    /// Free berths exist and none accepts a hull of this class. Blocking on purpose: unlike drift,
    /// the player grew the hull and can upgrade or buy a berth at the same terminal.
    /// </summary>
    BerthTooSmall,

    /// <summary>A store of this grid is already in flight. The second request does nothing.</summary>
    InProgress,
}
