// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Content.Shared._Triad.Worldgen;

public readonly record struct BlobTile(Vector2i Pos, int TilesetIndex);

/// <summary>
///     Pure, seed-deterministic random-walk blob shape generator. Extracted from
///     <c>BlobFloorPlanBuilderSystem.PlaceFloorplanTiles</c> so the same walk can be rolled
///     ahead of grid materialization (e.g. for radar-sensed shape previews).
/// </summary>
public static class BlobShapeGen
{
    public static List<BlobTile> Roll(System.Random rng, float radius, int floorPlacements, float blobDrawProb,
        int tilesetCount)
    {
        if (tilesetCount < 1)
            throw new ArgumentOutOfRangeException(nameof(tilesetCount), tilesetCount, "Must be at least 1.");
        if (floorPlacements < 0)
            throw new ArgumentOutOfRangeException(nameof(floorPlacements), floorPlacements, "Must be non-negative.");

        var candidates = new List<Vector2i>(floorPlacements * 6);
        // Position index rather than a plain membership set: removal is a swap with the tail, and
        // finding the victim's slot by scanning was the walk's dominant cost at prototype sizes
        // (a few hundred placements against a candidate list that runs past a thousand). The
        // resulting list order is identical either way, which matters because the draw at the
        // bottom of this method indexes straight into it.
        var candidateIndex = new Dictionary<Vector2i, int>(floorPlacements * 6);
        var taken = new HashSet<Vector2i>(floorPlacements * 5);
        var tiles = new List<BlobTile>(floorPlacements + 1);
        var radsq = Math.Pow(radius, 2);

        void AddCandidate(Vector2i point)
        {
            if (taken.Contains(point) || candidateIndex.ContainsKey(point))
                return;
            candidateIndex[point] = candidates.Count;
            candidates.Add(point);
        }

        void RemoveCandidate(Vector2i point)
        {
            if (!candidateIndex.Remove(point, out var idx))
                return;

            var lastIdx = candidates.Count - 1;
            var moved = candidates[lastIdx];
            candidates[idx] = moved;
            candidates.RemoveAt(lastIdx);

            if (idx != lastIdx)
                candidateIndex[moved] = idx;
        }

        void PlaceTile(Vector2i point)
        {
            RemoveCandidate(point);
            taken.Add(point);
            tiles.Add(new BlobTile(point, rng.Next(tilesetCount)));

            var north = point.Offset(Direction.North);
            var south = point.Offset(Direction.South);
            var east = point.Offset(Direction.East);
            var west = point.Offset(Direction.West);

            // Same distance-squared-from-origin check as the upstream walk. Integer squares are
            // exact in a double, so dropping Math.Pow here is a speedup and not a rounding change.
            if (DistSqFromOrigin(north) <= radsq)
                AddCandidate(north);
            if (DistSqFromOrigin(south) <= radsq)
                AddCandidate(south);
            if (DistSqFromOrigin(east) <= radsq)
                AddCandidate(east);
            if (DistSqFromOrigin(west) <= radsq)
                AddCandidate(west);
        }

        static double DistSqFromOrigin(Vector2i p) => (double) p.X * p.X + (double) p.Y * p.Y;

        PlaceTile(Vector2i.Zero);

        for (var i = 0; i < floorPlacements; i++)
        {
            if (candidates.Count == 0)
                break;

            var point = candidates[rng.Next(candidates.Count)];
            PlaceTile(point);

            if (blobDrawProb > 0.0f)
            {
                var north = point.Offset(Direction.North);
                var south = point.Offset(Direction.South);
                var east = point.Offset(Direction.East);
                var west = point.Offset(Direction.West);

                // Deliberately NOT radius-checked, matching the upstream walk: these go straight
                // through PlaceTile. So the real bound on a tile's distance from origin is
                // radius + 1, not radius. It does not cascade, since PlaceTile only contributes
                // candidates that are radius-checked on the way in.
                if (!taken.Contains(north) && rng.NextDouble() < blobDrawProb)
                    PlaceTile(north);
                if (!taken.Contains(south) && rng.NextDouble() < blobDrawProb)
                    PlaceTile(south);
                if (!taken.Contains(east) && rng.NextDouble() < blobDrawProb)
                    PlaceTile(east);
                if (!taken.Contains(west) && rng.NextDouble() < blobDrawProb)
                    PlaceTile(west);
            }
        }

        return tiles;
    }

    /// <summary>
    ///     Convex hull of the rolled tile set, as a fallback outline.
    ///     <paramref name="maxVerts"/> sits above the largest hull any shipping prototype
    ///     produces (observed maximum 22 over 200 rolls each), so the trim loop below does not
    ///     fire in practice. That is deliberate: the trim is not outward-safe, so the cheapest
    ///     way to keep the outline a strict superset of the rock is to never need it.
    /// </summary>
    public static Vector2i[] ComputeHull(IReadOnlyList<BlobTile> tiles, int maxVerts = 24)
    {
        var points = new List<Vector2i>(tiles.Count * 4);
        foreach (var tile in tiles)
        {
            var x = tile.Pos.X;
            var y = tile.Pos.Y;
            points.Add(new Vector2i(x, y));
            points.Add(new Vector2i(x + 1, y));
            points.Add(new Vector2i(x, y + 1));
            points.Add(new Vector2i(x + 1, y + 1));
        }

        var hull = MonotoneChainHull(points);

        // Pinning the extreme vertices preserves the true bounding box, and nothing more.
        // Detection range is derived from that box, so a contact would otherwise resolve at the
        // wrong range and betray the handoff.
        //
        // It does NOT make the trim safe. Deleting an interior vertex replaces two edges with a
        // chord across the corner, and that chord can pass through real tiles, so a trimmed
        // outline can read smaller than the rock it stands for. The default maxVerts is set above
        // any observed hull size precisely so this loop stays unreached; if you lower it, the
        // outline stops being a strict superset and radar can report empty where rock is.
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        foreach (var vert in hull)
        {
            minX = Math.Min(minX, vert.X);
            minY = Math.Min(minY, vert.Y);
            maxX = Math.Max(maxX, vert.X);
            maxY = Math.Max(maxY, vert.Y);
        }

        bool IsExtreme(Vector2i v) => v.X == minX || v.X == maxX || v.Y == minY || v.Y == maxY;

        while (hull.Count > maxVerts)
        {
            var removeIdx = -1;
            var smallestArea = long.MaxValue;
            for (var i = 0; i < hull.Count; i++)
            {
                if (IsExtreme(hull[i]))
                    continue;

                var prev = hull[(i - 1 + hull.Count) % hull.Count];
                var cur = hull[i];
                var next = hull[(i + 1) % hull.Count];
                var area = Math.Abs(TriangleArea2(prev, cur, next));
                if (area < smallestArea)
                {
                    smallestArea = area;
                    removeIdx = i;
                }
            }

            // Every remaining vertex is load-bearing for the bounds; a slightly longer outline
            // beats a wrong one.
            if (removeIdx < 0)
                break;

            hull.RemoveAt(removeIdx);
        }

        return hull.ToArray();
    }

    // long, not double: tile corners are integers, so twice-the-area is exact in integer
    // arithmetic. Coordinates stay well inside a byte for any shipping prototype, so this
    // cannot overflow, and it removes the last floating-point comparison from the outline path.
    private static long TriangleArea2(Vector2i a, Vector2i b, Vector2i c)
    {
        return (long) (b.X - a.X) * (c.Y - a.Y) - (long) (b.Y - a.Y) * (c.X - a.X);
    }

    private static List<Vector2i> MonotoneChainHull(List<Vector2i> points)
    {
        // The HashSet only dedupes; the sort immediately after is what fixes the order, and it is
        // total because the points are distinct after deduping. Set iteration order never reaches
        // the output, which matters because describe and materialize must agree exactly.
        var pts = new List<Vector2i>(new HashSet<Vector2i>(points));
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        if (pts.Count < 3)
            return pts;

        var lower = new List<Vector2i>();
        foreach (var p in pts)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }

        var upper = new List<Vector2i>();
        for (var i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static long Cross(Vector2i o, Vector2i a, Vector2i b)
    {
        return (long) (a.X - o.X) * (b.Y - o.Y) - (long) (a.Y - o.Y) * (b.X - o.X);
    }
}
