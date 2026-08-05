using Content.Server.Worldgen.Systems;

namespace Content.Server.Worldgen.Components;

/// <summary>
///     This is used for marking a chunk as loaded.
/// </summary>
[RegisterComponent]
[Access(typeof(WorldControllerSystem))]
public sealed partial class LoadedChunkComponent : Component
{
    /// <summary>
    ///     The current list of entities loading this chunk.
    /// </summary>
    [ViewVariables] public List<EntityUid>? Loaders = null;

    /// <summary>
    ///     Triad: the last time some loader's wanted-set contained this chunk. Load and unload
    ///     share a single boundary quantized to the loader's own cell, so a ship sitting on a
    ///     cell boundary line flips a whole rim of chunks in and out of the wanted-set every
    ///     pass; the unload sweep only drops a chunk once this is older than the grace cvar,
    ///     which is what turns that flicker into nothing instead of a despawn/respawn cycle.
    /// </summary>
    [ViewVariables] public TimeSpan LastWanted;
}

