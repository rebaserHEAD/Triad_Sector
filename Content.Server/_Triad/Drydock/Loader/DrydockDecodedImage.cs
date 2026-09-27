using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Content.Server._Triad.Drydock.Codec;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// An image's tile table and rows decoded from their JSON text into the nodes a load reads, the rows by entity id and
/// row name. Decoding reads nothing but the image, so it runs on any thread: <see cref="DrydockNodeJson"/> is static
/// with no state, and a node's constructor only stores what it is given.
/// </summary>
public sealed class DrydockDecodedImage
{
    public required MappingDataNode Tiles { get; init; }

    public required Dictionary<long, Dictionary<string, MappingDataNode>> Rows { get; init; }

    /// <summary>
    /// Every row and the tile table, decoded. A value that is not the image's encoding throws
    /// <see cref="DrydockDecodeException"/> naming the entity and the row it was in.
    /// </summary>
    public static DrydockDecodedImage Decode(DrydockImage image)
    {
        var tiles = DecodeMapping(image.Tiles, null, "tile table");
        var rows = new Dictionary<long, Dictionary<string, MappingDataNode>>(image.Entities.Count);
        foreach (var entity in image.Entities)
        {
            var decoded = new Dictionary<string, MappingDataNode>(entity.Rows.Count);
            foreach (var (name, text) in entity.Rows)
                decoded.Add(name, DecodeMapping(text, entity.Id, name));

            rows.Add(entity.Id, decoded);
        }

        return new DrydockDecodedImage { Tiles = tiles, Rows = rows };
    }

    private static MappingDataNode DecodeMapping(string text, long? entity, string row)
    {
        try
        {
            return DrydockNodeJson.Decode(JsonNode.Parse(text)) as MappingDataNode
                   ?? throw new FormatException("the value is not a mapping");
        }
        catch (Exception e)
        {
            throw new DrydockDecodeException(entity, row, e);
        }
    }
}

/// <summary>
/// A stored value that is not the image's encoding, with the entity it belongs to (null for the tile table) and its row.
/// The message is the decode's own.
/// </summary>
public sealed class DrydockDecodeException(long? entity, string row, Exception inner) : Exception(inner.Message, inner)
{
    public long? Entity { get; } = entity;

    public string Row { get; } = row;
}
