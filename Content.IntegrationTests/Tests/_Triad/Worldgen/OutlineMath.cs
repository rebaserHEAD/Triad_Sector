// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

/// <summary>
///     Exact area arithmetic for rectilinear outlines on the integer lattice, shared by the
///     outline tests. Integral input keeps the shoelace sum exact, so enclosed-cell counts
///     compare with Is.EqualTo rather than a tolerance.
/// </summary>
internal static class OutlineMath
{
    /// <summary>Twice the signed shoelace area. Integral input, so this is exact.</summary>
    public static long DoubleArea(Vector2i[] poly)
    {
        long acc = 0;

        for (var i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            acc += (long) a.X * b.Y - (long) b.X * a.Y;
        }

        return acc;
    }

    /// <summary>
    ///     Enclosed area in whole cells. The outline is rectilinear on the integer lattice, so its
    ///     area is always a whole number of cells and comparing it to a cell count is exact.
    /// </summary>
    public static long EnclosedCells(Vector2i[] poly) => System.Math.Abs(DoubleArea(poly)) / 2;
}
