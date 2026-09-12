using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._NF.PublicTransit;
using Content.Server.Nuke;
using Content.Server.Spawners.Components;
using Content.Shared.Buckle;
using Content.Shared.Explosion.Components;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Nuke;
using Content.Shared.Singularity.Components;
using Robust.Shared.Map;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// What an impound does that an ordinary store will not: clear the two gates that exist to refuse a
/// hull nobody can safely put away, so that a hull nobody is coming back for can be put away anyway.
///
/// <para>An ordinary store refuses and the player tries again. An impound has nowhere to refuse to,
/// because the alternative to taking the hull is leaving it in a round that is ending. So the gates
/// that protect a store from doing damage become instructions to do the damage deliberately, and
/// only those two: a document that will not write or will not read back still fails, because those
/// are the failures an impound cannot paper over.</para>
///
/// <para>That damage is not undone when one of those two failures follows it. A hazard is deleted
/// and an occupant is moved at the gates, before serialize and validate can run, because a mind
/// must never reach the document and a countdown must never resume from one; a refusal after them
/// hands the hull back to its station intact but emptied, with its crew at a bus stop. Both are
/// survivable and neither is a duplicate, which is the trade the gates exist to make.</para>
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private SharedBuckleSystem _buckle = default!;
    [Dependency] private PublicTransitSystem _transit = default!;

    /// <summary>
    /// Takes a live hull into the impound lot: the ordinary store pipeline with its two refusal
    /// gates turned into instructions, landing the row on
    /// <see cref="DrydockShipState.Impounded"/> with no berth.
    ///
    /// <para>The hull is always saved first. There is no impounded-while-flying, which is the one
    /// thing the freeze it replaces could never say: a row that reads impounded while a grid still
    /// carries the ship is the duplicate every state transition here exists to prevent.</para>
    /// </summary>
    /// <param name="terms">
    /// The fee share, the reason, whether the owner may act on it, and who is taking it. The fee is
    /// taken against the appraisal measured during this very store, so the credits owed can never
    /// exceed what the hull is worth.
    /// </param>
    /// <param name="inline">
    /// Run the pipeline unsliced on this caller's path. The round-end sweep sets it; the admin
    /// ripcord, taken mid-round with players to protect, leaves it off.
    /// </param>
    public Task<(DrydockStoreResult Result, Guid? ShipId)> TryImpoundShip(
        EntityUid gridUid,
        Guid ownerUserId,
        int? roundId,
        DrydockImpound terms,
        EntityUid? stationUid = null,
        DrydockProgressCallback? onProgress = null,
        bool inline = false)
    {
        return TryStoreShip(gridUid, ownerUserId, roundId, berthId: null, stationUid, onProgress, terms, inline);
    }

    /// <summary>
    /// Clears what <see cref="HasHazardAboard"/> refuses for, so the gate that follows has nothing
    /// left to find. Each of the three gets the treatment its reason for blocking calls for, and
    /// those are not the same treatment.
    ///
    /// <para>An armed nuke and a singularity ARE the hazard, so they go.
    /// <c>ActiveTimerTriggerComponent</c> is not a hazard component at all: the trigger system adds
    /// it to anything with a running countdown, so deleting every carrier would take innocuous
    /// hardware with it. The narrow reason a countdown blocks is that it is an ordinary data field
    /// which would resume on thaw, so removing the component cancels the countdown and keeps the
    /// object.</para>
    ///
    /// <para>Deleted rather than queued, because the gate this exists to satisfy runs in the same
    /// tick and a queued deletion is still there when it looks. Collected before acting, because
    /// deleting inside an <c>AllEntityQuery</c> walk mutates what is being walked.</para>
    /// </summary>
    private void PurgeHazardsAboard(EntityUid gridUid)
    {
        var doomed = new List<EntityUid>();
        var disarm = new List<EntityUid>();

        var nukes = AllEntityQuery<NukeComponent, TransformComponent>();
        while (nukes.MoveNext(out var uid, out var nuke, out var xform))
        {
            if (xform.GridUid == gridUid && nuke.Status == NukeStatus.ARMED)
                doomed.Add(uid);
        }

        var timers = AllEntityQuery<ActiveTimerTriggerComponent, TransformComponent>();
        while (timers.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                disarm.Add(uid);
        }

        var singularities = AllEntityQuery<SingularityComponent, TransformComponent>();
        while (singularities.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                doomed.Add(uid);
        }

        if (doomed.Count == 0 && disarm.Count == 0)
            return;

        foreach (var uid in disarm)
            RemComp<ActiveTimerTriggerComponent>(uid);

        foreach (var uid in doomed)
            Del(uid);

        Log.Info($"Drydock: impound cleared {doomed.Count} hazard(s) and {disarm.Count} countdown(s) from {ToPrettyString(gridUid)}.");
    }

    /// <summary>
    /// Moves every mind off the hull, so the gate that follows has nobody left to refuse for. A mind
    /// must never reach the document, and an impound cannot answer "somebody is aboard" with "then
    /// not today", so it answers it by emptying the ship.
    /// </summary>
    /// <returns>How many were moved, for the log line and the audit reason.</returns>
    private int EvictOrganicsAboard(DrydockStoreContext ctx)
    {
        var gridUid = ctx.GridUid;

        var aboard = new List<EntityUid>();
        CollectMindsAboard(gridUid, aboard);

        if (aboard.Count == 0)
            return 0;

        var drop = FindImpoundDropOff(ctx);

        foreach (var uid in aboard)
        {
            // Both of these keep an occupant parented to the hull, so a bare move leaves them
            // aboard and the gate refuses a hull that has already been emptied on paper. Forced,
            // because every ordinary reason to say no to a removal is outranked here.
            _buckle.TryUnbuckle(uid, uid, popup: false);
            _containers.TryRemoveFromContainer(uid, force: true);

            // Leaving the paused staging map, when this runs after the freeze, is what unpauses
            // them: the engine re-derives an entity's pause state from the map it lands on.
            _xform.SetCoordinates(uid, drop);
        }

        Log.Info($"Drydock: impound moved {aboard.Count} occupant(s) off {ToPrettyString(gridUid)}.");
        return aboard.Count;
    }

    /// <summary>
    /// Mirrors the predicate in <c>ShipyardSystem.FoundOrganics</c>, which is the gate this exists to
    /// satisfy: ghosts do not count, and a mind counts when its player may still come back to it,
    /// meaning a live session or a character that is not physically dead. Anything that method finds
    /// and this one misses refuses an impound at a gate with no refusal left, so the two walks have
    /// to stay the same walk. That includes not recursing into a match, which is why the match arm
    /// continues rather than descending.
    /// </summary>
    private void CollectMindsAboard(EntityUid uid, List<EntityUid> into)
    {
        var children = Transform(uid).ChildEnumerator;

        while (children.MoveNext(out var child))
        {
            if (HasComp<GhostComponent>(child))
                continue;

            if (_mind.TryGetMind(child, out _, out var mindComp)
                && (mindComp.Session != null || !_mind.IsCharacterDeadPhysically(mindComp)))
            {
                into.Add(child);
                continue;
            }

            CollectMindsAboard(child, into);
        }
    }

    /// <summary>
    /// Where an evicted occupant lands: a spawn point on the map the hull came from, and on that
    /// map only. Bus service outranks distance, because somewhere close with no way to leave is
    /// worse than somewhere further along a route: the whole point of putting a player down rather
    /// than deleting them is that they can carry on playing.
    ///
    /// <para>The home map is read off the context, never the grid, because after the freeze the grid
    /// is on a private staging map and a query keyed on its transform would either find nothing or,
    /// unfiltered, find spawn points on every map at once: another sector, or a hull mid-store on a
    /// paused map of its own, where a body put down past that store's last organics gate is written
    /// into its document. A grid that is itself one tick from its own freeze is skipped for the same
    /// reason.</para>
    ///
    /// <para>Observer spawns are skipped because they are ghost markers, and a body put on one is
    /// standing in whatever the mapper thought a ghost could occupy.</para>
    ///
    /// <para>The last rung is not a refusal. An impound never refuses, so a sector that offers no
    /// spawn point at all still takes the hull, and the occupants are left on the home map at the
    /// hull's own position: whatever they are wearing, wherever they were, which is survivable and
    /// reversible. Being written into the document is neither.</para>
    /// </summary>
    private EntityCoordinates FindImpoundDropOff(DrydockStoreContext ctx)
    {
        var gridUid = ctx.GridUid;

        EntityCoordinates? best = null;
        var bestRank = (Unserved: 0, Distance: 0f);

        if (ctx.HomeMap is { } home)
        {
            var spawns = AllEntityQuery<SpawnPointComponent, TransformComponent>();
            while (spawns.MoveNext(out var uid, out var spawn, out var xform))
            {
                if (spawn.SpawnType == SpawnPointType.Observer)
                    continue;

                if (xform.MapUid != home)
                    continue;

                if (xform.GridUid is not { } grid || grid == gridUid)
                    continue;

                if (HasComp<DrydockInProgressComponent>(grid))
                    continue;

                var rank = (
                    Unserved: _transit.StationList.Contains(grid) ? 0 : 1,
                    Distance: (_xform.GetWorldPosition(uid) - ctx.HomePosition).Length());

                if (best != null && rank.CompareTo(bestRank) >= 0)
                    continue;

                best = xform.Coordinates;
                bestRank = rank;
            }
        }

        if (best is { } found)
            return found;

        Log.Error($"Drydock: impound of {ToPrettyString(gridUid)} found no spawn point on its home map; its occupants stay on that map where the hull was.");

        return ctx.HomeMap is { } map
            ? new EntityCoordinates(map, ctx.HomePosition)
            : EntityCoordinates.Invalid;
    }
}
