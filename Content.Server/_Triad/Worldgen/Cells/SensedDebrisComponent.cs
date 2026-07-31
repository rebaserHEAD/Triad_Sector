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
[Access(typeof(DebrisMaterializeQueueSystem))]
public sealed partial class SensedDebrisComponent : Component
{
    public DebrisRecord? Record;
}
