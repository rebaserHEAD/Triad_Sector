using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Robust.Shared.GameObjects;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// The head of a restore: raised once, as a broadcast, after the last entity has started and the manifest's after-start
/// members are set, and before the first directed <see cref="GridRestoredEvent"/>. A rebuild that has to finish for the whole
/// grid before any entity's own rebuild runs, such as joining every device to its network, rides this event; a per-entity
/// rebuild rides the directed one.
///
/// <para><paramref name="Entities"/> is every restored entity that exists, in ascending order of its id in the image
/// (<see cref="DrydockImageEntity.Id"/>, the <c>entity_id</c> column). A handler on this event
/// carries the restore contract: it does not add a component to a restored entity (the entity is at MapInitialized, so the
/// added component alone would get <c>MapInitEvent</c>), and it does not rely on another handler's effects unless the
/// rebuild list orders them.</para>
///
/// <para><paramref name="Carried"/> is every entity's carried values, for a rebuild that needs them all before any
/// entity's own handler runs.</para>
/// </summary>
[ByRefEvent]
public readonly record struct GridRestoringEvent(EntityUid Grid, IReadOnlyList<EntityUid> Entities, DrydockCarried? Carried = null)
{
    /// <summary>The value <paramref name="uid"/> carried under <paramref name="key"/> (<see cref="DrydockCarried.TryGet{T}"/>).</summary>
    public bool TryGetCarried<T>(EntityUid uid, string key, [NotNullWhen(true)] out T? value) where T : notnull
    {
        value = default;
        return Carried != null && Carried.TryGet(uid, key, out value);
    }
}
