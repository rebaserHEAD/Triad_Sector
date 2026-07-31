using System.Numerics;
using Content.Server.Worldgen.Systems.Debris;

namespace Content.Server.Worldgen.Components.Debris;

/// <summary>
///     This is used for attaching a piece of debris to it's owning controller.
///     Mostly just syncs deletion.
/// </summary>
[RegisterComponent, UnsavedComponent] // Triad: UnsavedComponent, see below
// Triad: the materialization queue fills this in on spawn, standing in for the placer.
// Triad: UnsavedComponent - OwningController is an entity ref to a live world chunk, pure
// round-scoped bookkeeping. Serializing a debris grid (wreck persistence blobs) would write it
// as a dangling ref that reloads Invalid and errors on every load; the placer re-books moved
// debris at runtime anyway, so nothing in a saved grid wants this.
[Access(typeof(DebrisFeaturePlacerSystem), typeof(Content.Server._Triad.Worldgen.Cells.DebrisMaterializeQueueSystem))]
public sealed partial class OwnedDebrisComponent : Component
{
    /// <summary>
    ///     The last location in the controller's internal structure for this debris.
    /// </summary>
    [DataField("lastKey")] public Vector2 LastKey;

    /// <summary>
    ///     The DebrisFeaturePlacerController-having entity that owns this.
    /// </summary>
    [DataField("owningController")] public EntityUid OwningController;
}

