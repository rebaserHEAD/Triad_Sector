// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Content.Server._Triad.Worldgen.Cells;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

[TestFixture]
[TestOf(typeof(BlobShapeGen))]
public sealed class BlobShapeGenTest
{
    private const float Radius = 15f;
    private const int Placements = 100;
    private const float DrawProb = 0.5f;
    private const int TilesetCount = 4;

    [Test]
    public void SameSeed_ProducesIdenticalTiles()
    {
        var a = BlobShapeGen.Roll(new System.Random(12345), Radius, Placements, DrawProb, TilesetCount);
        var b = BlobShapeGen.Roll(new System.Random(12345), Radius, Placements, DrawProb, TilesetCount);

        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void DifferentSeeds_ProduceDifferentTiles()
    {
        var a = BlobShapeGen.Roll(new System.Random(1), Radius, Placements, DrawProb, TilesetCount);
        var b = BlobShapeGen.Roll(new System.Random(2), Radius, Placements, DrawProb, TilesetCount);

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void AllPositions_AreWithinRadiusOfOrigin()
    {
        var tiles = BlobShapeGen.Roll(new System.Random(42), Radius, Placements, DrawProb, TilesetCount);
        var radiusSq = (double) Radius * Radius;

        foreach (var tile in tiles)
        {
            var distSq = (double) tile.Pos.X * tile.Pos.X + (double) tile.Pos.Y * tile.Pos.Y;
            Assert.That(distSq, Is.LessThanOrEqualTo(radiusSq));
        }
    }

    /// <summary>
    ///     Pins the exact walk output across a spread of seeds and prototype shapes. The describe
    ///     pass and the materialize pass roll this walk independently from the same seed, so any
    ///     change to draw count, draw order, or candidate ordering silently desynchronizes the
    ///     radar outline from the grid that loads in. Optimizations to <see cref="BlobShapeGen"/>
    ///     must leave this fingerprint untouched; a deliberate generation change must update it in
    ///     the same commit that changes the walk.
    /// </summary>
    [Test]
    public void Walk_FingerprintIsStable()
    {
        Assert.That(ComputeWalkFingerprint(),
            Is.EqualTo("473FA9F1CEFC496B6C7BE3C14F67081347980FFD7FB005BCA4140E46A288381F"));
    }

    private static string ComputeWalkFingerprint()
    {
        // Spread over the real prototype range: NF debris runs radius 5-26, placements 96-324.
        (int Seed, float Radius, int Placements, float DrawProb, int Tileset)[] cases =
        {
            (1, 15f, 100, 0.5f, 4),
            (2, 15f, 100, 0.5f, 4),
            (42, 15f, 100, 0.5f, 4),
            (12345, 26f, 276, 0.5f, 4),
            (7, 5f, 96, 0.0f, 1),
            (999, 26f, 324, 0.9f, 8),
            (2024, 10f, 200, 0.25f, 2),
        };

        var sb = new StringBuilder();
        foreach (var (seed, radius, placements, drawProb, tileset) in cases)
        {
            var tiles = BlobShapeGen.Roll(new System.Random(seed), radius, placements, drawProb, tileset);
            sb.Append(seed).Append(':').Append(tiles.Count).Append('|');
            foreach (var tile in tiles)
                sb.Append(tile.Pos.X).Append(',').Append(tile.Pos.Y).Append(',').Append(tile.TilesetIndex).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    [Test]
    public void TileCount_IsAtLeastFloorPlacementsPlusOne()
    {
        // Radius/placements chosen so the candidate pool can't exhaust before the loop finishes:
        // every main-loop iteration is guaranteed to place its mandatory tile.
        var tiles = BlobShapeGen.Roll(new System.Random(7), Radius, Placements, DrawProb, TilesetCount);

        Assert.That(tiles.Count, Is.GreaterThanOrEqualTo(Placements + 1));
    }

    [Test]
    public void Hull_IsConvexAndContainsAllTileCorners()
    {
        var tiles = BlobShapeGen.Roll(new System.Random(42), Radius, Placements, DrawProb, TilesetCount);
        var hull = BlobShapeGen.ComputeHull(tiles);

        // Tracks ComputeHull's maxVerts default. It was left at 16 when the cap moved to 24, which
        // meant the assertion no longer said anything about the code it was guarding.
        Assert.That(hull.Length, Is.LessThanOrEqualTo(24));
        Assert.That(IsConvexCcw(hull), Is.True, "Hull is not convex / not wound counter-clockwise.");

        foreach (var tile in tiles)
        {
            foreach (var corner in Corners(tile))
            {
                Assert.That(IsInsideOrOnHull(hull, corner), Is.True,
                    $"Tile corner {corner} of tile {tile.Pos} falls outside the hull.");
            }
        }
    }

    [Test]
    public void Hull_IsDeterministicForSameSeed()
    {
        var tilesA = BlobShapeGen.Roll(new System.Random(555), Radius, Placements, DrawProb, TilesetCount);
        var tilesB = BlobShapeGen.Roll(new System.Random(555), Radius, Placements, DrawProb, TilesetCount);

        var hullA = BlobShapeGen.ComputeHull(tilesA);
        var hullB = BlobShapeGen.ComputeHull(tilesB);

        Assert.That(hullA, Is.EqualTo(hullB));
    }

    private static Vector2i[] Corners(BlobTile tile)
    {
        var x = tile.Pos.X;
        var y = tile.Pos.Y;
        return new[]
        {
            new Vector2i(x, y),
            new Vector2i(x + 1, y),
            new Vector2i(x, y + 1),
            new Vector2i(x + 1, y + 1),
        };
    }

    private static bool IsConvexCcw(Vector2i[] hull)
    {
        for (var i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            var c = hull[(i + 2) % hull.Length];
            if (Cross(a, b, c) < 0)
                return false;
        }

        return true;
    }

    private static bool IsInsideOrOnHull(Vector2i[] hull, Vector2i point)
    {
        for (var i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            if (Cross(a, b, point) < 0)
                return false;
        }

        return true;
    }

    // Exact. The outline is integral, so these predicates need no epsilon, and dropping the
    // tolerance means a vertex sitting exactly on an edge is judged on the edge rather than
    // "close enough to it".
    private static long Cross(Vector2i o, Vector2i a, Vector2i b)
    {
        return (long) (a.X - o.X) * (b.Y - o.Y) - (long) (a.Y - o.Y) * (b.X - o.X);
    }
}
