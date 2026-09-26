using System.Diagnostics.CodeAnalysis;
using Robust.Shared.GameObjects;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// Raised once a loaded image has fully started: directed at each restored entity in ascending order of its id in the
/// image (<see cref="DrydockImageEntity.Id"/>, the <c>entity_id</c> column) after the
/// last entity has started and the manifest's after-start members are set and after <see cref="GridRestoringEvent"/> has
/// completed, then once more as a broadcast for work that is per grid. A handler rebuilds what the image does not carry, and may read any other restored entity, because every one
/// has started.
///
/// <para>The directed raise carries its <paramref name="Target"/> and the load's <paramref name="Carried"/> values; the
/// broadcast carries neither, so <see cref="TryGetCarried{T}"/> answers false there.</para>
/// </summary>
[ByRefEvent]
public readonly record struct GridRestoredEvent(EntityUid Grid, EntityUid Target = default, DrydockCarried? Carried = null)
{
    /// <summary>The value this entity carried under <paramref name="key"/> (<see cref="DrydockCarried.TryGet{T}"/>).</summary>
    public bool TryGetCarried<T>(string key, [NotNullWhen(true)] out T? value) where T : notnull
    {
        value = default;
        return Carried != null && Target.IsValid() && Carried.TryGet(Target, key, out value);
    }
}
