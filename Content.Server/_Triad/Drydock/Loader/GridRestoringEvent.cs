using System.Collections.Generic;
using Robust.Shared.GameObjects;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// The head of a restore: raised once, as a broadcast, after the last entity has started and the manifest's after-start
/// members are set, and before the first directed <see cref="GridRestoredEvent"/>. A rebuild that has to finish for the whole
/// grid before any entity's own rebuild runs, such as joining every device to its network, rides this event; a per-entity
/// rebuild rides the directed one.
///
/// <para><paramref name="Entities"/> is every restored entity that exists, in ascending stable id. A handler on this event
/// carries the restore contract: it does not add a component to a restored entity (the entity is at MapInitialized, so the
/// added component alone would get <c>MapInitEvent</c>), and it does not rely on another handler's effects unless the
/// rebuild list orders them.</para>
/// </summary>
[ByRefEvent]
public readonly record struct GridRestoringEvent(EntityUid Grid, IReadOnlyList<EntityUid> Entities);
