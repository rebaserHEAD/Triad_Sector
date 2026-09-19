using Robust.Shared.GameObjects;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// Raised once a loaded image has fully started: directed at each restored entity in ascending stable id order after the
/// last entity has started and the manifest's after-start members are set, then once more as a broadcast for work that is
/// per grid. A handler rebuilds what the image does not carry, and may read any other restored entity, because every one
/// has started.
/// </summary>
[ByRefEvent]
public readonly record struct GridRestoredEvent(EntityUid Grid);
