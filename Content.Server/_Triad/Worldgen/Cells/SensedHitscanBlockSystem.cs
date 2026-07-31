// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Mono.Gatherable;
using Content.Server._Mono.Radar;
using Content.Shared._Triad.Worldgen;
using Content.Shared.Weapons.Hitscan.Events;
using Content.Shared.Weapons.Hitscan.Systems;
using Robust.Shared.Map;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Stops ship-grade hitscan beams on described-but-unmaterialized debris, closing the gap
///     <see cref="SensedProjectileBlockSystem"/> leaves: a beam resolves in one frame with no
///     entity to sweep per tick, so it is blocked at the raycast result instead.
///
///     The mechanism is interception, not duplication. The basic raycast system publishes its
///     result as a mutable event before damage, visuals, radar and gathering consume it; this
///     system runs first for <see cref="ShipWeaponHitscanComponent"/> carriers, asks the records
///     for a data-space hit along the beam, and on a closer hit truncates the distance and drops
///     the hit entity. Every consumer downstream then does the right thing on its own: damage
///     finds no target, the beam and impact flash draw at the rock face, and the rock's build is
///     expedited exactly like a projectile hit.
///
///     Server-only, like everything that reads records. A predicting client may briefly draw an
///     overshooting beam; ship artillery fire is server-driven, so in practice it does not.
/// </summary>
public sealed class SensedHitscanBlockSystem : EntitySystem
{
    [Dependency] private readonly CellDescribeSystem _describe = default!;
    [Dependency] private readonly DebrisMaterializeQueueSystem _queue = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShipWeaponHitscanComponent, HitscanRaycastFiredEvent>(OnRaycastFired, before:
        [
            typeof(HitscanBasicDamageSystem),
            typeof(HitscanBasicEffectsSystem),
            typeof(HitscanBasicVisualsSystem),
            typeof(HitscanReflectSystem),
            typeof(HitscanStunSystem),
            typeof(HitscanSpawnEntitySystem),
            typeof(HitscanDiffractSystem),
            typeof(GatherableSystemHitscan),
            typeof(HitscanRadarSystem),
        ]);
    }

    private void OnRaycastFired(Entity<ShipWeaponHitscanComponent> ent, ref HitscanRaycastFiredEvent args)
    {
        if (args.Canceled)
            return;

        var from = _transform.ToMapCoordinates(args.FromCoordinates);
        if (!_map.TryGetMap(from.MapId, out var mapUid))
            return;

        var direction = args.ShotDirection;
        if (direction.LengthSquared() == 0f)
            return;

        direction = direction.Normalized();
        var to = from.Position + direction * args.DistanceTried;

        if (!_describe.TryQuerySegment(mapUid.Value, from.Position, to, out var record, out var hitPoint))
            return;

        var hitDist = (hitPoint - from.Position).Length();
        if (hitDist >= args.DistanceTried)
            return;

        args.HitEntity = null;
        args.DistanceTried = hitDist;

        _queue.ExpediteHit(record!);
    }
}
