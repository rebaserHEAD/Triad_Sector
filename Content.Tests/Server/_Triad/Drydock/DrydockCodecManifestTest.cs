#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Content.Server._Triad.Drydock.Codec;
using Content.Shared.Damage;
using Content.Shared.Doors.Components;
using Content.Shared.FixedPoint;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Tests.Server._Triad.Drydock;

/// <summary>
/// The build-time audit of <see cref="DrydockCodecManifest"/>. Every entry there is hand-written
/// work standing in for something the engine cannot do, so each one has to keep failing the check
/// that made it necessary: an entry whose premise went away is redundant hand-written work, and
/// redundant hand-written work in a serializer is how a stored ship silently loses a field.
/// </summary>
[TestFixture, TestOf(typeof(DrydockCodecManifest))]
[Parallelizable(ParallelScope.All)]
public sealed class DrydockCodecManifestTest
{
    [Test]
    public void EveryAsymmetricEntryNamesAReadOnlyMemberWithASerializer()
    {
        Assert.That(DrydockCodecManifest.AsymmetricInlineFields, Is.Not.Empty,
            "The audit asserts against the entries, so an empty manifest would pass it by doing nothing.");

        foreach (var entry in DrydockCodecManifest.AsymmetricInlineFields)
        {
            var member = Member(entry.Owner, entry.Member);
            Assert.That(member, Is.Not.Null, $"{entry.Owner.Name}.{entry.Member} does not exist.");

            var attribute = member!.GetCustomAttribute<DataFieldBaseAttribute>();
            Assert.That(attribute, Is.Not.Null, $"{entry.Owner.Name}.{entry.Member} is not a data field.");

            Assert.Multiple(() =>
            {
                Assert.That(attribute!.ReadOnly, Is.True,
                    $"{entry.Owner.Name}.{entry.Member} is no longer readOnly, so the generated writer writes it and the entry is redundant.");

                Assert.That(attribute.CustomTypeSerializer, Is.Not.Null,
                    $"{entry.Owner.Name}.{entry.Member} no longer names a serializer, so there is no asymmetry left to correct.");
            });
        }
    }

    /// <summary>
    /// The premise of every entry: the member's own serializer reads a shape it cannot write. The
    /// day it grows a writer, the pass writes the member through it and the entry has to go.
    /// </summary>
    [Test]
    public void NoAsymmetricEntrySerializerHasLearnedToWrite()
    {
        foreach (var entry in DrydockCodecManifest.AsymmetricInlineFields)
        {
            var member = Member(entry.Owner, entry.Member)!;
            var serializer = member.GetCustomAttribute<DataFieldBaseAttribute>()!.CustomTypeSerializer!;

            Assert.That(DrydockCodecManifest.CanWrite(MemberType(member), serializer), Is.False,
                $"{serializer.Name} now writes {MemberType(member).Name}: remove the {entry.Owner.Name}.{entry.Member} entry and let the pass write the member through it.");
        }
    }

    /// <summary>
    /// The control. The test above is an absence, and an absence passes just as well when the check
    /// itself is broken, so the same predicate is pointed at a serializer that does write.
    /// </summary>
    [Test]
    public void TheWriterCheckDetectsAWriter()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DrydockCodecManifest.CanWrite(typeof(Dictionary<string, FixedPoint2>), typeof(ControlWriter)), Is.True,
                "The check missed a serializer that implements ITypeWriter for exactly this type.");

            Assert.That(DrydockCodecManifest.CanWrite(typeof(Dictionary<string, FixedPoint2>), typeof(DamageSpecifierDictionarySerializer)), Is.False,
                "The check claimed a reader-only serializer can write.");
        });
    }

    [Test]
    public void EveryComputedEntryStillDescribesItsComponent()
    {
        Assert.That(DrydockCodecManifest.ComputedFields, Is.Not.Empty,
            "The audit asserts against the entries, so an empty manifest would pass it by doing nothing.");

        foreach (var entry in DrydockCodecManifest.ComputedFields)
        {
            Assert.That(Wrong(entry), Is.Null);
        }
    }

    /// <summary>
    /// The control. Every assertion above is that a check found nothing, which is also what a broken
    /// check reports, so the same check is run against entries corrupted one way each.
    /// </summary>
    [Test]
    public void TheComputedCheckCatchesACorruptedEntry()
    {
        var real = DrydockCodecManifest.ComputedFields[0];

        Assert.Multiple(() =>
        {
            Assert.That(Wrong(real with { ComputedMember = "SecondsUntilNothing" }), Is.Not.Null,
                "A computed member that does not exist went unreported.");

            Assert.That(Wrong(real with { ComputedMember = nameof(DoorComponent.Partial) }), Is.Not.Null,
                "A computed member that is a field rather than a property with a setter went unreported.");

            Assert.That(Wrong(real with { BackingMember = nameof(DoorComponent.Partial) }), Is.Not.Null,
                "A backing member that is itself a data field went unreported, and it would be written twice.");

            Assert.That(Wrong(real with { BackingMember = "NextStateChangeThatIsNot" }), Is.Not.Null,
                "A backing member that does not exist went unreported.");
        });
    }

    /// <summary>
    /// What is wrong with this entry, or null when nothing is. A computed field is a property whose
    /// setter refuses what its getter returns, so the entry has to name one; the backing member has
    /// to be something the generated writer does not already write, or the codec would store the
    /// value twice and the second write would win.
    /// </summary>
    private static string? Wrong(DrydockCodecManifest.ComputedField entry)
    {
        var computed = Member(entry.Component, entry.ComputedMember);
        if (computed == null)
            return $"{entry.Component.Name}.{entry.ComputedMember} does not exist.";

        if (computed.GetCustomAttribute<DataFieldAttribute>() == null)
            return $"{entry.Component.Name}.{entry.ComputedMember} is not a data field, so the engine never writes it and there is nothing to replace.";

        if (computed is not PropertyInfo property)
            return $"{entry.Component.Name}.{entry.ComputedMember} is not a property, so it is not computed over anything.";

        if (property.SetMethod == null)
            return $"{entry.Component.Name}.{entry.ComputedMember} has no setter, so the engine could not read it back either way.";

        var backing = Member(entry.Component, entry.BackingMember);
        if (backing == null)
            return $"{entry.Component.Name}.{entry.BackingMember} does not exist.";

        if (backing.GetCustomAttribute<DataFieldBaseAttribute>() != null)
            return $"{entry.Component.Name}.{entry.BackingMember} is itself a data field, so the pass would write it twice.";

        return null;
    }

    private static MemberInfo? Member(Type owner, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        return (MemberInfo?) owner.GetField(name, flags) ?? owner.GetProperty(name, flags);
    }

    private static Type MemberType(MemberInfo member) => member switch
    {
        FieldInfo field => field.FieldType,
        PropertyInfo property => property.PropertyType,
        _ => throw new InvalidOperationException($"{member.Name} is neither a field nor a property."),
    };

    /// <summary>A serializer that does write, so the check has something to find.</summary>
    private sealed class ControlWriter : ITypeWriter<Dictionary<string, FixedPoint2>>
    {
        public DataNode Write(
            ISerializationManager serializationManager,
            Dictionary<string, FixedPoint2> value,
            IDependencyCollection dependencies,
            bool alwaysWrite = false,
            ISerializationContext? context = null)
        {
            return new MappingDataNode();
        }
    }
}
