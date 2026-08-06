using Content.Server.Physics.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics;
using System.Numerics;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Physics.Collision.Shapes;

namespace Content.Server.Physics.Controllers;

/// <summary>
/// A component which makes its entity periodically chaotic jumps arounds
/// </summary>
public sealed class ChaoticJumpSystem : VirtualController
{
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly RayCastSystem _rayCast = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ChaoticJumpComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<ChaoticJumpComponent> chaotic, ref MapInitEvent args)
    {
        //So the entity doesn't teleport instantly. For tesla, for example, it's important for it to eat tesla's generator.
        chaotic.Comp.NextJumpTime = _gameTiming.CurTime + TimeSpan.FromSeconds(_random.NextFloat(chaotic.Comp.JumpMinInterval, chaotic.Comp.JumpMaxInterval));
    }

    public override void UpdateBeforeSolve(bool prediction, float frameTime)
    {
        base.UpdateBeforeSolve(prediction, frameTime);

        var query = EntityQueryEnumerator<ChaoticJumpComponent>();
        while (query.MoveNext(out var uid, out var chaotic))
        {
            //Jump
            if (chaotic.NextJumpTime <= _gameTiming.CurTime)
            {
                Jump(uid, chaotic);
                chaotic.NextJumpTime += TimeSpan.FromSeconds(_random.NextFloat(chaotic.JumpMinInterval, chaotic.JumpMaxInterval));
            }
        }
    }

    private void Jump(EntityUid uid, ChaoticJumpComponent component)
    {
        var xform = Transform(uid);
        var origin = _physics.GetPhysicsTransform(uid);
        var startPos = origin.Position;

        var direction = _random.NextAngle();
        var dir = direction.ToVec();
        var range = _random.NextFloat(component.RangeMin, component.RangeMax);
        var translation = dir * range;

        // Triad: replaced the zero-width raycast + 1-tile offset below with two probes along the jump path.
        // The teleport ignores physics entirely, so whatever these probes miss, the entity escapes through.
        // Old logic preserved:
        // var ray = new CollisionRay(startPos, direction.ToVec(), component.CollisionMask);
        // var rayCastResults = _physics.IntersectRay(xform.MapID, ray, range, uid, returnOnFirstHit: false).FirstOrNull();
        // if (rayCastResults != null)
        // {
        //     targetPos = rayCastResults.Value.HitPos;
        //     targetPos = new Vector2(targetPos.X - (float) Math.Cos(direction), targetPos.Y - (float) Math.Sin(direction));
        // }
        // else
        // {
        //     targetPos = new Vector2(startPos.X + range * (float) Math.Cos(direction), startPos.Y + range * (float) Math.Sin(direction));
        // }
        var filter = new QueryFilter
        {
            MaskBits = component.CollisionMask,
            IsIgnored = entity => entity == uid,
        };

        // Fraction of the jump the entity is allowed to travel. Each probe can only tighten this.
        var fraction = 1f;
        var blocked = false;

        // Probe 1, zero-width ray along the centre line. A point start cannot overlap, so this stays
        // valid in gaps narrower than the swept footprint, where probe 2 goes blind. This is what holds
        // a cage whose interior is tighter than the body.
        var rayResult = _rayCast.CastRayClosest(xform.MapID, startPos, translation, filter);
        if (rayResult.Hit)
        {
            fraction = MathF.Min(fraction, rayResult.Results[0].Fraction);
            blocked = true;
        }

        // Probe 2, the body's own swept footprint. Catches sub-tile gaps (containment-field corner slots)
        // that the centre line threads but the body could not fit through. Note that a shape cast which
        // starts already overlapping reports NO hit rather than fraction 0, so this probe silently goes
        // blind whenever the entity is in contact. That is exactly why probe 1 has to carry the floor.
        var shape = new PhysShapeCircle(component.SweepRadius);
        var shapeResult = _rayCast.CastShape(xform.MapID, shape, origin, translation, filter, RayCastSystem.RayCastClosestCallback);
        if (shapeResult.Hit)
        {
            fraction = MathF.Min(fraction, shapeResult.Results[0].Fraction);
            blocked = true;
        }

        Vector2 targetPos;
        if (blocked)
        {
            // Land just short of the first solid contact along the path.
            var stopDistance = MathF.Max(0f, range * fraction - component.SweepSkin);
            targetPos = startPos + dir * stopDistance;
        }
        else
        {
            targetPos = startPos + translation;
        }

        Spawn(component.Effect, xform.Coordinates);

        _transform.SetWorldPosition(uid, targetPos);
    }
}
