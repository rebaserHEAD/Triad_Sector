using Robust.Shared.Map;

namespace Content.Server._Triad.Shuttles;

/// <summary>
/// Active autopilot flight order on a shuttle console. Present only while autopilot is engaged;
/// <see cref="Content.Server._Mono.Shuttles.ShuttleConsoleAutopilotSystem"/> drives the steering
/// servo from it directly, with no HTN brain involved.
/// </summary>
[RegisterComponent]
public sealed partial class ShuttleAutopilotComponent : Component
{
    /// <summary>
    /// Destination handed to the steering servo. Re-applied every tick so steering survives
    /// grid changes, mirroring what the old HTN operator did.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public EntityCoordinates Target;

    /// <summary>
    /// World angle to settle at on arrival.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public Angle TargetAngle;

    /// <summary>
    /// Speed limit for the flight, stamped from the ordering pilot's helm speed limiter
    /// when the destination is set. Null = no limit.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public float? MaxSpeed;
}
