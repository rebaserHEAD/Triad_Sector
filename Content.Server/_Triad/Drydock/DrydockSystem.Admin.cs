using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server.Database;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The admin half: the decisions the store cannot make because they need the live world. A row
/// says a ship is checked out; only the entity manager knows whether a grid still carries its id.
/// </summary>
public sealed partial class DrydockSystem
{
    /// <summary>
    /// Every hull id that has a live grid in the current round. A retrieved ship, a ship that
    /// failed to store, and a ship mid-store all count; a row that says checked out for any of
    /// them is telling the truth, and restoring it would mint a second copy.
    /// </summary>
    public HashSet<Guid> LiveShipIds()
    {
        var live = new HashSet<Guid>();
        var query = AllEntityQuery<DrydockIdentityComponent>();
        while (query.MoveNext(out var uid, out var identity))
        {
            if (identity.ShipId != Guid.Empty && !TerminatingOrDeleted(uid))
                live.Add(identity.ShipId);
        }

        return live;
    }

    public bool IsShipLive(Guid shipId)
    {
        var query = AllEntityQuery<DrydockIdentityComponent>();
        while (query.MoveNext(out var uid, out var identity))
        {
            if (identity.ShipId == shipId && !TerminatingOrDeleted(uid))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Admin restore, guarded: a ship whose grid is still in the world is not lost, and putting
    /// its row back to stored would let it be retrieved into a duplicate while the original
    /// flies. Everything else is the store's decision.
    /// </summary>
    public Task<DrydockBerthResult> TryAdminRestore(Guid shipId, int berthId, Guid? actorUserId, int? roundId, string reason)
    {
        if (IsShipLive(shipId))
        {
            Log.Warning($"Drydock: admin restore of {shipId} refused, a live grid still carries it.");
            return Task.FromResult(DrydockBerthResult.WrongState);
        }

        return _store.TryRestoreShip(shipId, berthId, actorUserId, roundId, reason);
    }
}
