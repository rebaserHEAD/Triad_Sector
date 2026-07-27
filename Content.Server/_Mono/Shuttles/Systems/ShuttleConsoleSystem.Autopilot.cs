using Content.Server._Mono.NPC.HTN;
using Content.Server._Mono.NPC.HTN.Operators;
using Content.Server._Triad.Shuttles;
using Content.Server.NPC.HTN;
using Content.Server.Physics.Controllers;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Popups;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.Shuttles;
using Content.Shared.Construction.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;

namespace Content.Server._Mono.Shuttles;

// Triad: autopilot no longer rides the NPC HTN stack. The console used to carry an HTN whose
// AutopilotShuttleCompound ran a single ShipMoveToOperator against blackboard keys, which meant
// autopilot shared the NPC sleep sweep (npc.player_pause_distance defaults to 32, so the brain
// slept whenever nobody stood at the console and only kept flying because sleep leaked the
// steerer), replanned at 2Hz while idle, and forced an "except consoles" carve-out on every NPC
// brain change. Autopilot now drives ShipSteeringSystem directly from ShuttleAutopilotComponent.
public sealed partial class ShuttleConsoleAutopilotSystem : EntitySystem
{
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShipSteeringSystem _steering = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShuttleConsoleComponent, ShuttleConsoleAutopilotPositionMessage>(OnAutopilotMessage);
        SubscribeLocalEvent<ShuttleConsoleComponent, SteeringDoneEvent>(OnSteeringDone);
        SubscribeLocalEvent<ShuttleAutopilotComponent, ComponentShutdown>(OnAutopilotShutdown); // Triad
    }

    private void OnAutopilotMessage(Entity<ShuttleConsoleComponent> ent, ref ShuttleConsoleAutopilotPositionMessage args)
    {
        // Triad: was a blackboard write into the console's HTN:
        // if (!TryComp<HTNComponent>(ent, out var htn))
        //     return;
        //
        // var blackboard = htn.Blackboard;
        // blackboard.SetValue(ent.Comp.AutopilotTargetKey, _transform.ToCoordinates(args.Coordinates));
        // blackboard.SetValue(ent.Comp.AutopilotRotationKey, args.Angle + MathF.PI);
        var autopilot = EnsureComp<ShuttleAutopilotComponent>(ent);
        autopilot.Target = _transform.ToCoordinates(args.Coordinates);
        autopilot.TargetAngle = args.Angle + MathF.PI;

        if (Engage(ent, autopilot) == null)
            RemCompDeferred<ShuttleAutopilotComponent>(ent);
    }

    // Triad: per-tick flight-order supervision, replacing the old operator's Update.
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ShuttleAutopilotComponent, ShuttleConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var autopilot, out var console, out var xform))
        {
            // Mirror the old operator's requirements: the console must stay anchored and powered.
            if (HasComp<AnchorableComponent>(uid) && !xform.Anchored
                || TryComp<ApcPowerReceiverComponent>(uid, out var receiver) && !_power.IsPowered(uid, receiver))
            {
                Finish(uid);
                continue;
            }

            // Yield the helm to a human pilot; the steerer itself counts as one active source.
            if (TryComp<PilotedShuttleComponent>(xform.GridUid, out var piloted) && piloted.ActiveSources > 1)
            {
                Finish(uid);
                continue;
            }

            // Re-engage every tick so steering follows the console across grid changes,
            // exactly as the old operator re-called Steer in its Update.
            var steerer = Engage(uid, autopilot);

            if (steerer == null || steerer.Status == ShipSteeringStatus.InRange)
                Finish(uid);
        }
    }

    /// <summary>
    /// Points the steering servo at the flight order. Knob values mirror the retired
    /// AutopilotMoveCompound YAML plus the old ShipMoveToOperator defaults.
    /// </summary>
    private ShipSteererComponent? Engage(EntityUid uid, ShuttleAutopilotComponent autopilot)
    {
        var comp = _steering.Steer(uid, autopilot.Target);

        if (comp == null)
            return null;

        comp.AlwaysFaceTarget = true;
        comp.AvoidCollisions = true;
        comp.AvoidProjectiles = false;
        comp.BrakeThreshold = 0.75f;
        comp.EvasionSectorCount = 24;
        comp.EvasionSectorDepth = 2;
        comp.FinishOnCollide = true;
        comp.InRangeMaxSpeed = 0.1f;
        comp.InRangeRotation = autopilot.TargetAngle;
        comp.LeadingEnabled = false;
        comp.MaxRotateRate = 0.01f;
        comp.Mode = ShipSteeringMode.GoToRange;
        comp.NoFinish = false;
        comp.Range = 40f;
        comp.RangeTolerance = null;
        comp.TargetRotation = 0f;

        return comp;
    }

    /// <summary>
    /// Ends the flight order. The old operator raised SteeringDoneEvent from its shutdown on
    /// every exit path (arrival, power loss, yielding to a pilot), so we keep that contract.
    /// </summary>
    private void Finish(EntityUid uid)
    {
        _steering.Stop(uid);
        RemCompDeferred<ShuttleAutopilotComponent>(uid);
        RaiseLocalEvent(uid, new SteeringDoneEvent(), false);
    }

    private void OnAutopilotShutdown(Entity<ShuttleAutopilotComponent> ent, ref ComponentShutdown args)
    {
        // Covers console destruction/deconstruction mid-flight; Stop is a no-op if already stopped.
        _steering.Stop(ent.Owner);
    }

    private void OnSteeringDone(Entity<ShuttleConsoleComponent> ent, ref SteeringDoneEvent args)
    {
        _audio.PlayPvs(ent.Comp.AutopilotDoneSound, ent);
        _popup.PopupEntity(Loc.GetString("shuttle-console-autopilot-popup-done"), ent, PopupType.Medium);
    }
}
