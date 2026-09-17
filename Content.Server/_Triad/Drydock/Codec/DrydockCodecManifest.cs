using System;
using System.Collections.Immutable;
using System.Reflection;
using Content.Shared.Damage;
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
    public sealed record AsymmetricInlineField(Type Owner, string Member, string Key);

    /// <summary>
    /// <para><c>DamageSpecifier.DamageDict</c> is an <c>IncludeDataField(readOnly)</c> carrying
    /// <see cref="DamageSpecifierDictionarySerializer"/>, which is an <c>ITypeReader</c> and nothing
    /// else, under its own <c>//todo writing</c>
    /// (<c>Content.Shared/Damage/DamageSpecifierDictionarySerializer.cs:13-14</c>). Its reader takes
    /// the whole specifier mapping and looks for a <c>types</c> sub-mapping and a <c>groups</c>
    /// sub-mapping (<c>:38</c>, <c>:43</c>), so inlined pairs would be read by nobody and the damage
    /// would come back zero.</para>
    ///
    /// <para>Only <c>types</c> is written. The reader distributes a group's total across the group's
    /// members and adds it to the same dictionary (<c>:58-72</c>), and the live dictionary is
    /// already that flattened total, so writing both would count the damage twice.</para>
    ///
    /// <para>The pass owns the key. The generated writer emits one only when
    /// <c>_damageTypeDictionary</c> is non-null, and that field has no writer anywhere in the tree,
    /// so today there is nothing to overwrite; if it is ever populated, the live value still wins.</para>
    /// </summary>
    public static readonly ImmutableArray<AsymmetricInlineField> AsymmetricInlineFields =
        ImmutableArray.Create(
            new AsymmetricInlineField(typeof(DamageSpecifier), nameof(DamageSpecifier.DamageDict), "types"));

    /// <summary>
    /// The key this member's value is written under, or null when the member is written the ordinary
    /// way.
    /// </summary>
    public static string? AsymmetricKey(MemberInfo member)
    {
        foreach (var entry in AsymmetricInlineFields)
        {
            if (entry.Owner == member.DeclaringType && entry.Member == member.Name)
                return entry.Key;
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
