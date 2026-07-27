using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Shuttles;

/// <summary>
/// Raised on the client when it wants to cancel the console's active autopilot flight order.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShuttleConsoleAutopilotCancelMessage : BoundUserInterfaceMessage
{
}
