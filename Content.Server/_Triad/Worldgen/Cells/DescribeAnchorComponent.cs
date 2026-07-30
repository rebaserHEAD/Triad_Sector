// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Marks an entity as making the space around it real: <see cref="CellDescribeSystem"/> rolls
///     debris in a halo around anything carrying this, whether or not a sensor is watching and
///     whether or not the entity loads chunks. Ships and people are picked up on their own; this
///     is for the things that anchor a region without doing either, chiefly stations and points
///     of interest.
/// </summary>
[RegisterComponent]
public sealed partial class DescribeAnchorComponent : Component
{
    /// <summary>
    ///     Describe reach in metres, measured from the anchor's hull edge. Null takes the
    ///     server-wide sensed range, which is what almost everything should want.
    /// </summary>
    [DataField]
    public float? Range;
}
