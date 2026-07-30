// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Content.Server._Triad.Worldgen.Cells;
using Robust.Shared.Analyzers;
using Robust.Shared.Maths;

namespace Content.Benchmarks;

/// <summary>
///     Measures the seed-deterministic debris shape walk against the linear-scan version it
///     replaced. The walk runs twice per piece of debris (once when the cell is described, once
///     when the grid is actually built), so its cost lands directly on the describe sweep and on
///     chunk-load ticks.
/// </summary>
[Virtual]
public class BlobShapeGenBenchmark
{
    // Real NF debris prototype shapes: small wreck, mid asteroid, gigantic asteroid.
    [Params(96, 276, 324)]
    public int Placements;

    [Params(5f, 26f)]
    public float Radius;

    private const float DrawProb = 0.5f;
    private const int TilesetCount = 4;

    [Benchmark(Baseline = true, Description = "Linear scan (pre-optimization)")]
    public int LinearScan()
    {
        return RollLinearScan(new Random(12345), Radius, Placements, DrawProb, TilesetCount).Count;
    }

    [Benchmark(Description = "Indexed swap-remove (current)")]
    public int Indexed()
    {
        return BlobShapeGen.Roll(new Random(12345), Radius, Placements, DrawProb, TilesetCount).Count;
    }

    /// <summary>
    ///     Verbatim copy of the walk as it stood before the position index was introduced, kept
    ///     here only so the benchmark has an honest baseline to compare against. Do not use this
    ///     for anything else.
    /// </summary>
    private static List<BlobTile> RollLinearScan(Random rng, float radius, int floorPlacements, float blobDrawProb,
        int tilesetCount)
    {
        var candidates = new List<Vector2i>(floorPlacements * 6);
        var candidateSet = new HashSet<Vector2i>(floorPlacements * 6);
        var taken = new HashSet<Vector2i>(floorPlacements * 5);
        var tiles = new List<BlobTile>(floorPlacements + 1);
        var radsq = Math.Pow(radius, 2);

        void AddCandidate(Vector2i point)
        {
            if (taken.Contains(point) || !candidateSet.Add(point))
                return;
            candidates.Add(point);
        }

        void RemoveCandidate(Vector2i point)
        {
            if (!candidateSet.Remove(point))
                return;
            var idx = candidates.IndexOf(point);
            var lastIdx = candidates.Count - 1;
            candidates[idx] = candidates[lastIdx];
            candidates.RemoveAt(lastIdx);
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

            if (Math.Pow(north.X, 2) + Math.Pow(north.Y, 2) <= radsq)
                AddCandidate(north);
            if (Math.Pow(south.X, 2) + Math.Pow(south.Y, 2) <= radsq)
                AddCandidate(south);
            if (Math.Pow(east.X, 2) + Math.Pow(east.Y, 2) <= radsq)
                AddCandidate(east);
            if (Math.Pow(west.X, 2) + Math.Pow(west.Y, 2) <= radsq)
                AddCandidate(west);
        }

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
}
