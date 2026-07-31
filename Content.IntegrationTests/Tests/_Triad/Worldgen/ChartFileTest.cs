// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._Triad.Worldgen;
using Content.Shared._Triad.Worldgen;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

/// <summary>
///     The reconnect chart file's contract: what was written comes back exactly, the wrong
///     round comes back not at all, and corruption degrades to skipped lines rather than a
///     throw. The file is a private cache, so "discard and move on" is the only acceptable
///     failure mode.
/// </summary>
[TestFixture]
[TestOf(typeof(ChartFile))]
public sealed class ChartFileTest
{
    private static readonly SensedProtoRecipe RecipeA = new("TriadRockSmall", 6f, 14, 0.5f, 1);
    private static readonly SensedProtoRecipe RecipeB = new("TriadRockLarge", 26f, 324, 0.9f, 8);

    private static List<(MapId, IEnumerable<ChartEntry>)> SampleMaps()
    {
        return new List<(MapId, IEnumerable<ChartEntry>)>
        {
            (new MapId(3), new[]
            {
                new ChartEntry(1, 0, new Vector2(-1523.25f, 88.0625f), -123456789, RecipeA),
                new ChartEntry(901, 2, new Vector2(0.1f, -0.1f), 42, RecipeB),
            }),
            (new MapId(7), new[]
            {
                new ChartEntry(17, 0, new Vector2(2048f, 2048f), int.MinValue, RecipeA),
            }),
        };
    }

    [Test]
    public void RoundTripsExactly()
    {
        var text = ChartFile.Serialize(41, SampleMaps());

        Assert.That(ChartFile.TryDeserialize(text, 41, out var maps), Is.True);
        Assert.That(maps, Has.Count.EqualTo(2));

        var byMap = maps.ToDictionary(m => m.Map, m => m.Entries);
        Assert.Multiple(() =>
        {
            Assert.That(byMap[new MapId(3)], Is.EqualTo(SampleMaps()[0].Item2.ToList()));
            Assert.That(byMap[new MapId(7)], Is.EqualTo(SampleMaps()[1].Item2.ToList()));
        });
    }

    [Test]
    public void WrongRoundDiscardsTheWholeFile()
    {
        var text = ChartFile.Serialize(41, SampleMaps());

        Assert.That(ChartFile.TryDeserialize(text, 42, out _), Is.False,
            "a chart from another round merged; charts are round-scoped and must never cross");
    }

    [Test]
    public void GarbageIsRejectedAndCorruptLinesAreSkipped()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChartFile.TryDeserialize("", 1, out _), Is.False);
            Assert.That(ChartFile.TryDeserialize("not a chart\nround 1\n", 1, out _), Is.False);
            Assert.That(ChartFile.TryDeserialize("triadchart 999\nround 1\n", 1, out _), Is.False,
                "an unknown format version parsed; forward compatibility means discard, not guess");
        });

        // One mangled contact line in an otherwise good file costs that line only.
        var text = ChartFile.Serialize(5, SampleMaps());
        var lines = text.Split('\n');
        lines[3] = "c 901 not-a-number oops";
        var corrupted = string.Join('\n', lines);

        Assert.That(ChartFile.TryDeserialize(corrupted, 5, out var maps), Is.True);
        Assert.That(maps.SelectMany(m => m.Entries).Count(), Is.EqualTo(2),
            "expected exactly the two intact entries to survive the corrupted line");
    }
}
