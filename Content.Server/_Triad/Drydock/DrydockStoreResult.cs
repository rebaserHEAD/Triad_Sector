namespace Content.Server._Triad.Drydock;

/// <summary>
/// The outcome of a store attempt. A refusal names its reason, so the console can say what to fix.
/// Every value other than <see cref="Success"/> leaves the live grid as usable as it was.
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
    /// The written document disagrees with the live grid. Aborts before any revision is filed, so a
    /// serializer regression cannot half-commit a ship.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// Off, or read-only. Read-only stops a suspect build writing more revisions without grounding
    /// the fleet.
    /// </summary>
    Disabled,

    /// <summary>
    /// The owner has no free berth. Checked before the first mutation and again inside the filing
    /// transaction, where the unique index on the berth column settles the race.
    /// </summary>
    NoBerth,

    /// <summary>Free berths exist, none fits this hull class. The player can upgrade or buy one here.</summary>
    BerthTooSmall,

    /// <summary>A store of this grid is already in flight. The second request does nothing.</summary>
    InProgress,

    /// <summary>The berth the player named already has a ship in it.</summary>
    BerthOccupied,
}
