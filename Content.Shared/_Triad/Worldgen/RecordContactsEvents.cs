// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Worldgen;

[Serializable, NetSerializable]
public sealed class RequestRecordContactsEvent : EntityEventArgs
{
    public NetEntity Console;

    public RequestRecordContactsEvent(NetEntity console)
    {
        Console = console;
    }
}

[Serializable, NetSerializable]
public sealed class RecordContactsDeltaEvent : EntityEventArgs
{
    public NetEntity Console;

    /// <summary>
    ///     The map every id in this event lives on, stamped by the server. Contacts are stored
    ///     per map client-side, and carrying the map here means receipt never has to resolve the
    ///     console entity, which can legitimately be outside the client's view by the time the
    ///     reply lands.
    /// </summary>
    public MapId Map;

    /// <summary>
    ///     The server's current round id, for the client's reconnect chart file: the chart is
    ///     round-scoped, and carrying the round on the channel itself means persistence never
    ///     depends on lobby-state plumbing that a mid-round reconnect may not replay.
    /// </summary>
    public int RoundId;

    /// <summary>
    /// Client clears its live set for this console before applying.
    /// </summary>
    public bool FullReset;

    /// <summary>
    ///     Shape recipes for the prototypes referenced by <see cref="Adds"/>, indexed by
    ///     <see cref="RecordContactData.ProtoIndex"/>. Rebuilt per event, so indices are only
    ///     meaningful within the delta that carried them. A legend instead of a proto id per
    ///     contact because NetSerializer has no string dedup on the wire: a belt repeats the same
    ///     handful of prototypes hundreds of times, and re-encoding a ~20 byte string per contact
    ///     is the single largest avoidable cost in the old format. An index is one varint byte.
    /// </summary>
    public List<DebrisProtoRecipe> Legend;

    public List<RecordContactData> Adds;

    /// <summary>
    ///     Existence-scope removals: the rock is gone (destroyed in play, cell retired, ghost
    ///     rock). Drops from the live view AND the chart.
    /// </summary>
    public List<int> Removes;

    /// <summary>
    ///     View-scope removals: the rock still exists but left this console's picture (out of
    ///     range, other map, materialized into a real grid). Drops from the live view only; the
    ///     chart keeps its last-known entry, which is the whole point of having one.
    /// </summary>
    public List<int> Fades;

    public RecordContactsDeltaEvent(NetEntity console, MapId map, int roundId, bool fullReset,
        List<DebrisProtoRecipe> legend, List<RecordContactData> adds, List<int> removes, List<int> fades)
    {
        Console = console;
        Map = map;
        RoundId = roundId;
        FullReset = fullReset;
        Legend = legend;
        Adds = adds;
        Removes = removes;
        Fades = fades;
    }
}

/// <summary>
///     How a contact's geometry reaches the client. <see cref="RecordContactData.Version"/> ships
///     inert so debris persistence later is a server-side feature with no protocol change: a
///     blob-restored rock will arrive on the Explicit arm with a bumped version. Clients skip
///     contacts carrying arm values they do not understand, so adding an arm is likewise free.
/// </summary>
[Serializable, NetSerializable]
public enum RecordContactArm : byte
{
    /// <summary>
    ///     The client rolls the shape itself from (recipe, seed) through the shared
    ///     <see cref="BlobShapeGen"/> / <see cref="TileOutline"/> code, so the drawn silhouette is
    ///     bit-identical to the rock that materializes. The only arm produced today.
    /// </summary>
    Pristine = 0,

    /// <summary>
    ///     Outline carried verbatim in <see cref="RecordContactData.Outline"/>: the escape hatch
    ///     for anything not seed-derivable. No current producer.
    /// </summary>
    Explicit = 1,
}

/// <summary>
///     Everything the client needs to re-roll a prototype's blob shape locally, mirrored from the
///     server-only BlobFloorPlanBuilder component. Mirrored rather than read off the prototype
///     because the client's component factory never registers server-only components, so the
///     prototype's copy of these numbers is invisible client-side.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct DebrisProtoRecipe(
    string ProtoId,
    float Radius,
    int FloorPlacements,
    float BlobDrawProb,
    int TilesetCount);

/// <summary>
///     One dormant rock as the radar learns of it. The wire carries the recipe, not the geometry:
///     roughly 20 bytes per contact against ~350-850 for the traced outline it replaces. Color is
///     not carried at all; the client derives it from the prototype's IFF component.
///     <paramref name="Outline"/> is populated only on the <see cref="RecordContactArm.Explicit"/>
///     arm, in grid-local tile units relative to <paramref name="MapPosition"/> (tile corners are
///     integral, and NetSerializer writes an int as a zigzag varint but a float as four fixed
///     bytes); a null array costs a single sentinel byte on every other arm.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct RecordContactData(
    int Id,
    int Version,
    RecordContactArm Arm,
    Vector2 MapPosition,
    int ProtoIndex,
    int Seed,
    Vector2i[]? Outline);
