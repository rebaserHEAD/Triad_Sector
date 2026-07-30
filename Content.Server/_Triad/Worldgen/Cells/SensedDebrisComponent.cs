// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Links a materialized debris grid back to the record it was built from, so its death
///     can be told apart from a garbage-collected unload.
/// </summary>
[RegisterComponent]
[Access(typeof(DebrisMaterializeQueueSystem))]
public sealed partial class SensedDebrisComponent : Component
{
    public DebrisRecord? Record;
}
