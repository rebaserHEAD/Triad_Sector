using Content.Shared.Damage;
using Content.Shared.FixedPoint;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// A copying sidecar carrying one entity's damage across a store and retrieve.
/// <see cref="DamageableComponent.Damage"/> is declared read-only to the serializer, so it is never
/// written and a damaged ship would otherwise come back pristine, which is a free repair on every
/// combat vessel.
///
/// <para>Copied at store, applied and removed by an explicit pass after the grid has fully
/// materialized. The component's own presence is the marker, so no lifestage guard is wanted.</para>
/// </summary>
/// <remarks>
/// It holds the raw damage dictionary rather than a <see cref="DamageSpecifier"/> because
/// <see cref="DamageSpecifier.DamageDict"/> is itself read-only to the serializer: a
/// DamageSpecifier-typed field would serialize empty no matter what this component declared.
/// </remarks>
[RegisterComponent]
public sealed partial class DrydockDamageSidecarComponent : Component
{
    [DataField]
    public Dictionary<string, FixedPoint2> DamageDict = new();
}
