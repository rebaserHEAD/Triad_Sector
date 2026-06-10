namespace Content.Server.Kitchen.Components;

/// <summary>
///     Applies to items that are capable of butchering entities, or
///     are otherwise sharp for some purpose.
/// </summary>
[RegisterComponent]
public sealed partial class SharpComponent : Component
{
    // TODO just make this a tool type.
    public HashSet<EntityUid> Butchering = new();

    [DataField("butcherDelayModifier")]
    public float ButcherDelayModifier = 1.0f;

    // Triad: GhettoSurgery (ported from Goob-MRP) makes any sharp item act as a scalpel/bonesaw;
    // these flags track whether it added those tool comps so it can remove them on shutdown.
    public bool HadScalpel;
    public bool HadBoneSaw;
}
