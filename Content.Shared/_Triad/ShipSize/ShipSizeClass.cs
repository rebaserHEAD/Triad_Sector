using Robust.Shared.Serialization;

namespace Content.Shared._Triad.ShipSize;

/// <summary>
/// Size classification for a ship's hull, derived from its built (non-empty) tile count.
/// </summary>
[Serializable, NetSerializable]
public enum ShipSizeClass : byte
{
    Cutter,
    Corvette,
    Frigate,
    Cruiser,
    Capital,
    SuperCapital,
}
