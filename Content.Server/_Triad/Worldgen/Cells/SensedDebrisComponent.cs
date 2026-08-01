// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Links a materialized debris grid back to the record it was built from, so its death
///     can be told apart from a garbage-collected unload.
///
///     Unsaved: the record reference is round-scoped bookkeeping, and any future capture of a
///     debris grid (wreck persistence blobs, a player somehow saving one) must not carry an
///     orphan marker into the blob. Runtime markers riding serialized grids is a known seam.
/// </summary>
[RegisterComponent, UnsavedComponent]
[Access(typeof(DebrisMaterializeQueueSystem), typeof(DebrisCaptureSystem))]
public sealed partial class SensedDebrisComponent : Component
{
    public DebrisRecord? Record;

    /// <summary>
    ///     Whether anything has touched this grid since it materialized: a tile changed, an
    ///     entity anchored or unanchored on it, something re-parented onto, off or within it
    ///     (container looting included), or a player click-interacted with anything aboard.
    ///     Decides the capture ladder's rung at unload: false is a free rollback to the seed
    ///     roll, true serializes the grid to a blob. Trigger-happy by design; a false positive
    ///     costs a few KB of blob, a false negative regenerates whatever a player took or built.
    /// </summary>
    public bool Touched;

    /// <summary>
    ///     Serialization attempts burned since the last successful capture. Bounded by
    ///     <see cref="DebrisCaptureSystem.MaxCaptureAttempts"/>; at the cap the grid is released
    ///     to the GC uncaptured, loudly, rather than held alive forever against a
    ///     deterministically failing serializer.
    /// </summary>
    public byte CaptureAttempts;

    /// <summary>
    ///     The GC queue tried to collect this grid while a capture was still owed, and the
    ///     capture system cancelled the collection. A cancelled GC entry is dequeued for good,
    ///     so whoever clears this flag must hand the grid back to the queue or it leaks alive.
    /// </summary>
    public bool GcHeld;
}
