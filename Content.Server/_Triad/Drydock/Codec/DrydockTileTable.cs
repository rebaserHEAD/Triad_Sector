using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// A grid's tiles as the image stores them, because the grid component's own chunk field is left
/// out of its row (<see cref="DrydockCodecFieldPass.FieldCase.GridChunks"/>): the engine's chunk
/// serializer numbers tiles through a tilemap only its own loader and saver build.
///
/// <para>The table is a tilemap of the image's own ids to tile definition names, and one entry per
/// chunk in the engine's version 7 chunk layout: for each row and then each column, an int32 id and
/// then the flags, variant and rotation-mirroring bytes, as base64
/// (<c>RobustToolbox/Robust.Shared/EntitySerialization/MapChunkSerializer.cs:75-92</c>). The layout
/// is the engine's so that its own chunk reader restores the tiles on load, with tile-change events
/// and collision regeneration suppressed while it does (<c>:46</c>, <c>:67</c>); the ids are ours,
/// so a stored ship never depends on the numbering of the server that stored it.</para>
///
/// <para>Id 0 is always the empty tile, which fills every cell of a chunk that holds no tile. Each
/// chunk entry is shaped as the engine's own (<c>ind</c>, <c>tiles</c>, <c>version</c>, and
/// <c>size</c>) and the entries are keyed by that index, as the grid component's chunk field is, so
/// a loader hands the entries and the tilemap to the engine unchanged.</para>
/// </summary>
public static class DrydockTileTable
{
    public const string SizeKey = "size";
    public const string TileMapKey = "tilemap";
    public const string ChunksKey = "chunks";

    /// <summary>The chunk layout version, as the engine's chunk entries carry it.</summary>
    public const string ChunkVersion = "7";

    /// <summary>Bytes per tile in the layout: the id, then flags, variant and rotation-mirroring.</summary>
    private const int TileBytes = 7;

    /// <param name="chunkSize">The grid's chunk size, which decides where each tile's chunk is.</param>
    /// <param name="tiles">The grid's non-empty tiles; an empty one is skipped rather than stored.</param>
    /// <param name="definitionName">This server's tile definition name for a tile type id.</param>
    public static MappingDataNode Write(
        ushort chunkSize,
        IEnumerable<(Vector2i Indices, Tile Tile)> tiles,
        Func<int, string> definitionName)
    {
        var ids = new Dictionary<int, int> { [Tile.Empty.TypeId] = 0 };
        var tileMap = new MappingDataNode { ["0"] = new ValueDataNode(definitionName(Tile.Empty.TypeId)) };
        var chunks = new SortedDictionary<Vector2i, Tile[]>(ChunkOrder.Instance);

        foreach (var (indices, tile) in tiles)
        {
            if (tile.IsEmpty)
                continue;

            if (!ids.ContainsKey(tile.TypeId))
            {
                var id = ids.Count;
                ids[tile.TypeId] = id;
                tileMap[id.ToString(CultureInfo.InvariantCulture)] = new ValueDataNode(definitionName(tile.TypeId));
            }

            var chunk = new Vector2i(FloorDiv(indices.X, chunkSize), FloorDiv(indices.Y, chunkSize));
            if (!chunks.TryGetValue(chunk, out var cells))
                chunks[chunk] = cells = new Tile[chunkSize * chunkSize];

            var x = indices.X - chunk.X * chunkSize;
            var y = indices.Y - chunk.Y * chunkSize;
            cells[y * chunkSize + x] = tile;
        }

        // Keyed by the chunk index, as the grid component's own chunk field is: it is a dictionary from index to
        // chunk, which the engine reads from a mapping and nothing else.
        var entries = new MappingDataNode();
        foreach (var (chunk, cells) in chunks)
        {
            var bytes = new byte[cells.Length * TileBytes];
            using (var writer = new BinaryWriter(new MemoryStream(bytes)))
            {
                // Row by row, as the reader takes them. An unset cell is the default Tile, which is
                // the empty one, id 0.
                foreach (var tile in cells)
                {
                    writer.Write(ids[tile.TypeId]);
                    writer.Write(tile.Flags);
                    writer.Write(tile.Variant);
                    writer.Write(tile.RotationMirroring);
                }
            }

            // Each entry carries its size, because the engine's reader takes it from the entry and
            // otherwise assumes 16 (MapChunkSerializer.cs:48-52).
            var ind = $"{chunk.X.ToString(CultureInfo.InvariantCulture)},{chunk.Y.ToString(CultureInfo.InvariantCulture)}";
            entries[ind] = new MappingDataNode
            {
                ["ind"] = new ValueDataNode(ind),
                ["tiles"] = new ValueDataNode(Convert.ToBase64String(bytes)),
                ["version"] = new ValueDataNode(ChunkVersion),
                [SizeKey] = new ValueDataNode(chunkSize.ToString(CultureInfo.InvariantCulture)),
            };
        }

        return new MappingDataNode
        {
            [SizeKey] = new ValueDataNode(chunkSize.ToString(CultureInfo.InvariantCulture)),
            [TileMapKey] = tileMap,
            [ChunksKey] = entries,
        };
    }

    /// <summary>
    /// The non-empty tiles a table holds, with each id resolved to this server's tile type. The
    /// engine's reader is what restores a grid; this is the image's own reading of the same bytes,
    /// for checking a restore against what was stored.
    /// </summary>
    /// <exception cref="FormatException">The table is not one <see cref="Write"/> produced.</exception>
    public static List<(Vector2i Indices, Tile Tile)> Read(MappingDataNode table, Func<string, int> typeId)
    {
        var chunkSize = ushort.Parse(Scalar(table, SizeKey), CultureInfo.InvariantCulture);

        if (!table.TryGet<MappingDataNode>(TileMapKey, out var tileMap)
            || !table.TryGet<MappingDataNode>(ChunksKey, out var chunks))
        {
            throw new FormatException("Drydock tile table: the tilemap or the chunks are missing.");
        }

        var types = new Dictionary<int, int>();
        foreach (var (key, name) in tileMap)
        {
            if (name is not ValueDataNode { Value: var definition })
                throw new FormatException($"Drydock tile table: tilemap entry {key} is not a name.");

            types[int.Parse(key, CultureInfo.InvariantCulture)] = typeId(definition);
        }

        var tiles = new List<(Vector2i, Tile)>();
        foreach (var (key, node) in chunks)
        {
            if (node is not MappingDataNode entry)
                throw new FormatException("Drydock tile table: a chunk entry is not a mapping.");

            if (Scalar(entry, "ind") != key)
                throw new FormatException($"Drydock tile table: chunk {key} carries the index {Scalar(entry, "ind")}.");

            if (Scalar(entry, "version") != ChunkVersion)
                throw new FormatException($"Drydock tile table: a chunk is version {Scalar(entry, "version")}, not {ChunkVersion}.");

            var ind = Scalar(entry, "ind").Split(',');
            var chunk = new Vector2i(int.Parse(ind[0], CultureInfo.InvariantCulture), int.Parse(ind[1], CultureInfo.InvariantCulture));
            var bytes = Convert.FromBase64String(Scalar(entry, "tiles"));

            if (bytes.Length != chunkSize * chunkSize * TileBytes)
                throw new FormatException($"Drydock tile table: chunk {chunk} holds {bytes.Length} bytes, not {chunkSize * chunkSize * TileBytes}.");

            using var reader = new BinaryReader(new MemoryStream(bytes));
            for (var y = 0; y < chunkSize; y++)
            {
                for (var x = 0; x < chunkSize; x++)
                {
                    var id = reader.ReadInt32();
                    var flags = reader.ReadByte();
                    var variant = reader.ReadByte();
                    var rotationMirroring = reader.ReadByte();

                    if (!types.TryGetValue(id, out var type))
                        throw new FormatException($"Drydock tile table: chunk {chunk} names tile id {id}, which its tilemap does not.");

                    if (type == Tile.Empty.TypeId)
                        continue;

                    tiles.Add((new Vector2i(chunk.X * chunkSize + x, chunk.Y * chunkSize + y), new Tile(type, flags, variant, rotationMirroring)));
                }
            }
        }

        return tiles;
    }

    private static string Scalar(MappingDataNode mapping, string key) =>
        mapping.TryGet<ValueDataNode>(key, out var value)
            ? value.Value
            : throw new FormatException($"Drydock tile table: '{key}' is missing.");

    /// <summary>Division that rounds toward negative infinity, so tile -1 is in chunk -1 rather than 0.</summary>
    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

    /// <summary>Chunks in a fixed order, so the same grid always writes the same table.</summary>
    private sealed class ChunkOrder : IComparer<Vector2i>
    {
        public static readonly ChunkOrder Instance = new();

        public int Compare(Vector2i a, Vector2i b) =>
            a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
    }
}
