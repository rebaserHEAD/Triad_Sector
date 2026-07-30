using System.Numerics;
using Content.Server.Worldgen.Systems.Debris;

namespace Content.Server.Worldgen.Components.Debris;

/// <summary>
///     This is used for attaching a piece of debris to it's owning controller.
///     Mostly just syncs deletion.
/// </summary>
[RegisterComponent]
// Triad: the materialization queue fills this in on spawn, standing in for the placer.
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

