// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Mono.SpaceArtillery;
using Content.Shared.Projectiles;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Stops ship-weapon projectiles on described-but-unmaterialized debris. A dormant rock has
///     no entity and no physics, so without this a shot flies straight through a rock the radar
///     is painting; the fix is the X4 lesson, resolved in data space: sweep each projectile's
///     per-tick segment against the record tile sets, kill the shot at the intersection, and
///     let materialization be a consequence of the confirmed hit rather than a prerequisite for
///     the block.
///
///     Opt-in by <see cref="ShipWeaponProjectileComponent"/>, which every Mono ship-artillery
///     round already carries: small-arms fire never crosses kilometres of belt, and the marker
///     costs the query loop nothing for entities that lack it. Hitscan ship lasers resolve in
///     one frame with no entity to sweep and are a separate, currently unhandled integration
///     point.
/// </summary>
public sealed class SensedProjectileBlockSystem : EntitySystem
{
    [Dependency] private readonly CellDescribeSystem _describe = default!;
    [Dependency] private readonly DebrisMaterializeQueueSystem _queue = default!;
    [Dependency] private readonly TransformSystem _xformSys = default!;

    /// <summary>Last swept position per live ship projectile, pruned as they die.</summary>
    private readonly Dictionary<EntityUid, Vector2> _lastPos = new();

    private readonly HashSet<EntityUid> _seen = new();
    private readonly List<EntityUid> _stale = new();

    public override void Update(float frameTime)
    {
        _seen.Clear();

        var query = EntityQueryEnumerator<ShipWeaponProjectileComponent, ProjectileComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var projectile, out var xform))
        {
            _seen.Add(uid);

            if (xform.MapUid is not { } map)
                continue;

            var pos = _xformSys.GetWorldPosition(xform);
            if (!_lastPos.TryGetValue(uid, out var last))
            {
                // First sighting: no segment yet, so the spawn tick's muzzle-to-here gap goes
                // unswept. That gap is at most one tick of travel from a muzzle that is almost
                // always in loaded space; sweeping it would need a muzzle position this system
                // never sees. From the next tick on the segment chain is continuous.
                _lastPos[uid] = pos;
                continue;
            }

            _lastPos[uid] = pos;

            if (projectile.ProjectileSpent || (pos - last).LengthSquared() < 0.0001f)
                continue;

            if (!_describe.TryQuerySegment(map, last, pos, out var record, out var hitPoint))
                continue;

            Block(uid, projectile, map, record!, hitPoint);
        }

        // Prune entries for projectiles that died since last tick, or the dictionary grows for
        // the round.
        _stale.Clear();
        foreach (var uid in _lastPos.Keys)
        {
            if (!_seen.Contains(uid))
                _stale.Add(uid);
        }

        foreach (var uid in _stale)
        {
            _lastPos.Remove(uid);
        }
    }

    /// <summary>
    ///     The same exit a physical impact takes, minus the damage: spend the projectile, show
    ///     the impact effect at the data-space hit point so the shot visibly stops rather than
    ///     vanishing, delete it, and expedite the rock it hit.
    /// </summary>
    private void Block(EntityUid uid, ProjectileComponent projectile, EntityUid map, DebrisRecord record,
        Vector2 hitPoint)
    {
        projectile.ProjectileSpent = true;
        Dirty(uid, projectile);

        var coordinates = new EntityCoordinates(map, hitPoint);
        if (projectile.ImpactEffect is not null)
        {
            RaiseNetworkEvent(new ImpactEffectEvent(projectile.ImpactEffect, GetNetCoordinates(coordinates)),
                Filter.Pvs(coordinates, entityMan: EntityManager));
        }

        QueueDel(uid);
        _queue.ExpediteHit(record);
    }
}
