using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Content.Server.Atmos.Components;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.Definition;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// The keys a stored component row may hold at its top level: every tag the component declares, and every key the codec
/// writes into the row itself. A row is held to this set before it is read (<see cref="DrydockCodec.Read(Type, Robust.Shared.Serialization.Markdown.Mapping.MappingDataNode)"/>),
/// because the engine's generated reader looks each declared tag up by name and never enumerates the row
/// (<c>RobustToolbox/Robust.Serialization.Generator/Generator.cs:472</c>), so a key it does not declare is a stored value
/// dropped without a word.
///
/// <list type="bullet">
/// <item>A data field's tag, on the type and every base, by the walk the field pass makes
/// (<see cref="DrydockCodecFieldPass.DataMembers"/>). The pass writes a time offset, a <c>readOnly</c> field and a flag
/// field's rewrite under that same tag.</item>
/// <item>An include of a data definition: that definition's own set, since its pairs are merged into the owner's mapping
/// under no key of their own.</item>
/// <item>An include with an asymmetric entry (<see cref="DrydockCodecManifest.AsymmetricInlineFields"/>): the key the pass
/// writes it under and each twin's side key.</item>
/// <item>An include written by a serializer of its own: the keys <see cref="SerializerIncludes"/> lists for it, which no
/// reflection can name. An include it does not list throws, so a new one fails the build-time walk over every registered
/// component rather than a load.</item>
/// <item>A computed field's backing key (<see cref="DrydockCodecManifest.ComputedFields"/>), which the pass writes in the
/// computed field's place.</item>
/// </list>
///
/// <para><c>$tag</c> is never a row's key: the stored form's decode takes it into the node's tag
/// (<see cref="DrydockNodeJson.Decode"/>), and a mapping carrying it cannot be encoded. A nested mapping is not held to
/// anything here; its keys are read by its own data definition's generated reader, which drops an unknown one just as
/// silently.</para>
/// </summary>
public static class DrydockDeclaredKeys
{
    /// <param name="Owner">The type declaring the include.</param>
    /// <param name="Member">The include.</param>
    /// <param name="Keys">The keys its serializer writes into the owner's mapping.</param>
    public sealed record SerializerInclude(Type Owner, string Member, ImmutableArray<string> Keys);

    /// <summary>
    /// A grid's gas is written by <c>TileAtmosCollectionSerializer</c> as <c>version</c> and <c>data</c>
    /// (<c>Content.Server/Atmos/Serialization/TileAtmosCollectionSerializer.cs:144-155</c>). Its reader also takes a version
    /// 1 layout, <c>tiles</c> and <c>uniqueMixes</c> (<c>:37-42</c>), which that writer never produces.
    /// </summary>
    public static readonly ImmutableArray<SerializerInclude> SerializerIncludes = ImmutableArray.Create(
        new SerializerInclude(
            typeof(GridAtmosphereComponent),
            nameof(GridAtmosphereComponent.Tiles),
            ImmutableArray.Create("version", "data")));

    private static readonly ConcurrentDictionary<Type, ImmutableHashSet<string>> Cache = new();

    /// <summary>The keys a row of <paramref name="type"/> may hold at its top level.</summary>
    /// <exception cref="InvalidOperationException">An include whose keys cannot be named.</exception>
    public static ImmutableHashSet<string> Of(Type type) => Cache.GetOrAdd(type, Build);

    private static ImmutableHashSet<string> Build(Type type)
    {
        var keys = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var (member, attribute) in DrydockCodecFieldPass.DataMembers(type))
        {
            if (attribute is DataFieldAttribute field)
            {
                keys.Add(field.Tag ?? DataDefinitionUtility.AutoGenerateTag(member.Name));
                continue;
            }

            var include = $"{member.DeclaringType?.Name}.{member.Name}";

            if (DrydockCodecManifest.Asymmetric(member) is { } asymmetric)
            {
                keys.Add(asymmetric.Key);
                keys.UnionWith(asymmetric.Twins.Select(twin => twin.SideKey));
                continue;
            }

            if (attribute.CustomTypeSerializer is { } serializer)
            {
                var listed = SerializerIncludes.FirstOrDefault(entry => entry.Owner == member.DeclaringType && entry.Member == member.Name)
                             ?? throw new InvalidOperationException(
                                 $"Drydock codec: {include} is an include written by {serializer.Name}, whose keys only a {nameof(SerializerIncludes)} entry can name.");
                keys.UnionWith(listed.Keys);
                continue;
            }

            // A value of a type that is not sealed can be a subtype, whose fields its declared type does not name.
            var included = DrydockCodecManifestMembers.MemberType(member);
            if (!typeof(ISerializationGenerated).IsAssignableFrom(included) || !(included.IsSealed || included.IsValueType))
            {
                throw new InvalidOperationException(
                    $"Drydock codec: {include} includes {included.Name}, which is not a sealed data definition, so the keys it writes cannot be named from its type.");
            }

            keys.UnionWith(Of(included));
        }

        foreach (var computed in DrydockCodecManifest.ComputedFields)
        {
            // Matched exactly, as the pass matches it (DrydockCodecFieldPass.BuildComputed).
            if (computed.Component == type)
                keys.Add(DataDefinitionUtility.AutoGenerateTag(computed.BackingMember));
        }

        return keys.ToImmutable();
    }
}
