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
}
