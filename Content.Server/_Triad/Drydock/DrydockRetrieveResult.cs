namespace Content.Server._Triad.Drydock;

/// <summary>
/// The outcome of a retrieve attempt. A refusal names its reason, so the console can say what is
/// the matter. Every value other than <see cref="Success"/> leaves the ship stored, berthed and
/// retrievable once the reason clears.
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
    /// No longer produced. A retrieve used to borrow the shipyard's shared staging map and had to
    /// ask for it to be built first; it now loads onto a private paused map of its own, made by the
    /// loader as part of the load, so there is nothing left that can fail this way.
    ///
    /// <para>Kept because the console's refusal switch reads it and it has a live locale string
    /// behind it, and because renumbering this enum would silently re-map every value after it.</para>
    /// </summary>
    NoStagingMap,

    /// <summary>No record carries this id, or its current revision is gone.</summary>
    NotFound,

    /// <summary>Another account owns it. The console refuses earlier; here it is a race with a transfer.</summary>
    NotOwned,

    /// <summary>The record is checked out: a live grid somewhere carries this ship.</summary>
    AlreadyOut,

    /// <summary>The ship sits in the impound holding area, out of its owner's berths.</summary>
    Impounded,

    /// <summary>The ship is offered to another captain and waits on their answer.</summary>
    InEscrow,

    /// <summary>The ship was sold. Only an admin restore brings it back.</summary>
    Sold,

    /// <summary>The row read as stored, then the claim lost to another retrieve of the same ship.</summary>
    NotStored,

    /// <summary>Every kept revision failed to decompress, verify, or load. An admin can look at the timeline.</summary>
    NoReadableRevision,

    /// <summary>The station's grid died while the ship was being loaded. The claim was released.</summary>
    StationLost,

    /// <summary>
    /// The pipeline was cancelled before it could hand the ship over: a round restart, a shutdown,
    /// or the slice watchdog. Whatever had been staged was scrapped and the claim was released, so
    /// nothing is out and the ship is still stored and still retrievable.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The hull was written off: it could not have brought itself home, so nothing was filed for it
    /// to come back as. Appended rather than grouped with the other refusals for the reason
    /// <see cref="NoStagingMap"/> gives.
    /// </summary>
    Destroyed,

    /// <summary>Its owner gave the ship up rather than redeem it out of impound.</summary>
    Abandoned,
}

/// <summary>
/// What a retrieve hands back: the reason, and on success the docked grid.
/// </summary>
public readonly record struct DrydockRetrieve(DrydockRetrieveResult Result, EntityUid? Grid)
{
    public bool Succeeded => Result == DrydockRetrieveResult.Success && Grid != null;

    public static DrydockRetrieve Refused(DrydockRetrieveResult result) => new(result, null);
}
