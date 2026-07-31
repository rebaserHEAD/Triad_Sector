// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Content.Shared._Triad.Worldgen;
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

    /// <summary>
    ///     The walk's real positional bound is radius + 1, not radius. Candidates are radius-checked
    ///     as they are added, but the blobDrawProb branch places a picked tile's neighbours straight
    ///     through PlaceTile with no check of their own, so a tile sitting on the radius can push one
    ///     exactly one step past it. It cannot cascade: those neighbours only ever contribute
    ///     candidates that are themselves radius-checked.
    ///
    ///     This asserted radius flat until 2026-07-30, which is a bound the generator does not hold.
    ///     It passed only because the single seed it used never drew a boundary neighbour, so it was
    ///     a latent failure waiting on any change to draw order rather than a guarantee. Swept across
    ///     seeds now, because a property exercised by one seed is a coincidence.
    /// </summary>
    [Test]
    public void AllPositions_AreWithinRadiusPlusOneOfOrigin()
    {
        var boundSq = (double) (Radius + 1) * (Radius + 1);

        for (var seed = 1; seed <= 64; seed++)
        {
            var tiles = BlobShapeGen.Roll(new System.Random(seed), Radius, Placements, DrawProb, TilesetCount);

            foreach (var tile in tiles)
            {
                var distSq = (double) tile.Pos.X * tile.Pos.X + (double) tile.Pos.Y * tile.Pos.Y;
                Assert.That(distSq, Is.LessThanOrEqualTo(boundSq),
                    $"seed {seed} placed a tile at {tile.Pos}, past radius + 1");
            }
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

}
