#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server._Triad.Drydock.Codec;
using NUnit.Framework;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Tests.Server._Triad.Drydock;

/// <summary>
/// The tile table on hand-built tiles. The byte layout is checked against the engine's reader by
/// offset, because the table exists to be read by that reader
/// (<c>RobustToolbox/Robust.Shared/EntitySerialization/MapChunkSerializer.cs:75-92</c>), which is
/// internal and needs the engine's own loader as its context, so a unit test cannot call it.
/// </summary>
[TestFixture, TestOf(typeof(DrydockTileTable))]
[Parallelizable(ParallelScope.All)]
public sealed class DrydockTileTableTest
{
    private const ushort Size = 16;

    // This server's numbering: type ids deliberately far from the table's own ids.
    private static readonly Dictionary<int, string> Names = new()
    {
        [0] = "Space",
        [40] = "FloorSteel",
        [77] = "Lattice",
    };

    private static string Name(int type) => Names[type];

    private static int Type(string name) => Names.Single(entry => entry.Value == name).Key;

    [Test]
    public void TilesRoundTripAcrossChunksAndNegativeIndices()
    {
        var tiles = new List<(Vector2i, Tile)>
        {
            (new Vector2i(0, 0), new Tile(40)),
            (new Vector2i(15, 15), new Tile(40, flags: 2, variant: 3, rotationMirroring: 5)),
            (new Vector2i(16, 0), new Tile(77)),
            (new Vector2i(-1, -1), new Tile(77, variant: 1)),
            (new Vector2i(-16, 3), new Tile(40)),
            (new Vector2i(-17, 3), new Tile(40)),
        };

        var table = DrydockTileTable.Write(Size, tiles, Name);
        var read = DrydockTileTable.Read(table, Type);

        Assert.That(read, Is.EquivalentTo(tiles));

        // Chunks (0,0) twice, (1,0), (-1,-1), (-1,0) for (-16,3) and (-2,0) for (-17,3): five, with
        // the last two a tile apart across a chunk edge.
        Assert.That(table.Get<MappingDataNode>(DrydockTileTable.ChunksKey), Has.Count.EqualTo(5));
    }

    [Test]
    public void AnEmptyTileIsNotStored()
    {
        var table = DrydockTileTable.Write(Size, new[] { (new Vector2i(3, 3), Tile.Empty) }, Name);

        Assert.Multiple(() =>
        {
            Assert.That(table.Get<MappingDataNode>(DrydockTileTable.ChunksKey), Is.Empty);
            Assert.That(DrydockTileTable.Read(table, Type), Is.Empty);
        });
    }

    [Test]
    public void TheTableUsesItsOwnIdsWithTheEmptyTileAtZero()
    {
        var table = DrydockTileTable.Write(Size, new[] { (new Vector2i(0, 0), new Tile(77)), (new Vector2i(1, 0), new Tile(40)) }, Name);
        var tileMap = table.Get<MappingDataNode>(DrydockTileTable.TileMapKey);

        Assert.That(tileMap.ToDictionary(entry => entry.Key, entry => ((ValueDataNode) entry.Value).Value),
            Is.EquivalentTo(new Dictionary<string, string> { ["0"] = "Space", ["1"] = "Lattice", ["2"] = "FloorSteel" }),
            "Ids in order of first appearance, after the empty tile, not this server's type ids.");
    }

    /// <summary>
    /// The layout the engine's reader takes: rows outer, columns inner, seven bytes a cell, an
    /// int32 id first. A tile at (x, y) within its chunk starts at byte (y * size + x) * 7.
    /// </summary>
    [Test]
    public void TheChunkBytesAreTheEngineLayout()
    {
        var tile = new Tile(40, flags: 9, variant: 6, rotationMirroring: 4);
        var table = DrydockTileTable.Write(Size, new[] { (new Vector2i(-14, -13), tile) }, Name);
        (var key, var node) = table.Get<MappingDataNode>(DrydockTileTable.ChunksKey).Single();
        var chunk = (MappingDataNode) node;
        var bytes = Convert.FromBase64String(chunk.Get<ValueDataNode>("tiles").Value);

        // (-14, -13) is in chunk (-1, -1) at (2, 3) within it.
        var at = (3 * Size + 2) * 7;

        Assert.Multiple(() =>
        {
            Assert.That(key, Is.EqualTo("-1,-1"), "keyed by its index, as the grid component's chunk field is");
            Assert.That(chunk.Get<ValueDataNode>("ind").Value, Is.EqualTo("-1,-1"));
            Assert.That(chunk.Get<ValueDataNode>("version").Value, Is.EqualTo("7"));
            Assert.That(chunk.Get<ValueDataNode>("size").Value, Is.EqualTo("16"));
            Assert.That(bytes, Has.Length.EqualTo(Size * Size * 7));

            Assert.That(BitConverter.ToInt32(bytes, at), Is.EqualTo(1), "the table's id, not the server's type id");
            Assert.That(bytes[at + 4], Is.EqualTo(9), "flags");
            Assert.That(bytes[at + 5], Is.EqualTo(6), "variant");
            Assert.That(bytes[at + 6], Is.EqualTo(4), "rotation-mirroring");

            Assert.That(bytes.Where((_, i) => i < at || i >= at + 7), Has.All.EqualTo(0),
                "Every other cell is the empty tile, id 0 with nothing set.");
        });
    }

    [Test]
    public void AChunkNamingAnIdItsTilemapLacksIsRefused()
    {
        var table = DrydockTileTable.Write(Size, new[] { (new Vector2i(0, 0), new Tile(40)) }, Name);
        table.Get<MappingDataNode>(DrydockTileTable.TileMapKey).Remove("1");

        Assert.Throws<FormatException>(() => DrydockTileTable.Read(table, Type));
    }
}
