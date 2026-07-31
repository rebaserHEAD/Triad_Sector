// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using Content.Server._Triad.Worldgen.Cells;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

/// <summary>
///     The outline has two properties and they need testing in opposite directions.
///
///     Safety: the outline never crosses into a solid tile. A test for this alone is one-sided and
///     nearly worthless, because the bounding rectangle passes it. Every shape contains itself.
///
///     Fidelity: the outline encloses nothing but the rock. This is the direction that catches the
///     failure the convex hull has, and the failure a flood fill seeded inside the bounding box
///     has. Both of those over-report, so both survive the safety test and die on this one.
/// </summary>
[TestFixture]
[TestOf(typeof(TileOutline))]
public sealed class TileOutlineTest
{
    private static List<BlobTile> Tiles(params (int X, int Y)[] cells)
        => cells.Select(c => new BlobTile(new Vector2i(c.X, c.Y), 0)).ToList();

    /// <summary>Twice the signed area. Integral input, so this is exact.</summary>
    private static long DoubleArea(Vector2i[] poly)
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
    private static long EnclosedCells(Vector2i[] poly) => System.Math.Abs(DoubleArea(poly)) / 2;

    [Test]
    public void SingleTileIsItsOwnSquare()
    {
        var outline = TileOutline.Trace(Tiles((0, 0)));

        Assert.That(outline, Is.Not.Null);
        Assert.That(outline!.Length, Is.EqualTo(4), "a single tile is a square, so four corners");
        Assert.That(EnclosedCells(outline), Is.EqualTo(1));
    }

    /// <summary>
    ///     The plus pentomino is the counterexample that killed the first draft of this algorithm.
    ///     Its bounding box is 3x3 and every corner cell of that box is empty. A flood fill seeded
    ///     at a corner of the tight box reaches only the pocket it started in, so the other three
    ///     corners read as enclosed and get filled, and the plus comes out as a solid block. The
    ///     fill has to be seeded outside a padded box for this to hold.
    /// </summary>
    [Test]
    public void PlusPentominoDoesNotBecomeABlock()
    {
        var outline = TileOutline.Trace(Tiles((1, -1), (0, 0), (1, 0), (2, 0), (1, 1)));

        Assert.That(outline, Is.Not.Null);
        Assert.That(EnclosedCells(outline!), Is.EqualTo(5),
            "the outline paved over the corner pockets: a plus enclosing 9 cells is the bounding box, not the rock");
        Assert.That(outline!.Length, Is.EqualTo(12),
            "a plus has twelve corners, eight convex and four reflex");
    }

    /// <summary>The same failure with one fewer tile, and no empty corner cell needed.</summary>
    [Test]
    public void TTetrominoDoesNotBecomeARectangle()
    {
        var outline = TileOutline.Trace(Tiles((0, 0), (1, 0), (2, 0), (1, 1)));

        Assert.That(outline, Is.Not.Null);
        Assert.That(EnclosedCells(outline!), Is.EqualTo(4),
            "enclosing 6 cells means the outline is the 3x2 bounding box and the shape is gone");
    }

    /// <summary>
    ///     A bay one tile wide, open to the outside. It must stay open: this is the exact feature
    ///     the convex hull swallows, and 63 percent of the largest rolls have one at 25+ tiles.
    /// </summary>
    [Test]
    public void OneTileBayStaysOpen()
    {
        var outline = TileOutline.Trace(Tiles((0, 0), (1, 0), (2, 0), (0, 1), (2, 1)));

        Assert.That(outline, Is.Not.Null);
        Assert.That(EnclosedCells(outline!), Is.EqualTo(5),
            "the bay was bridged; a bay open to space is not enclosed and must not be filled");
        Assert.That(outline!.Length, Is.EqualTo(8),
            "walking down into the bay and back out is eight corners");
    }

    /// <summary>
    ///     The converse case, and the reason the fill exists at all. A void with no path to the
    ///     outside is interior rock as far as radar is concerned, and emitting a separate loop for
    ///     each one-tile pocket was measured at a 64 percent vertex increase for no fidelity worth
    ///     having.
    /// </summary>
    [Test]
    public void FullyEnclosedVoidIsFilled()
    {
        var ring = Tiles(
            (0, 0), (1, 0), (2, 0),
            (0, 1), /* hole */ (2, 1),
            (0, 2), (1, 2), (2, 2));

        var outline = TileOutline.Trace(ring);

        Assert.That(outline, Is.Not.Null);
        Assert.That(EnclosedCells(outline!), Is.EqualTo(9),
            "an enclosed void must be filled, giving one outer loop rather than a loop plus a hole");
        Assert.That(outline!.Length, Is.EqualTo(4), "the filled ring is a 3x3 square");
    }

    /// <summary>
    ///     The safety property, stated structurally rather than by sampling. Every edge is axis
    ///     aligned and sits on an integer grid line, and tile interiors are the open squares
    ///     strictly between those lines, so the outline cannot pass through one.
    /// </summary>
    [Test]
    public void EveryEdgeLiesOnAGridLine()
    {
        var tiles = BlobShapeGen.Roll(new System.Random(1234), 12f, 120, 0.5f, 1);
        var outline = TileOutline.Trace(tiles);

        Assert.That(outline, Is.Not.Null);

        for (var i = 0; i < outline!.Length; i++)
        {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Length];

            Assert.That(a.X == b.X || a.Y == b.Y, Is.True,
                $"edge {a} -> {b} is diagonal, so it can cut through tile interiors");
            Assert.That(a, Is.Not.EqualTo(b), "zero-length edge");
        }
    }

    /// <summary>
    ///     Both directions at once over a real roll: the outline encloses every tile, and encloses
    ///     nothing that is not a tile or a void the outside cannot reach. Either half alone lets a
    ///     wrong answer through.
    /// </summary>
    [Test]
    public void OutlineEnclosesTheRockAndNothingElse()
    {
        for (var seed = 1; seed <= 25; seed++)
        {
            var tiles = BlobShapeGen.Roll(new System.Random(seed), 14f, 160, 0.5f, 1);
            var outline = TileOutline.Trace(tiles);
            Assert.That(outline, Is.Not.Null, $"seed {seed} produced no outline");

            var solid = tiles.Select(t => t.Pos).ToHashSet();
            var expected = solid.Count + CountEnclosedVoids(solid);

            Assert.That(EnclosedCells(outline!), Is.EqualTo((long) expected),
                $"seed {seed}: outline encloses {EnclosedCells(outline!)} cells against {expected} of real rock");
        }
    }

    [Test]
    public void TraceIsDeterministicForTheSameTiles()
    {
        var a = BlobShapeGen.Roll(new System.Random(99), 14f, 160, 0.5f, 1);
        var b = BlobShapeGen.Roll(new System.Random(99), 14f, 160, 0.5f, 1);

        Assert.That(TileOutline.Trace(a), Is.EqualTo(TileOutline.Trace(b)),
            "describe and materialize both trace their own roll, so identical tiles must give identical outlines");
    }

    /// <summary>
    ///     Independent reimplementation of the fill, deliberately not sharing code with the one
    ///     under test: flood from outside a padded box and count in-box empties never reached.
    /// </summary>
    private static int CountEnclosedVoids(HashSet<Vector2i> solid)
    {
        var min = new Vector2i(solid.Min(c => c.X), solid.Min(c => c.Y));
        var max = new Vector2i(solid.Max(c => c.X), solid.Max(c => c.Y));
        var lo = min - new Vector2i(1, 1);
        var hi = max + new Vector2i(1, 1);

        var seen = new HashSet<Vector2i> { lo };
        var queue = new Queue<Vector2i>();
        queue.Enqueue(lo);

        var steps = new[] { new Vector2i(1, 0), new Vector2i(-1, 0), new Vector2i(0, 1), new Vector2i(0, -1) };

        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var step in steps)
            {
                var next = cell + step;
                if (next.X < lo.X || next.X > hi.X || next.Y < lo.Y || next.Y > hi.Y)
                    continue;
                if (solid.Contains(next) || !seen.Add(next))
                    continue;
                queue.Enqueue(next);
            }
        }

        var enclosed = 0;
        for (var x = min.X; x <= max.X; x++)
        {
            for (var y = min.Y; y <= max.Y; y++)
            {
                var cell = new Vector2i(x, y);
                if (!solid.Contains(cell) && !seen.Contains(cell))
                    enclosed++;
            }
        }

        return enclosed;
    }
}
