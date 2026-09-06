namespace Content.Server._Triad.Drydock;

/// <summary>
/// A copying sidecar carrying one entity's live appearance data across a store and retrieve.
///
/// <para>Appearance is the largest hole the fidelity probe cannot see. That probe asks whether the
/// serializer can write a declared <c>[DataField]</c>; appearance data is not a declared field at
/// all. <c>AppearanceComponent</c> declares exactly one, <c>appearanceDataInit</c>, which is
/// read-only and exists only so a prototype can seed the dictionary. Everything the game writes
/// afterwards through <c>SharedAppearanceSystem.SetData</c> lives in an internal dictionary that no
/// save has ever contained.</para>
///
/// <para>So every visual a player changed comes back at its prototype default, and the entity's
/// real state is fine underneath: a lathe frozen mid-animation, a hydroponics tray showing no dead
/// plant while still holding one, a light that reads off while powered (test server, 2026-09-06).
/// Each of those was previously a hand-written revive step per machine. This carries the whole
/// class instead.</para>
///
/// <para>Copied at store and left live, since the serializer ignores appearance rather than choking
/// on it. Applied and removed by an explicit pass at retrieve. The component's own presence is the
/// marker.</para>
/// </summary>
/// <remarks>
/// Keyed by <c>EnumTypeFullName|MemberName</c>, valued as base64 of YAML, matching
/// <see cref="DrydockCapturedStateComponent"/>. The value's own concrete type rides inside that
/// YAML, because an appearance value is typed <c>object</c> and the restore has nothing else to
/// tell it what to read back. Both halves of the key are the same rename-drift surface the captured
/// state carries, and misses are counted and named for the same reason.
/// </remarks>
[RegisterComponent]
public sealed partial class DrydockAppearanceComponent : Component
{
    [DataField]
    public Dictionary<string, string> Data = new();
}
