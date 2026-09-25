using Content.Shared.Kitchen;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server.Kitchen.Components;

/// <summary>
/// Attached to a microwave that is currently in the process of cooking
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class ActiveMicrowaveComponent : Component
{
    [ViewVariables(VVAccess.ReadWrite)]
    public float CookTimeRemaining;

    [ViewVariables(VVAccess.ReadWrite)]
    public float TotalTime;

    [ViewVariables(VVAccess.ReadWrite)]
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    [AutoPausedField]
    // Triad: null is no malfunction due. The generated unpause handler shifts only a value that is set, so an unpause
    // cannot make one; a zero is shifted by the pause residency like any deadline and then reads as overdue in
    // MicrowaveSystem.RollMalfunction.
    // public TimeSpan MalfunctionTime = TimeSpan.Zero;
    public TimeSpan? MalfunctionTime;

    [ViewVariables]
    public (FoodRecipePrototype?, int) PortionedRecipe;
}
