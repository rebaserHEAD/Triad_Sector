// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Shared._Triad.Worldgen;

/// <summary>
///     Traces the true silhouette of a rolled tile set: the polygon that follows the actual
///     boundary of the rock rather than a convex bound around it.
///
///     Why this exists. <see cref="BlobShapeGen.Roll"/> picks uniformly from the whole frontier
///     each step rather than adjacent to the last tile, so blobs grow lobes with bays between
///     them and are structurally concave, not incidentally so. A convex hull bridges every bay by
///     definition, which measured out at roughly 40 to 58 percent more area painted than the rock
///     occupies. The fork's requirement is faithful shapes, not blips, and a 16-gon around a
///     concave rock is closer to a blip than to the rock.
///
///     The safety property, which matters more than the fidelity one: the outline never crosses
///     into a solid tile. Every emitted segment is a run of unit edges of the traced set, all of
///     which lie on lines x = n or y = n. Tile interiors are the open squares strictly between
///     those lines. The intersection is empty, so the outline cannot enter a tile. That holds by
///     construction rather than by tolerance, which is why there is no epsilon anywhere here.
/// </summary>
public static class TileOutline
{
    /// <summary>
    ///     Cardinal headings in counter-clockwise order, so rotating by +1 is a left turn.
    /// </summary>
    private static readonly Vector2i[] Step = { new(1, 0), new(0, 1), new(-1, 0), new(0, -1) };

    /// <summary>
    ///     Sanity valve, not a simplification ladder. The measured worst case over 15,500 rolls
    ///     across every shipping prototype is 252 vertices, so nothing in the game reaches this.
    ///     It exists so a shape nobody has produced yet cannot put an unbounded array on the wire:
    ///     past this, the convex hull is used instead. Raise it if real content ever grows past it;
    ///     do not lower it below the measured worst case without re-measuring.
    /// </summary>
    public const int MaxOutlineVerts = 512;

    private static int Left(int d) => (d + 1) & 3;
    private static int Right(int d) => (d + 3) & 3;

    /// <summary>
    ///     Converts a heading into the half-step that names a cell rather than a vertex. Cells are
    ///     named by their lower-left corner, so heading in a negative direction has to step back to
    ///     reach the cell that lies that way.
    /// </summary>
    private static Vector2i Corner(int d) => d switch
    {
        0 => new Vector2i(0, 0),   // +X
        1 => new Vector2i(0, 0),   // +Y
        2 => new Vector2i(-1, 0),  // -X
        _ => new Vector2i(0, -1),  // -Y
    };

    private static Vector2i FrontLeft(Vector2i v, int d) => v + Corner(d) + Corner(Left(d));

    private static Vector2i FrontRight(Vector2i v, int d) => v + Corner(d) + Corner(Right(d));

    /// <summary>
    ///     The outer silhouette of <paramref name="tiles"/>, counter-clockwise, in grid-local tile
    ///     units. Returns null when the tile set is empty or the outline exceeds
    ///     <see cref="MaxOutlineVerts"/>, in which case the caller should fall back to the hull.
    /// </summary>
    public static Vector2i[]? Trace(IReadOnlyList<BlobTile> tiles)
    {
        if (tiles.Count == 0)
            return null;

        var solid = new HashSet<Vector2i>(tiles.Count);

        // Bounds of the ORIGINAL tiles. FillEnclosed only ever adds cells strictly inside this
        // box, so it stays correct for the fill and is never used to pick the start vertex.
        var min = new Vector2i(int.MaxValue, int.MaxValue);
        var max = new Vector2i(int.MinValue, int.MinValue);

        foreach (var tile in tiles)
        {
            solid.Add(tile.Pos);
            min = new Vector2i(Math.Min(min.X, tile.Pos.X), Math.Min(min.Y, tile.Pos.Y));
            max = new Vector2i(Math.Max(max.X, tile.Pos.X), Math.Max(max.Y, tile.Pos.Y));
        }

        FillEnclosed(solid, min, max);

        // The lexicographically smallest tile is on the outer boundary of its component by
        // construction: both the cell west of it and the cell south of it sort strictly before it,
        // so neither is solid, so its south edge is a boundary edge and its south-west corner is a
        // convex corner. Starting there means the walk needs no closing fixup, can never begin on
        // a hole, and does not depend on set iteration order, which is the determinism requirement
        // since describe and materialize must agree exactly.
        var start = default(Vector2i);
        var haveStart = false;

        foreach (var cell in solid)
        {
            if (haveStart && !(cell.X < start.X || (cell.X == start.X && cell.Y < start.Y)))
                continue;

            start = cell;
            haveStart = true;
        }

        if (!haveStart)
            return null;

        var verts = WalkLoop(solid, start);
        return verts is null || verts.Length > MaxOutlineVerts ? null : verts;
    }

    /// <summary>
    ///     Fills empty cells that the outside cannot reach, so the trace produces one outer loop
    ///     rather than an outer loop plus a hole loop per pocket. Filling only ever adds area, so
    ///     it errs toward over-reporting the rock, which is the safe direction.
    /// </summary>
    private static void FillEnclosed(HashSet<Vector2i> solid, Vector2i min, Vector2i max)
    {
        // Seed the flood OUTSIDE a padded box, never inside the tight one. The tight bounding box
        // touches the shape on all four sides by definition; each touch pinches the gap between
        // shape and box edge shut, which cuts the in-box exterior into four disconnected corner
        // pockets. A seed inside the box reaches one of them, so the other three read as enclosed
        // and get filled: a plus pentomino comes out as a solid 3x3 block. The padded ring is
        // empty by construction and 4-connected around the whole shape, so one seed on it reaches
        // every pocket that touches the box.
        var lo = min - new Vector2i(1, 1);
        var hi = max + new Vector2i(1, 1);

        var reached = new HashSet<Vector2i>();
        var pending = new Stack<Vector2i>();

        reached.Add(lo);
        pending.Push(lo);

        while (pending.Count > 0)
        {
            var cell = pending.Pop();

            for (var d = 0; d < 4; d++)
            {
                var next = cell + Step[d];

                if (next.X < lo.X || next.X > hi.X || next.Y < lo.Y || next.Y > hi.Y)
                    continue;

                if (solid.Contains(next) || !reached.Add(next))
                    continue;

                pending.Push(next);
            }
        }

        for (var x = min.X; x <= max.X; x++)
        {
            for (var y = min.Y; y <= max.Y; y++)
            {
                var cell = new Vector2i(x, y);
                if (!solid.Contains(cell) && !reached.Contains(cell))
                    solid.Add(cell);
            }
        }
    }

    /// <summary>
    ///     Walks the boundary with the rock always on the left, emitting a vertex only on a turn.
    ///     Emitting on turns is what merges collinear runs, so the raw staircase never exists.
    /// </summary>
    private static Vector2i[]? WalkLoop(HashSet<Vector2i> solid, Vector2i startTile)
    {
        var startVert = startTile;
        const int startDir = 0; // +X along the start tile's south edge

        var v = startVert;
        var d = startDir;

        var used = new HashSet<(Vector2i Vert, int Dir)>();
        var verts = new List<Vector2i>();

        while (true)
        {
            // One boundary edge consumed per iteration, and a tile contributes at most four, so
            // this terminates after at most 4 * tiles steps whatever the geometry looks like.
            if (!used.Add((v, d)))
                break;

            v += Step[d];

            // Arriving along a boundary edge means behind-left is always solid and behind-right
            // always empty, so only the two cells ahead decide the turn.
            var fl = solid.Contains(FrontLeft(v, d));
            var fr = solid.Contains(FrontRight(v, d));

            int next;
            if (!fl && !fr)
                next = Left(d);        // walked off the end of a spur
            else if (fl && !fr)
                next = d;              // boundary continues along the same grid line
            else if (fl)               // fl && fr
                next = Right(d);       // the edge ahead is interior, wrap around
            else                       // !fl && fr: diagonal pinch, the only ambiguous case
                next = used.Contains((v, Left(d))) ? Right(d) : Left(d);

            if (next != d)
                verts.Add(v);

            d = next;

            if (v == startVert && d == startDir)
                break;

            if (verts.Count > MaxOutlineVerts)
                return null;
        }

        return verts.Count < 3 ? null : verts.ToArray();
    }
}
