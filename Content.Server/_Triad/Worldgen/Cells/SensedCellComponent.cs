// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Marks a world chunk entity as described by the sensed worldgen tier and holds its
///     debris records. Presence of this component means the cell's contents are decided;
///     an empty record set is a described-and-empty cell, not an undescribed one.
/// </summary>
[RegisterComponent]
[Access(typeof(CellDescribeSystem), typeof(DebrisMaterializeQueueSystem))]
public sealed partial class SensedCellComponent : Component
{
    public readonly Dictionary<Vector2, DebrisRecord> Records = new();
}
