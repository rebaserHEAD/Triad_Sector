// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Content.Server._Triad.Worldgen.Cells;

public enum RecordState : byte
{
    /// <summary>
    ///     Described only: the debris exists as data and a radar contact, no entity.
    /// </summary>
    Dormant,

    /// <summary>
    ///     A live grid entity currently backs this record.
    /// </summary>
    Materialized,
}

/// <summary>
///     A pre-determined piece of worldgen debris. The record is the source of truth:
///     radar contacts are drawn from it while dormant, and materialization spawns the
///     entity from it, so the shape seen at range is the rock that loads in.
///
///     The rock, not the finished grid. Describe models the shape roll and stops there.
///     Decoration runs later, at build time, and can lay tiles of its own: the RoomFill
///     markers in the NF debris tables stamp a room template that may reach a few tiles
///     past the rolled blob. That gap is known and accepted. This is a view, the excess is
///     bounded by the room template size, and a rock always materializes well inside
///     detection range, so nothing is ever navigated by the painted outline alone.
/// </summary>
public sealed class DebrisRecord
{
    /// <summary>Round-stable identifier used by the contact sync channel.</summary>
    public int Id;

    /// <summary>The cell (world chunk entity) that owns this record.</summary>
    public EntityUid Cell;

    public EntityUid Map;

    /// <summary>Spawn position in map coordinates.</summary>
    public Vector2 Point;

    /// <summary>Debris entity prototype id.</summary>
    public string Proto = string.Empty;

    /// <summary>
    ///     Seed all procedural rolls for this debris derive from. Shape today;
    ///     interior layout stages salt this same value.
    /// </summary>
    public int Seed;

    /// <summary>
    ///     Whether the shape roll produced tiles. False for non-blob debris (spawner markers
    ///     etc.), which get no radar contact. The server keeps no outline geometry at all: the
    ///     client re-rolls the shape from (<see cref="Proto"/>, <see cref="Seed"/>) through the
    ///     shared generator, and color derives from the prototype's IFF component client-side.
    /// </summary>
    public bool Shaped;

    /// <summary>
    ///     Bumped whenever the record's client-facing data changes, so the contact channel
    ///     re-sends on version mismatch. A pure wire cache-buster: capture bumps it, and the
    ///     pristine-vs-captured decision reads <see cref="Captured"/>, never this.
    /// </summary>
    public int Version;

    /// <summary>
    ///     A blob-captured record: the rock was touched while materialized, so unload serialized
    ///     the grid instead of rolling it back, and the dormant projections (contact outline,
    ///     data-space tiles, bound) describe the as-captured state rather than the seed roll.
    ///     Set by <see cref="CellDescribeSystem.ApplyCapture"/>, cleared only by the restore
    ///     failure valve (<see cref="CellDescribeSystem.RevertCapture"/>).
    /// </summary>
    public bool Captured;

    /// <summary>
    ///     The as-captured silhouette for the contact channel's Explicit arm, traced from the
    ///     grid's real tiles at capture time in the dormant frame (axis-aligned tile units around
    ///     <see cref="Point"/>). Null on a captured record means the trace failed, and the rock
    ///     then neither paints nor blocks in data space: the two must agree, or the block is an
    ///     invisible wall.
    /// </summary>
    public Vector2i[]? CapturedOutline;

    /// <summary>
    ///     Bounding radius of the rolled tiles around <see cref="Point"/>, in world units. The
    ///     coarse gate for data-space collision queries: a segment farther than this from
    ///     <see cref="Point"/> cannot touch the rock, so the tile set is never rolled for it.
    /// </summary>
    public float Bound;

    public RecordState State = RecordState.Dormant;

    public EntityUid? Entity;

    /// <summary>Set while sitting in the materialization queue to prevent double-enqueue.</summary>
    public bool Queued;

    /// <summary>
    ///     Spawn attempts a record gets before it is left alone for the rest of the load cycle.
    ///     Something durable is parked on the point; retrying it every tick is wasted work.
    ///     <see cref="DebrisMaterializeQueueSystem.EnqueueCell"/> zeroes the count on every cell
    ///     load, so giving up is per load cycle rather than permanent.
    /// </summary>
    public const byte MaxBlockedAttempts = 3;

    /// <summary>Materialize attempts dropped because the spawn point was blocked.</summary>
    public byte BlockedAttempts;

    /// <summary>
    ///     A ghost rock: it burned through its spawn attempts, so nothing durable can stand on
    ///     the point this load cycle. Ghosts are filtered everywhere the player could meet them,
    ///     and the rule is one rule on purpose: what radar will not paint must not stop a shot,
    ///     or the block is an invisible wall.
    /// </summary>
    public bool GaveUp => BlockedAttempts >= MaxBlockedAttempts;

    /// <summary>
    ///     Scratch sort key: seconds until the nearest loader reaches <see cref="Point"/>. Written
    ///     by the materialization queue's ordering pass and only meaningful during it. Not
    ///     persisted, not networked, and nothing outside that pass may read it.
    /// </summary>
    public float ArrivalKey;
}
