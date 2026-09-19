using System;
using System.Collections.Immutable;
using System.Reflection;
using Content.Shared.Damage;
using Content.Shared.Doors.Components;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// The corrections the codec cannot derive and has to be told, in one list, because each one is a
/// content fact rather than a rule: reflection can see that a field exists but not that the shape it
/// reads back from is spelled differently from the shape it lives in.
///
/// <para>Every entry is audited at build time. An entry whose premise has gone away is a failing
/// test rather than hand-written work left quietly in place, which is the same posture
/// <see cref="DrydockSerializationGap.CapturedTypes"/> takes.</para>
/// </summary>
public static class DrydockCodecManifest
{
    /// <summary>
    /// An inline field whose own serializer reads a shape it cannot write, so the pass has to name
    /// the key that serializer reads from.
    /// </summary>
    /// <param name="Owner">The data definition or component declaring the member.</param>
    /// <param name="Member">The member holding the live value.</param>
    /// <param name="Key">The key in the owner's mapping that the member's reader consumes.</param>
    /// <param name="AbsentKeys">
    /// The other keys that reader consumes, which the pass removes. The live member already holds
    /// everything they would contribute, so a row carrying one is read twice.
    /// </param>
    /// <param name="Twins">
    /// The owner's other data fields that read <paramref name="Key"/> or one of <paramref name="AbsentKeys"/>
    /// themselves. An asymmetric entry that writes under a key another data field reads must name that twin, and the
    /// twin is carried under its own side key: otherwise the write under the shared key is read into the twin as well
    /// and the twin's own value is lost.
    /// </param>
    public sealed record AsymmetricInlineField(
        Type Owner,
        string Member,
        string Key,
        ImmutableArray<string> AbsentKeys,
        ImmutableArray<AsymmetricTwin> Twins);

    /// <param name="Member">The owner's data field that reads a key the entry's own member is written under.</param>
    /// <param name="SideKey">
    /// Where its live value is carried instead. No reader consumes it: the owner's generated reader and the entry's
    /// serializer look their keys up by name and never enumerate the mapping. That holds by construction only for a
    /// reader that looks keys up: a twin on an owner read by a custom type serializer that enumerates its mapping would
    /// hand it the side key, and needs the key stripped before that read.
    /// </param>
    public sealed record AsymmetricTwin(string Member, string SideKey);

    /// <summary>
    /// <para><c>DamageSpecifier.DamageDict</c> is an <c>IncludeDataField(readOnly)</c> carrying
    /// <see cref="DamageSpecifierDictionarySerializer"/>, which is an <c>ITypeReader</c> and nothing
    /// else, under its own <c>//todo writing</c>
    /// (<c>Content.Shared/Damage/DamageSpecifierDictionarySerializer.cs:13-14</c>). Its reader takes
    /// the whole specifier mapping and looks for a <c>types</c> sub-mapping and a <c>groups</c>
    /// sub-mapping (<c>:38</c>, <c>:43</c>), so inlined pairs would be read by nobody and the damage
    /// would come back zero.</para>
    ///
    /// <para>Only <c>types</c> is written, and <c>groups</c> is removed. The reader distributes a
    /// group's total across that group's own damage types and adds it to the same dictionary it just
    /// filled from <c>types</c> (<c>:58-72</c>), while the live dictionary is already that flattened
    /// total. A row carrying both is therefore read as the group's damage twice, and it grows again
    /// on every re-read, because the prototype's authored <c>groups</c> survives in
    /// <c>_damageGroupDictionary</c> and the generated writer emits it whenever it is non-null.</para>
    ///
    /// <para>The pass owns both keys. It writes <c>types</c> whether or not the generated writer
    /// emitted one, and removes <c>groups</c> whether or not it did.</para>
    ///
    /// <para>Both keys are also read by data fields of their own, <c>_damageTypeDictionary</c> and
    /// <c>_damageGroupDictionary</c> (<c>Content.Shared/Damage/DamageSpecifier.cs:21-32</c>), kept "solely so the
    /// wiki works" and read by no hand-written code. Live, they hold what a prototype or map authored, or null. Left
    /// alone, a read put the live damage into the first and nothing into the second, so an authored weapon lost its
    /// <c>groups</c> and a damaged wall came back with its damage copied into a field an engine save writes as
    /// <c>types</c>. Each is carried under its own side key and set back from it after the read.</para>
    /// </summary>
    public static readonly ImmutableArray<AsymmetricInlineField> AsymmetricInlineFields =
        ImmutableArray.Create(
            new AsymmetricInlineField(
                typeof(DamageSpecifier),
                nameof(DamageSpecifier.DamageDict),
                "types",
                ImmutableArray.Create("groups"),
                ImmutableArray.Create(
                    new AsymmetricTwin("_damageTypeDictionary", "~types"),
                    new AsymmetricTwin("_damageGroupDictionary", "~groups"))));

    /// <summary>The field or property a twin names, which the manifest's audit keeps resolvable.</summary>
    public static MemberInfo TwinMember(AsymmetricInlineField entry, AsymmetricTwin twin)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        return (MemberInfo?) entry.Owner.GetField(twin.Member, flags)
               ?? entry.Owner.GetProperty(twin.Member, flags)
               ?? throw new InvalidOperationException($"Drydock codec: {entry.Owner.Name}.{twin.Member}, a twin of {entry.Member}, does not exist.");
    }

    /// <summary>
    /// A data field that is a computed property over a backing member, so the value the getter
    /// returns is not the value the setter accepts and the field does not survive its own round
    /// trip. The codec stores the backing member and skips the computed one.
    /// </summary>
    /// <param name="Component">The component declaring both members.</param>
    /// <param name="ComputedMember">The data field the engine writes and this pass removes.</param>
    /// <param name="BackingMember">The member actually holding the value, which is stored instead.</param>
    /// <param name="BackingCase">How the backing member is written, since it is not a data field and has no serializer of its own.</param>
    public sealed record ComputedField(
        Type Component,
        string ComputedMember,
        string BackingMember,
        DrydockCodecFieldPass.FieldCase BackingCase);

    /// <summary>
    /// <para>A door writes its pending state change as <c>SecondsUntilStateChange</c>, seconds from
    /// now, and its setter returns on null or on any positive value
    /// (<c>Content.Shared/Doors/Components/DoorComponent.cs:233-255</c>), so a change still in the
    /// future is dropped on read and only one already due is restored. That is finding F9: the field
    /// round-trips in form and not in value, which looks exactly like a field that works.</para>
    ///
    /// <para>The backing member is <c>NextStateChange</c> (<c>:70</c>), an absolute game time that
    /// is neither a data field nor <c>[AutoPausedField]</c>, so it is written through the
    /// time-offset adapter and the pause shift the engine applies to marked fields does not apply to
    /// it. Nothing in reflection can tell the codec any of this, which is why it is an entry rather
    /// than a rule.</para>
    /// </summary>
    public static readonly ImmutableArray<ComputedField> ComputedFields =
        ImmutableArray.Create(
            new ComputedField(
                typeof(DoorComponent),
                "SecondsUntilStateChange",
                nameof(DoorComponent.NextStateChange),
                DrydockCodecFieldPass.FieldCase.TimeOffset));

    /// <summary>
    /// The entry for this member, or null when the member is written the ordinary way.
    /// </summary>
    public static AsymmetricInlineField? Asymmetric(MemberInfo member)
    {
        foreach (var entry in AsymmetricInlineFields)
        {
            if (entry.Owner == member.DeclaringType && entry.Member == member.Name)
                return entry;
        }

        return null;
    }

    /// <summary>
    /// Can this <c>customTypeSerializer</c> write this type at all? Several content serializers read
    /// only, and <c>WriteValue&lt;T, TWriter&gt;</c> is constrained to <see cref="ITypeWriter{T}"/>
    /// (<c>RobustToolbox/Robust.Shared/Serialization/Manager/ISerializationManager.cs:247</c>), so
    /// the interface is tested rather than assumed. The pass and the audit ask this one method, so
    /// neither can drift from the other.
    /// </summary>
    public static bool CanWrite(Type valueType, Type serializer) =>
        typeof(ITypeWriter<>).MakeGenericType(valueType).IsAssignableFrom(serializer);
}
