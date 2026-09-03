using Content.Server.Database;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The outcome of anything that touches a berth: assigning one at store, buying, selling, moving a
/// ship between them, transferring a ship into somebody else's. A refusal names what the player or
/// admin can do about it, which is the whole reason it is an enum rather than a bool.
/// </summary>
public enum DrydockBerthResult : byte
{
    Success,

    /// <summary>The owner has no free berth at all. Buying one is the fix.</summary>
    NoBerth,

    /// <summary>
    /// Free berths exist, and none of them accepts a hull of this class. Upgrading one, or buying a
    /// larger one, is the fix. Distinct from <see cref="NoBerth"/> because the message differs.
    /// </summary>
    BerthTooSmall,

    /// <summary>The berth already holds a hull. Move it first.</summary>
    BerthOccupied,

    /// <summary>The berth or ship is unknown, or does not belong to whoever is asking.</summary>
    NotFound,

    /// <summary>The ship is not in a state this operation accepts.</summary>
    WrongState,

    /// <summary>
    /// Lost a race the database arbitrated: two writers wanted the same berth in the same instant
    /// and the unique index picked one. Nothing was written. Safe to retry.
    /// </summary>
    Conflict,
}

/// <summary>What filing a revision produced: the outcome, and on success the revision number and the berth the ship now sits in.</summary>
public sealed record DrydockFileResult(DrydockBerthResult Outcome, int Revision, int? BerthId);

/// <summary>One berth and whatever hull is sitting in it, for the terminal and the admin panel.</summary>
public sealed record DrydockBerthSlot(DrydockBerth Berth, DrydockShip? Occupant);
