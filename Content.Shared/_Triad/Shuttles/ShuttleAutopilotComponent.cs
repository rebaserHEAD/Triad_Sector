using Robust.Shared.GameStates;
using Robust.Shared.Map;

namespace Content.Shared._Triad.Shuttles;

/// <summary>
/// Active autopilot flight order on a shuttle console. Present only while autopilot is engaged;
/// the server's ShuttleConsoleAutopilotSystem drives the steering servo from it directly, with
/// no HTN brain involved. Networked so the console UI can show flight status.
/// </summary>
[RegisterComponent, NetworkedComponent]
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
    /// Speed limit for the flight, stamped from the helm's stored speed limiter when the
    /// destination is set. Null = no limit.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public float? MaxSpeed;
}
