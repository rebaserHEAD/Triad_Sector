#nullable enable
using System.Linq;
using Content.Server._Triad.Drydock.Codec;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Tests.Server._Triad.Drydock;

/// <summary>
/// The premise under an asymmetric entry's <c>AbsentKeys</c>: the reader still consumes those keys,
/// so a row carrying one has its damage counted a second time. Asserted by reading a hand-built
/// mapping through the manager rather than by reading the serializer's source, because a source
/// check passes just as well on a serializer that stopped reading the key for some other reason.
/// </summary>
[TestFixture, TestOf(typeof(DrydockCodecManifest))]
public sealed class DrydockCodecAsymmetricReaderTest : ContentUnitTest
{
    private ISerializationManager _serialization = default!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _serialization = IoCManager.Resolve<ISerializationManager>();
        _serialization.Initialize();

        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        prototypes.Initialize();
        prototypes.LoadString(Prototypes);
        prototypes.ResolveResults();
    }

    /// <summary>
    /// The entry this fixture is about, asserted before its behaviour is, so a manifest that moved
    /// fails here rather than quietly leaving the assertions below about nothing.
    /// </summary>
    [Test]
    public void TheDamageEntryStillNamesTheKeysThisFixtureCovers()
    {
        var entry = DrydockCodecManifest.AsymmetricInlineFields.Single(field => field.Owner == typeof(DamageSpecifier));

        Assert.Multiple(() =>
        {
            Assert.That(entry.Member, Is.EqualTo(nameof(DamageSpecifier.DamageDict)));
            Assert.That(entry.Key, Is.EqualTo("types"));
            Assert.That(entry.AbsentKeys, Is.EquivalentTo(new[] { "groups" }));
        });
    }

    /// <summary>
    /// The premise, and the control for it in the same test: the flattened total alone reads back as
    /// itself, and the same row with the authored group still on it reads back as twice that. The
    /// second number is the bug the entry removes, measured rather than argued.
    /// </summary>
    [Test]
    public void LeavingTheGroupKeyOnARowCountsItsDamageTwice()
    {
        // What the live DamageDict holds after the reader distributes Brute 6 across its three
        // types, which is what the codec writes under "types".
        var flattened = new MappingDataNode
        {
            ["types"] = new MappingDataNode
            {
                ["Blunt"] = new ValueDataNode("2"),
                ["Slash"] = new ValueDataNode("2"),
                ["Piercing"] = new ValueDataNode("2"),
            },
        };

        var withGroups = new MappingDataNode
        {
            ["types"] = new MappingDataNode
            {
                ["Blunt"] = new ValueDataNode("2"),
                ["Slash"] = new ValueDataNode("2"),
                ["Piercing"] = new ValueDataNode("2"),
            },
            ["groups"] = new MappingDataNode
            {
                ["Brute"] = new ValueDataNode("6"),
            },
        };

        Assert.Multiple(() =>
        {
            Assert.That(Total(flattened), Is.EqualTo(FixedPoint2.New(6)),
                "The row the codec writes must read back as the damage the entity had.");

            Assert.That(Total(withGroups), Is.EqualTo(FixedPoint2.New(12)),
                "The control: the reader still consumes the group key, so a row carrying it is read as the same damage twice. That is why the pass removes it.");
        });
    }

    private FixedPoint2 Total(MappingDataNode mapping) =>
        _serialization.Read<DamageSpecifier>(mapping, notNullableOverride: true).GetTotal();

    private const string Prototypes = @"
- type: damageType
  id: Blunt
  name: damage-type-blunt

- type: damageType
  id: Slash
  name: damage-type-slash

- type: damageType
  id: Piercing
  name: damage-type-piercing

- type: damageGroup
  id: Brute
  name: damage-group-brute
  damageTypes:
    - Blunt
    - Slash
    - Piercing
";
}
