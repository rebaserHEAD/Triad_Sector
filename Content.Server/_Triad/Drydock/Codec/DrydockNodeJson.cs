using System;
using System.Linq;
using System.Text.Json.Nodes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// One <see cref="DataNode"/> in, one <see cref="JsonNode"/> out, and back again. Everything the
/// codec writes reaches a row through here, so this is the encoding the image is stored in.
///
/// <para>A scalar is always a JSON string, so a number, a bool and the text that spells them stay
/// distinguishable; only a null scalar is JSON null. <see cref="ValueDataNode"/> carries
/// <see cref="DataNode.IsNull"/> as its own flag, computed from the literal, the whitespace and the
/// tag rather than from the text
/// (<c>RobustToolbox/Robust.Shared/Serialization/Markdown/Value/ValueDataNode.cs:36-39</c>), so a
/// null and the string <c>"null"</c> are different nodes and stay different rows.</para>
///
/// <para>A mapping needs no encoding for a non-string key because one cannot exist:
/// <see cref="MappingDataNode"/> is <c>IDictionary&lt;string, DataNode&gt;</c> and its YAML
/// constructor throws on a non-scalar key
/// (<c>RobustToolbox/Robust.Shared/Serialization/Markdown/Mapping/MappingDataNode.cs:15</c>,
/// <c>:57-58</c>). Key order is not carried and does not need to be: it was measured at
/// <c>bd884504e0</c>, where no key changes content when every mapping is re-keyed into PostgreSQL's
/// own order, which is why every comparison of stored rows is order-insensitive at the mapping
/// level.</para>
///
/// <para><see cref="TagKey"/> and <see cref="NodeKey"/> are the reserved members, because
/// <see cref="DataNode.Tag"/> lives on the base
/// (<c>RobustToolbox/Robust.Shared/Serialization/Markdown/DataNode.cs:11</c>) and a mapping and a
/// sequence can carry one as well as a scalar. A plain mapping whose keys happened to be those two
/// would read back as a tagged node with nothing in the encoding to tell them apart, so a mapping
/// carrying <see cref="TagKey"/> throws on the way out: the failure lands at write time on a
/// developer's machine rather than as a misread years later. <see cref="NodeKey"/> alone is not a
/// collision, because a mapping without <see cref="TagKey"/> encodes and reads back as itself.</para>
/// </summary>
public static class DrydockNodeJson
{
    /// <summary>The tag of a tagged node. A mapping carrying this key cannot be encoded.</summary>
    public const string TagKey = "$tag";

    /// <summary>The tagged node itself, encoded as an untagged node of its own kind.</summary>
    public const string NodeKey = "$node";

    /// <summary>
    /// The node as it will be stored. Null for a null scalar, which is what a member of a
    /// <see cref="JsonObject"/> or a <see cref="JsonArray"/> holds for JSON null.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A mapping at any depth carries <see cref="TagKey"/>, including the inner mapping of a tagged
    /// node, so the tree has no faithful encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">A node kind the image does not encode.</exception>
    public static JsonNode? Encode(DataNode node)
    {
        var encoded = EncodeUntagged(node);

        return node.Tag is { } tag
            ? new JsonObject { [TagKey] = JsonValue.Create(tag), [NodeKey] = encoded }
            : encoded;
    }

    /// <summary>
    /// The node a stored value describes.
    /// </summary>
    /// <exception cref="FormatException">
    /// The stored value is not something <see cref="Encode"/> produces: a scalar that is not a
    /// string, or a malformed tagged node. Corruption is not data, and a decode that quietly makes
    /// something of it restores a ship with holes in it.
    /// </exception>
    public static DataNode Decode(JsonNode? json)
    {
        if (json is not JsonObject mapping || !mapping.ContainsKey(TagKey))
            return DecodeUntagged(json);

        if (mapping.Count != 2 || !mapping.ContainsKey(NodeKey))
        {
            throw new FormatException(
                $"Drydock codec: a tagged node carried the members [{string.Join(", ", mapping.Select(pair => pair.Key))}], rather than exactly '{TagKey}' and '{NodeKey}'.");
        }

        if (mapping[TagKey] is not JsonValue tagValue || !tagValue.TryGetValue<string>(out var tag))
            throw new FormatException($"Drydock codec: a tagged node's '{TagKey}' held {Describe(mapping[TagKey])} rather than a string.");

        var inner = Decode(mapping[NodeKey]);

        // A node has one Tag field, so a tag wrapped around a tagged node has nowhere to go. It is
        // also not something Encode produces, since a tagged node's inner encoding is the untagged
        // one: reading it as anything at all would be inventing a tree from corruption.
        if (inner.Tag != null)
            throw new FormatException($"Drydock codec: a tagged node's '{NodeKey}' was itself tagged, and a node carries one tag.");

        inner.Tag = tag;
        return inner;
    }

    private static JsonNode? EncodeUntagged(DataNode node)
    {
        switch (node)
        {
            case ValueDataNode value:
                return value.IsNull ? null : JsonValue.Create(value.Value);

            case SequenceDataNode sequence:
            {
                // Order is the content for a component's list field, and a JSON array keeps it.
                var array = new JsonArray();
                foreach (var element in sequence.Sequence)
                {
                    array.Add(Encode(element));
                }

                return array;
            }

            case MappingDataNode mapping:
            {
                if (mapping.Has(TagKey))
                {
                    throw new InvalidOperationException(
                        $"Drydock codec: a mapping carries the reserved key '{TagKey}', which the encoding cannot tell from a tagged node. No data field may be named it.");
                }

                var encoded = new JsonObject();
                foreach (var (key, child) in mapping)
                {
                    encoded.Add(key, Encode(child));
                }

                return encoded;
            }

            default:
                // DataNodeParser.DataNodeAlias is the fourth kind and is parser-internal, never part
                // of a finished tree. Anything reaching here is a kind the image has no rule for.
                throw new NotSupportedException($"Drydock codec: {node.GetType().Name} is not a node kind the image encodes.");
        }
    }

    private static DataNode DecodeUntagged(JsonNode? json)
    {
        switch (json)
        {
            case null:
                return ValueDataNode.Null();

            case JsonArray array:
            {
                var sequence = new SequenceDataNode(array.Count);
                foreach (var element in array)
                {
                    sequence.Add(Decode(element));
                }

                return sequence;
            }

            case JsonObject mapping:
            {
                var decoded = new MappingDataNode(mapping.Count);
                foreach (var (key, child) in mapping)
                {
                    decoded.Add(key, Decode(child));
                }

                return decoded;
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
                return new ValueDataNode(text);

            default:
                throw new FormatException($"Drydock codec: a stored scalar held {Describe(json)}, and a scalar is a string here whatever it spells.");
        }
    }

    private static string Describe(JsonNode? json) => json?.GetValueKind().ToString() ?? "null";
}
