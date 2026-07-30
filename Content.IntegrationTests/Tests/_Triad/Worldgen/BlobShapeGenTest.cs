using System.Numerics;
using Content.Server._Triad.Worldgen.Cells;

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

        Assert.That(hull.Length, Is.LessThanOrEqualTo(16));
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

    private static Vector2[] Corners(BlobTile tile)
    {
        var x = tile.Pos.X;
        var y = tile.Pos.Y;
        return new[]
        {
            new Vector2(x, y),
            new Vector2(x + 1, y),
            new Vector2(x, y + 1),
            new Vector2(x + 1, y + 1),
        };
    }

    private static bool IsConvexCcw(Vector2[] hull)
    {
        for (var i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            var c = hull[(i + 2) % hull.Length];
            if (Cross(a, b, c) < -1e-6)
                return false;
        }

        return true;
    }

    private static bool IsInsideOrOnHull(Vector2[] hull, Vector2 point)
    {
        for (var i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            if (Cross(a, b, point) < -1e-6)
                return false;
        }

        return true;
    }

    private static double Cross(Vector2 o, Vector2 a, Vector2 b)
    {
        return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }
}
