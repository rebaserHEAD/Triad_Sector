#nullable enable
using System;
using System.Text.Json.Nodes;
using Content.Server._Triad.Drydock.Codec;
using NUnit.Framework;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Tests.Server._Triad.Drydock;

/// <summary>
/// The per-node encoder and decoder on hand-built trees, so every case is a plain unit test rather
/// than something that needs a component, a serialization manager or a server pair.
///
/// <para>Nothing here compares nodes with <c>Equals</c>. <see cref="ValueDataNode.Equals(ValueDataNode)"/>
/// compares <see cref="ValueDataNode.Value"/> and nothing else
/// (<c>RobustToolbox/Robust.Shared/Serialization/Markdown/Value/ValueDataNode.cs:130-135</c>), so an
/// assertion that used it would pass while the two things this encoding exists to keep apart, a null
/// and its tag, were both lost. Every round trip checks kind, value, <c>IsNull</c> and <c>Tag</c>
/// explicitly.</para>
/// </summary>
[TestFixture, TestOf(typeof(DrydockNodeJson))]
[Parallelizable(ParallelScope.All)]
public sealed class DrydockNodeJsonTest
{
    private static DataNode RoundTrip(DataNode node) => DrydockNodeJson.Decode(DrydockNodeJson.Encode(node));

    private static ValueDataNode AssertScalar(DataNode node, string value, bool isNull, string? tag)
    {
        Assert.That(node, Is.InstanceOf<ValueDataNode>());
        var scalar = (ValueDataNode) node;

        Assert.Multiple(() =>
        {
            Assert.That(scalar.Value, Is.EqualTo(value), "value");
            Assert.That(scalar.IsNull, Is.EqualTo(isNull), "IsNull");
            Assert.That(scalar.Tag, Is.EqualTo(tag), "Tag");
        });

        return scalar;
    }

    [Test]
    public void ATaggedMappingKeepsItsTagAndItsMembers()
    {
        var mapping = new MappingDataNode();
        mapping.Add("hull", new ValueDataNode("steel"));
        mapping.Add("count", new ValueDataNode("3"));
        mapping.Tag = "!type:ShipPart";

        var encoded = DrydockNodeJson.Encode(mapping);

        Assert.That(encoded, Is.InstanceOf<JsonObject>());
        var wrapper = (JsonObject) encoded!;
        Assert.Multiple(() =>
        {
            Assert.That(wrapper.Count, Is.EqualTo(2));
            Assert.That(wrapper[DrydockNodeJson.TagKey]!.GetValue<string>(), Is.EqualTo("!type:ShipPart"));
            Assert.That(wrapper[DrydockNodeJson.NodeKey], Is.InstanceOf<JsonObject>());
        });

        var decoded = DrydockNodeJson.Decode(encoded);

        Assert.That(decoded, Is.InstanceOf<MappingDataNode>());
        var back = (MappingDataNode) decoded;
        Assert.Multiple(() =>
        {
            Assert.That(back.Tag, Is.EqualTo("!type:ShipPart"));
            Assert.That(back.Count, Is.EqualTo(2));
        });

        // A number stays the text that spells it: the scalar rule is what keeps 3 and "3" one thing.
        AssertScalar(back["hull"], "steel", false, null);
        AssertScalar(back["count"], "3", false, null);
    }

    /// <summary>
    /// The control the step is judged on: if the null and the string that spells it ever encoded the
    /// same way, this is the test that fails, and it asserts both halves in one place so neither can
    /// be read as the other's receipt.
    /// </summary>
    [Test]
    public void ANullScalarAndTheStringNullAreNotTheSameRow()
    {
        var nullScalar = ValueDataNode.Null();
        var spelled = new ValueDataNode("null");

        var encodedNull = DrydockNodeJson.Encode(nullScalar);
        var encodedSpelled = DrydockNodeJson.Encode(spelled);

        Assert.Multiple(() =>
        {
            Assert.That(encodedNull, Is.Null, "a null scalar is JSON null");
            Assert.That(encodedSpelled, Is.InstanceOf<JsonValue>());
            Assert.That(encodedSpelled!.GetValue<string>(), Is.EqualTo("null"), "the string is a JSON string");
        });

        AssertScalar(DrydockNodeJson.Decode(encodedNull), string.Empty, true, null);
        AssertScalar(DrydockNodeJson.Decode(encodedSpelled), "null", false, null);
    }

    [Test]
    public void TheEmptyStringIsAStringAndNotANull()
    {
        var encoded = DrydockNodeJson.Encode(new ValueDataNode(string.Empty));

        Assert.Multiple(() =>
        {
            Assert.That(encoded, Is.InstanceOf<JsonValue>());
            Assert.That(encoded!.GetValue<string>(), Is.EqualTo(string.Empty));
        });

        AssertScalar(DrydockNodeJson.Decode(encoded), string.Empty, false, null);
    }

    [Test]
    public void ASequenceKeepsItsOrderItsNullAndItsString()
    {
        var sequence = new SequenceDataNode();
        sequence.Add(ValueDataNode.Null());
        sequence.Add(new ValueDataNode("aft"));

        var encoded = DrydockNodeJson.Encode(sequence);

        Assert.That(encoded, Is.InstanceOf<JsonArray>());
        Assert.That(((JsonArray) encoded!).Count, Is.EqualTo(2));

        var decoded = DrydockNodeJson.Decode(encoded);
        Assert.That(decoded, Is.InstanceOf<SequenceDataNode>());
        var back = (SequenceDataNode) decoded;

        Assert.That(back.Count, Is.EqualTo(2));
        AssertScalar(back[0], string.Empty, true, null);
        AssertScalar(back[1], "aft", false, null);
    }

    [Test]
    public void ATaggedSequenceKeepsItsTag()
    {
        var sequence = new SequenceDataNode();
        sequence.Add(new ValueDataNode("port"));
        sequence.Tag = "!type:DockList";

        var decoded = RoundTrip(sequence);

        Assert.That(decoded, Is.InstanceOf<SequenceDataNode>());
        var back = (SequenceDataNode) decoded;
        Assert.Multiple(() =>
        {
            Assert.That(back.Tag, Is.EqualTo("!type:DockList"));
            Assert.That(back.Count, Is.EqualTo(1));
        });
        AssertScalar(back[0], "port", false, null);
    }

    [Test]
    public void ATaggedNullScalarKeepsBothHalves()
    {
        var node = ValueDataNode.Null();
        node.Tag = "!type:Deadline";

        var encoded = DrydockNodeJson.Encode(node);

        Assert.That(encoded, Is.InstanceOf<JsonObject>());
        var wrapper = (JsonObject) encoded!;
        Assert.Multiple(() =>
        {
            Assert.That(wrapper.Count, Is.EqualTo(2));
            Assert.That(wrapper[DrydockNodeJson.TagKey]!.GetValue<string>(), Is.EqualTo("!type:Deadline"));
            Assert.That(wrapper[DrydockNodeJson.NodeKey], Is.Null, "the tagged node is still JSON null");
        });

        AssertScalar(DrydockNodeJson.Decode(encoded), string.Empty, true, "!type:Deadline");
    }

    [Test]
    public void AMappingInASequenceInAMappingComesBackWhole()
    {
        var inner = new MappingDataNode();
        inner.Add("id", new ValueDataNode("Airlock"));
        inner.Add("locked", new ValueDataNode("true"));

        var sequence = new SequenceDataNode();
        sequence.Add(inner);

        var outer = new MappingDataNode();
        outer.Add("doors", sequence);

        var decoded = RoundTrip(outer);

        Assert.That(decoded, Is.InstanceOf<MappingDataNode>());
        var backOuter = (MappingDataNode) decoded;
        Assert.That(backOuter["doors"], Is.InstanceOf<SequenceDataNode>());

        var backSequence = (SequenceDataNode) backOuter["doors"];
        Assert.That(backSequence.Count, Is.EqualTo(1));
        Assert.That(backSequence[0], Is.InstanceOf<MappingDataNode>());

        var backInner = (MappingDataNode) backSequence[0];
        Assert.That(backInner.Count, Is.EqualTo(2));
        AssertScalar(backInner["id"], "Airlock", false, null);
        AssertScalar(backInner["locked"], "true", false, null);
    }

    [Test]
    public void AMappingCarryingTheTagKeyThrowsAtDepth()
    {
        var collides = new MappingDataNode();
        collides.Add(DrydockNodeJson.TagKey, new ValueDataNode("not a tag"));

        var sequence = new SequenceDataNode();
        sequence.Add(collides);

        var outer = new MappingDataNode();
        outer.Add("cargo", sequence);

        Assert.Throws<InvalidOperationException>(() => DrydockNodeJson.Encode(outer));
    }

    [Test]
    public void ATaggedMappingCarryingTheTagKeyThrowsToo()
    {
        var collides = new MappingDataNode();
        collides.Add(DrydockNodeJson.TagKey, new ValueDataNode("not a tag"));
        collides.Tag = "!type:Anything";

        Assert.Throws<InvalidOperationException>(() => DrydockNodeJson.Encode(collides));
    }

    /// <summary>
    /// The other reserved member is not a collision: a plain mapping carrying it reads back as
    /// itself, so it is not thrown on.
    /// </summary>
    [Test]
    public void AMappingCarryingOnlyTheNodeKeyIsAPlainMapping()
    {
        var mapping = new MappingDataNode();
        mapping.Add(DrydockNodeJson.NodeKey, new ValueDataNode("ordinary"));

        var decoded = RoundTrip(mapping);

        Assert.That(decoded, Is.InstanceOf<MappingDataNode>());
        var back = (MappingDataNode) decoded;
        Assert.Multiple(() =>
        {
            Assert.That(back.Count, Is.EqualTo(1));
            Assert.That(back.Tag, Is.Null);
        });
        AssertScalar(back[DrydockNodeJson.NodeKey], "ordinary", false, null);
    }

    [Test]
    public void DecodeRefusesAScalarThatIsNotAString()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<FormatException>(() => DrydockNodeJson.Decode(JsonNode.Parse("5")));
            Assert.Throws<FormatException>(() => DrydockNodeJson.Decode(JsonNode.Parse("true")));
            Assert.Throws<FormatException>(() => DrydockNodeJson.Decode(JsonValue.Create(5)));
        });
    }

    [Test]
    public void DecodeRefusesATaggedNodeCarryingAThirdKey()
    {
        var json = JsonNode.Parse($$"""{"{{DrydockNodeJson.TagKey}}": "!type:Thing", "{{DrydockNodeJson.NodeKey}}": "x", "extra": "y"}""");

        Assert.Throws<FormatException>(() => DrydockNodeJson.Decode(json));
    }

    /// <summary>
    /// A node carries one <see cref="DataNode.Tag"/>, and a tagged node's inner encoding is the
    /// untagged one, so a tag wrapped around a tag is not something <see cref="DrydockNodeJson.Encode"/>
    /// can produce. It fails at the read rather than one step later at a re-encode, and the decoder
    /// never builds the mapping with a literal tag key that the encoder would then refuse.
    /// </summary>
    [Test]
    public void DecodeRefusesANodeTaggedTwice()
    {
        var json = JsonNode.Parse(
            $$$"""{"{{{DrydockNodeJson.TagKey}}}": "!type:Outer", "{{{DrydockNodeJson.NodeKey}}}": {"{{{DrydockNodeJson.TagKey}}}": "!type:Inner", "{{{DrydockNodeJson.NodeKey}}}": "x"}}""");

        Assert.Throws<FormatException>(() => DrydockNodeJson.Decode(json));
    }
}
