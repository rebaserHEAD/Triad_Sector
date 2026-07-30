// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Content.Server._Triad.Worldgen.Cells;

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

    public static Vector2[] ComputeHull(IReadOnlyList<BlobTile> tiles, int maxVerts = 16)
    {
        var points = new List<Vector2>(tiles.Count * 4);
        foreach (var tile in tiles)
        {
            var x = tile.Pos.X;
            var y = tile.Pos.Y;
            points.Add(new Vector2(x, y));
            points.Add(new Vector2(x + 1, y));
            points.Add(new Vector2(x, y + 1));
            points.Add(new Vector2(x + 1, y + 1));
        }

        var hull = MonotoneChainHull(points);

        // Trimming cuts corners inward, so the extreme vertices are pinned: the simplified
        // outline must keep the true bounding box. Detection range is derived from that box,
        // and a contact that resolved at the wrong range would betray the handoff.
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        foreach (var vert in hull)
        {
            minX = MathF.Min(minX, vert.X);
            minY = MathF.Min(minY, vert.Y);
            maxX = MathF.Max(maxX, vert.X);
            maxY = MathF.Max(maxY, vert.Y);
        }

        bool IsExtreme(Vector2 v) => v.X == minX || v.X == maxX || v.Y == minY || v.Y == maxY;

        while (hull.Count > maxVerts)
        {
            var removeIdx = -1;
            var smallestArea = double.MaxValue;
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

    private static double TriangleArea2(Vector2 a, Vector2 b, Vector2 c)
    {
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }

    private static List<Vector2> MonotoneChainHull(List<Vector2> points)
    {
        var pts = new List<Vector2>(new HashSet<Vector2>(points));
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        if (pts.Count < 3)
            return pts;

        var lower = new List<Vector2>();
        foreach (var p in pts)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }

        var upper = new List<Vector2>();
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

    private static double Cross(Vector2 o, Vector2 a, Vector2 b)
    {
        return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }
}
