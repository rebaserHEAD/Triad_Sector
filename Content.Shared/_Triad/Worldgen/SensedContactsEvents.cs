// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Worldgen;

[Serializable, NetSerializable]
public sealed class RequestSensedContactsEvent : EntityEventArgs
{
    public NetEntity Console;

    public RequestSensedContactsEvent(NetEntity console)
    {
        Console = console;
    }
}

[Serializable, NetSerializable]
public sealed class SensedContactsDeltaEvent : EntityEventArgs
{
    public NetEntity Console;

    /// <summary>
    /// Client clears its live set for this console before applying.
    /// </summary>
    public bool FullReset;

    /// <summary>
    ///     Shape recipes for the prototypes referenced by <see cref="Adds"/>, indexed by
    ///     <see cref="SensedContactData.ProtoIndex"/>. Rebuilt per event, so indices are only
    ///     meaningful within the delta that carried them. A legend instead of a proto id per
    ///     contact because NetSerializer has no string dedup on the wire: a belt repeats the same
    ///     handful of prototypes hundreds of times, and re-encoding a ~20 byte string per contact
    ///     is the single largest avoidable cost in the old format. An index is one varint byte.
    /// </summary>
    public List<SensedProtoRecipe> Legend;

    public List<SensedContactData> Adds;
    public List<int> Removes;

    public SensedContactsDeltaEvent(NetEntity console, bool fullReset, List<SensedProtoRecipe> legend,
        List<SensedContactData> adds, List<int> removes)
    {
        Console = console;
        FullReset = fullReset;
        Legend = legend;
        Adds = adds;
        Removes = removes;
    }
}

/// <summary>
///     How a contact's geometry reaches the client. The union is deliberately wider than what is
///     produced today: <see cref="Modified"/> and <see cref="SensedContactData.Version"/> ship
///     inert so that debris persistence later is a server-side feature with no protocol change.
/// </summary>
[Serializable, NetSerializable]
public enum SensedContactArm : byte
{
    /// <summary>
    ///     The client rolls the shape itself from (recipe, seed) through the shared
    ///     <see cref="BlobShapeGen"/> / <see cref="TileOutline"/> code, so the drawn silhouette is
    ///     bit-identical to the rock that materializes. The only arm produced today.
    /// </summary>
    Pristine = 0,

    /// <summary>
    ///     Outline carried verbatim in <see cref="SensedContactData.Outline"/>: the escape hatch
    ///     for anything not seed-derivable. No current producer.
    /// </summary>
    Explicit = 1,

    /// <summary>
    ///     Reserved: pristine roll plus a modification delta (transform, removed tiles), for
    ///     persist-modified debris. Clients skip contacts carrying arms they do not understand.
    /// </summary>
    Modified = 2,
}

/// <summary>
///     Everything the client needs to re-roll a prototype's blob shape locally, mirrored from the
///     server-only BlobFloorPlanBuilder component. Mirrored rather than read off the prototype
///     because the client's component factory never registers server-only components, so the
///     prototype's copy of these numbers is invisible client-side.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct SensedProtoRecipe(
    string ProtoId,
    float Radius,
    int FloorPlacements,
    float BlobDrawProb,
    int TilesetCount);

/// <summary>
///     One dormant rock as the radar learns of it. The wire carries the recipe, not the geometry:
///     roughly 20 bytes per contact against ~350-850 for the traced outline it replaces. Color is
///     not carried at all; the client derives it from the prototype's IFF component.
///     <paramref name="Outline"/> is populated only on the <see cref="SensedContactArm.Explicit"/>
///     arm, in grid-local tile units relative to <paramref name="MapPosition"/> (tile corners are
///     integral, and NetSerializer writes an int as a zigzag varint but a float as four fixed
///     bytes); a null array costs a single sentinel byte on every other arm.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct SensedContactData(
    int Id,
    int Version,
    SensedContactArm Arm,
    Vector2 MapPosition,
    int ProtoIndex,
    int Seed,
    Vector2i[]? Outline);
