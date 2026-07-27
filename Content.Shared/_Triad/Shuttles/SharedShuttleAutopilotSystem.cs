using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Shuttles;

/// <summary>
/// Networks <see cref="ShuttleAutopilotComponent"/> state so the console UI can display the
/// active flight order. Manual state handling because the target is EntityCoordinates.
/// </summary>
public sealed class SharedShuttleAutopilotSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShuttleAutopilotComponent, ComponentGetState>(OnGetState);
        SubscribeLocalEvent<ShuttleAutopilotComponent, ComponentHandleState>(OnHandleState);
    }

    private void OnGetState(Entity<ShuttleAutopilotComponent> ent, ref ComponentGetState args)
    {
        args.State = new ShuttleAutopilotComponentState
        {
            Target = GetNetCoordinates(ent.Comp.Target),
            TargetAngle = ent.Comp.TargetAngle,
            MaxSpeed = ent.Comp.MaxSpeed,
        };
    }

    private void OnHandleState(Entity<ShuttleAutopilotComponent> ent, ref ComponentHandleState args)
    {
        if (args.Current is not ShuttleAutopilotComponentState state)
            return;

        ent.Comp.Target = GetCoordinates(state.Target);
        ent.Comp.TargetAngle = state.TargetAngle;
        ent.Comp.MaxSpeed = state.MaxSpeed;
    }

    [Serializable, NetSerializable]
    private sealed class ShuttleAutopilotComponentState : ComponentState
    {
        public NetCoordinates Target;
        public Angle TargetAngle;
        public float? MaxSpeed;
    }
}
