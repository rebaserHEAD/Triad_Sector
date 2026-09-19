using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using Content.Server.Power.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.Definition;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// The manifest's half of the codec (<see cref="DrydockCodecManifestMembers"/>): the members that are not data fields,
/// written beside an entity's component rows in one row of their own and read back for the loader to set at each
/// member's moment. The codec writes and decodes; when a value goes in, and through what, is the loader's.
/// </summary>
public sealed partial class DrydockCodec
{
    /// <summary>The row an entity's manifest members travel in, keyed <c>Component.Member</c>.</summary>
    public const string ManifestRow = "~manifest";

    private static readonly ILookup<string, DrydockManifestMember> MembersByComponent =
        DrydockCodecManifestMembers.Members.ToLookup(member => member.Component, StringComparer.Ordinal);

    private static readonly Dictionary<string, DrydockManifestMember> MembersByKey =
        DrydockCodecManifestMembers.Members.ToDictionary(member => $"{member.Component}.{member.Member}", StringComparer.Ordinal);

    /// <summary>
    /// An entity's manifest members, or null when it carries none. A member the codec carries already is not written
    /// again (<see cref="DrydockMemberKind.ReapplyCarried"/>). A null value is written as an explicit null, so the read
    /// sets it rather than leaving whatever non-null default the prototype gave the new component. A value no serializer
    /// can write is counted in <paramref name="unwritable"/> by its type and left out.
    /// </summary>
    public MappingDataNode? WriteManifest(
        Entity<MetaDataComponent> entity,
        IEnumerable<IComponent> components,
        IComponentFactory factory,
        Dictionary<string, int> unwritable)
    {
        MappingDataNode? row = null;
        var carried = components.ToList();
        var names = carried.Select(c => factory.GetComponentName(c.GetType())).ToHashSet(StringComparer.Ordinal);
        foreach (var component in carried)
        {
            var name = factory.GetComponentName(component.GetType());
            foreach (var member in MembersByComponent[name])
            {
                if (member.Kind == DrydockMemberKind.ReapplyCarried || member.OnlyWith is { } with && !names.Contains(with))
                    continue;

                var info = DrydockCodecManifestMembers.Resolve(component.GetType(), member.Member)
                           ?? throw new InvalidOperationException($"Drydock codec: {name}.{member.Member} is in the manifest and not on the component.");

                DataNode node;
                try
                {
                    node = WriteMember(entity, member, info, Get(info, component));
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    var type = DrydockCodecManifestMembers.MemberType(info).Name;
                    unwritable[type] = unwritable.GetValueOrDefault(type) + 1;
                    continue;
                }

                (row ??= new MappingDataNode())[$"{name}.{member.Member}"] = node;
            }
        }

        return row;
    }

    private DataNode WriteMember(Entity<MetaDataComponent> entity, DrydockManifestMember member, MemberInfo info, object? value)
    {
        if (value == null)
            return ValueDataNode.Null();

        return value switch
        {
            // A receiver's provider is kept as the provider entity, so it travels as a stable id like every reference.
            Entity<ExtensionCableProviderComponent> provider => _serialization.WriteValue(typeof(EntityUid), provider.Owner, alwaysWrite: true, context: Context),
            TimeSpan time when member.Kind == DrydockMemberKind.AbsoluteTime => _pass.WriteTime(entity, time),
            _ => _serialization.WriteValue(DrydockCodecManifestMembers.MemberType(info), value, alwaysWrite: true, context: Context),
        };
    }

    /// <summary>
    /// The members of <paramref name="moment"/> a manifest row holds, decoded: a reference resolves through the load's
    /// stable ids, a game time against the clock at load, an explicit null to null. A receiver's provider comes back as the
    /// provider entity, for the loader to pair through the cable system.
    /// </summary>
    public IEnumerable<(DrydockManifestMember Member, object? Value)> ReadManifest(MappingDataNode row, DrydockApplyMoment moment, IComponentFactory factory)
    {
        foreach (var (key, node) in row)
        {
            if (!MembersByKey.TryGetValue(key, out var member))
                throw new FormatException($"Drydock codec: a manifest row carries '{key}', which the manifest does not list.");

            if (member.Moment != moment)
                continue;

            if (node is ValueDataNode { IsNull: true })
            {
                yield return (member, null);
                continue;
            }

            var type = DrydockCodecManifestMembers.MemberType(
                DrydockCodecManifestMembers.Resolve(factory.GetRegistration(member.Component).Type, member.Member)!);

            object? value;
            if (member.Kind == DrydockMemberKind.AbsoluteTime)
                value = _pass.ReadTime((ValueDataNode) node);
            else if (member.Kind == DrydockMemberKind.ViaSystem && type != typeof(string))
                value = _serialization.Read(typeof(EntityUid), node, context: Context, notNullableOverride: true);
            else
                value = _serialization.Read(type, node, context: Context);

            yield return (member, value);
        }
    }

    private static readonly ConcurrentDictionary<Type, string[]> NotCarriedKeys = new();

    /// <summary>
    /// Takes out of a component's row the data fields the manifest lists as not carried (a grid's alerter list, which its
    /// power handler rebuilds and would otherwise double). A not-carried member that is not a data field was never written.
    /// </summary>
    private void RemoveNotCarried(Type type, MappingDataNode mapping)
    {
        var keys = NotCarriedKeys.GetOrAdd(type, t =>
        {
            var name = _factory.GetComponentName(t);
            return DrydockCodecManifestMembers.NotCarried
                .Where(entry => entry.Component == name)
                .Select(entry => DrydockCodecManifestMembers.Resolve(t, entry.Member)?.GetCustomAttribute<DataFieldAttribute>() is { } field
                    ? field.Tag ?? DataDefinitionUtility.AutoGenerateTag(entry.Member)
                    : null)
                .OfType<string>()
                .ToArray();
        });

        foreach (var key in keys)
            mapping.Remove(key);
    }

    /// <summary>Sets a manifest member on a component: a field or property, public or not, on the type or a base.</summary>
    public static void SetMember(IComponent component, DrydockManifestMember member, object? value)
    {
        switch (DrydockCodecManifestMembers.Resolve(component.GetType(), member.Member))
        {
            case FieldInfo field:
                field.SetValue(component, value);
                break;
            case PropertyInfo { CanWrite: true } property:
                property.SetValue(component, value);
                break;
            case PropertyInfo property:
                throw new InvalidOperationException($"Drydock codec: {member.Component}.{member.Member} has no setter; set it through its system.");
            default:
                throw new InvalidOperationException($"Drydock codec: {member.Component}.{member.Member} is in the manifest and not on the component.");
        }
    }

    /// <summary>Reads a manifest member off a component.</summary>
    public static object? GetMember(IComponent component, DrydockManifestMember member) =>
        Get(DrydockCodecManifestMembers.Resolve(component.GetType(), member.Member)!, component);

    private static object? Get(MemberInfo info, object target) => info switch
    {
        FieldInfo field => field.GetValue(target),
        PropertyInfo property => property.GetValue(target),
        _ => null,
    };
}
