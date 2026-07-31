// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Content.Server._Triad.Worldgen.Cells;

public enum SensedState : byte
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
    ///     re-sends on version mismatch. Nothing bumps it yet: unload is a rollback today, so 0
    ///     means "pristine roll from <see cref="Seed"/>" for the life of the round. The bump
    ///     site, when debris persistence lands, is unload capture in the materialize queue.
    /// </summary>
    public int Version;

    /// <summary>Hull AABB diagonal times the prototype's visual detection multiplier.</summary>
    public float DetectSignature;

    /// <summary>Flat visual detection bias from the prototype (asteroids carry 1024).</summary>
    public float DetectBias;

    public SensedState State = SensedState.Dormant;

    public EntityUid? Entity;

    /// <summary>Set while sitting in the materialization queue to prevent double-enqueue.</summary>
    public bool Queued;

    /// <summary>Materialize attempts dropped because the spawn point was blocked.</summary>
    public byte BlockedAttempts;

    /// <summary>
    ///     Scratch sort key: seconds until the nearest loader reaches <see cref="Point"/>. Written
    ///     by the materialization queue's ordering pass and only meaningful during it. Not
    ///     persisted, not networked, and nothing outside that pass may read it.
    /// </summary>
    public float ArrivalKey;
}
