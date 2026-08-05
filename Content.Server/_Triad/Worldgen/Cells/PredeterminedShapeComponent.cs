// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Pins the debris' procedural shape rolls to this seed, so a radar-described shape rolled ahead of
///     materialization matches the shape actually built onto the grid.
/// </summary>
[RegisterComponent]
public sealed partial class PredeterminedShapeComponent : Component
{
    [DataField]
    public int Seed;
}
