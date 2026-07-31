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
    /// <summary>
    ///     Rolls from a wire recipe: the single convention every recipe consumer shares, so the
    ///     client preview, the server's collision tiles and the tests draw the identical stream.
    ///     Owns the tileset floor of one that <see cref="Roll(System.Random,float,int,float,int)"/>
    ///     would otherwise reject, matching how recipes are minted server-side.
    /// </summary>
    public static List<BlobTile> Roll(System.Random rng, in SensedProtoRecipe recipe)
        => Roll(rng, recipe.Radius, recipe.FloorPlacements, recipe.BlobDrawProb, Math.Max(1, recipe.TilesetCount));

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
}
