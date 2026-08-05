// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Content.Shared._Triad.Worldgen;

/// <summary>
///     Grid traversal for data-space collision: resolves a segment against a rolled tile set
///     without any entity or physics existing. Tile (x, y) spans [x, x+1) by [y, y+1), matching
///     <see cref="BlobShapeGen"/> output.
/// </summary>
public static class TileWalk
{
    /// <summary>
    ///     Walks the tiles the segment crosses, in order from <paramref name="start"/>, and
    ///     returns the first one contained in <paramref name="tiles"/> together with the entry
    ///     parameter t in [0, 1]. Amanatides-Woo traversal: exact axis-crossing arithmetic, no
    ///     epsilon, no step size to tune. A corner-exact crossing resolves through one of the two
    ///     adjacent tiles rather than both, which for a projectile against 1m tiles is
    ///     indistinguishable from either answer.
    /// </summary>
    public static bool TraceSegment(Vector2 start, Vector2 end, HashSet<Vector2i> tiles,
        out Vector2i hitTile, out float hitT)
    {
        hitTile = default;
        hitT = 0f;

        var cell = new Vector2i((int) MathF.Floor(start.X), (int) MathF.Floor(start.Y));
        if (tiles.Contains(cell))
        {
            hitTile = cell;
            return true;
        }

        var delta = end - start;

        var stepX = delta.X > 0 ? 1 : delta.X < 0 ? -1 : 0;
        var stepY = delta.Y > 0 ? 1 : delta.Y < 0 ? -1 : 0;

        // Parameter t at which the walk next crosses an X (resp. Y) gridline, and how much t one
        // whole tile costs on that axis. A zero-delta axis never crosses: infinity.
        var tMaxX = stepX != 0
            ? ((stepX > 0 ? cell.X + 1 : cell.X) - start.X) / delta.X
            : float.PositiveInfinity;
        var tMaxY = stepY != 0
            ? ((stepY > 0 ? cell.Y + 1 : cell.Y) - start.Y) / delta.Y
            : float.PositiveInfinity;

        var tDeltaX = stepX != 0 ? MathF.Abs(1f / delta.X) : float.PositiveInfinity;
        var tDeltaY = stepY != 0 ? MathF.Abs(1f / delta.Y) : float.PositiveInfinity;

        while (true)
        {
            float t;
            if (tMaxX < tMaxY)
            {
                t = tMaxX;
                tMaxX += tDeltaX;
                cell = new Vector2i(cell.X + stepX, cell.Y);
            }
            else
            {
                t = tMaxY;
                tMaxY += tDeltaY;
                cell = new Vector2i(cell.X, cell.Y + stepY);
            }

            if (t > 1f)
                return false;

            if (!tiles.Contains(cell))
                continue;

            hitTile = cell;
            hitT = t;
            return true;
        }
    }
}
