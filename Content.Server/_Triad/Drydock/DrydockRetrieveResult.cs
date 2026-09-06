namespace Content.Server._Triad.Drydock;

/// <summary>
/// The outcome of a retrieve attempt. A refusal names its reason so the console can tell the
/// player what is actually the matter, instead of the one sentence every refusal used to share.
/// Every value other than <see cref="Success"/> leaves the ship exactly where it was: stored,
/// berthed, and retrievable once the reason clears.
/// </summary>
public enum DrydockRetrieveResult : byte
{
    /// <summary>Loaded, revived, and docked at the requesting station.</summary>
    Success,

    /// <summary>The drydock is off. Read-only mode still allows retrieve on purpose.</summary>
    Disabled,

    /// <summary>The requesting console is not on a station with a grid to dock at.</summary>
    NoStation,

    /// <summary>
    /// The shipyard's staging map could not be brought up. The retrieve asks the shipyard to
    /// build it the way a purchase does, so this is the map failing to come back at all, not the
    /// fresh-round case where nobody has bought a ship yet.
    /// </summary>
    NoStagingMap,

    /// <summary>No record carries this id, or its current revision is gone.</summary>
    NotFound,

    /// <summary>The record belongs to another account. The console refuses this earlier and audits it; here it is the race with a transfer.</summary>
    NotOwned,

    /// <summary>An admin has flagged the ship. Nothing moves it until the investigation closes.</summary>
    Investigating,

    /// <summary>The record is checked out: a live grid somewhere carries this ship.</summary>
    AlreadyOut,

    /// <summary>An admin has frozen the ship pending a decision.</summary>
    Held,

    /// <summary>The ship is offered to another captain and waits on their answer.</summary>
    InEscrow,

    /// <summary>The ship was sold. Only an admin restore brings it back.</summary>
    Sold,

    /// <summary>
    /// The row read as stored, then the claim lost. Another retrieve of the same ship landed in
    /// between, which is exactly the race the claim exists for.
    /// </summary>
    NotStored,

    /// <summary>Every kept revision failed to decompress, verify, or load. An admin can look at the timeline.</summary>
    NoReadableRevision,

    /// <summary>The station's grid died while the ship was being loaded. The claim was released.</summary>
    StationLost,
}

/// <summary>
/// What a retrieve hands back: the reason, and on success the docked grid.
/// </summary>
public readonly record struct DrydockRetrieve(DrydockRetrieveResult Result, EntityUid? Grid)
{
    public bool Succeeded => Result == DrydockRetrieveResult.Success && Grid != null;

    public static DrydockRetrieve Refused(DrydockRetrieveResult result) => new(result, null);
}
