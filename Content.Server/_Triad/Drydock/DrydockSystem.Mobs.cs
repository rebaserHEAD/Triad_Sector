using System.Collections.Generic;
using System.Linq;
using Content.Server._Triad.Drydock.Loader;
using Content.Shared.Mobs.Components;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Creatures and bodies a store leaves out. The store keeps only map-savable entities, dropping the rest with
/// everything under them (<see cref="DrydockImageSystem.Walk"/>), and almost every prototype carrying
/// <see cref="MobStateComponent"/> composes <c>save: false</c>. The organics gate counts only a mind with a session or
/// a living owner (<c>ShipyardSystem.Consoles.cs:787-820</c>), so a pet, an animal, a bot, a mindless body or the body
/// of a dead player who disconnected passes it, and without the eviction the despawn deletes it with what it wears,
/// holds and carries.
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private DrydockImageSystem _image = default!;

    /// <summary>
    /// Moves every creature and body the store would leave out to the impound drop-off, with its subtree attached,
    /// and returns how many moved. Nothing moves when there is no drop-off to move to; the store's backstop then
    /// refuses for them.
    /// </summary>
    private int EvictMobsLeftOut(DrydockStoreContext ctx)
    {
        var leftOut = MobsLeftOut(ctx.GridUid, null);
        if (leftOut.Count == 0)
            return 0;

        var drop = FindImpoundDropOff(ctx, null);
        if (!drop.IsValid(EntityManager))
            return 0;

        foreach (var uid in leftOut)
        {
            // Unchecked, because a body cannot pass the checks a user's own unbuckle makes
            // (SharedBuckleSystem.Buckle.cs:430-458), and a buckled occupant stays parented to its strap.
            _buckle.Unbuckle(uid, null);
            _containers.TryRemoveFromContainer(uid, force: true);
            _xform.SetCoordinates(uid, drop);
        }

        Log.Info($"Drydock: moved {leftOut.Count} creature(s) or bod(y/ies) off {ToPrettyString(ctx.GridUid)}, which the store would have left out: "
                 + string.Join(", ", leftOut.Select(uid => MetaData(uid).EntityPrototype?.ID ?? "(no prototype)")));
        return leftOut.Count;
    }

    /// <summary>
    /// Every creature or body on the hull that the store leaves out, outermost first: one under another in the
    /// result is not listed, because moving the outer one takes it along. <paramref name="ids"/> is the store's
    /// id set; null walks for one, and only when the hull carries a creature at all.
    /// </summary>
    private List<EntityUid> MobsLeftOut(EntityUid gridUid, IReadOnlyDictionary<EntityUid, long>? ids)
    {
        var mobs = GetEntityQuery<MobStateComponent>();
        var carriers = new List<EntityUid>();
        foreach (var uid in _fidelity.GridTreeList(gridUid))
        {
            if (mobs.HasComp(uid))
                carriers.Add(uid);
        }

        if (carriers.Count == 0)
            return carriers;

        ids ??= _image.Walk(gridUid).Ids;
        var leftOut = carriers.Where(uid => !ids.ContainsKey(uid)).ToHashSet();

        return leftOut.Where(uid => !HasAncestorIn(uid, gridUid, leftOut)).ToList();
    }

    private bool HasAncestorIn(EntityUid uid, EntityUid gridUid, HashSet<EntityUid> set)
    {
        var parent = Transform(uid).ParentUid;
        while (parent.IsValid() && parent != gridUid)
        {
            if (set.Contains(parent))
                return true;

            parent = Transform(parent).ParentUid;
        }

        return false;
    }
}
