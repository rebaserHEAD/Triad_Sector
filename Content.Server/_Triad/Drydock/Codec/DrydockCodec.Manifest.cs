using System.Collections;
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

/// <summary>A manifest member a store could not write: the entity's prototype, the member, and what the serializer threw.</summary>
public sealed record DrydockUnwritableMember(string? Prototype, DrydockManifestMember Member, string Exception, string Message)
{
    public override string ToString() => $"{Member.Key} on {Prototype ?? "(no prototype)"}: {Exception}: {Message}";
}

/// <summary>
/// The manifest's half of the codec (<see cref="DrydockCodecManifestMembers"/>): the members that are not data fields,
/// written beside an entity's component rows in one row of their own and read back for the loader to set at each
/// member's moment. The codec writes and decodes; when a value goes in, and through what, is the loader's.
/// </summary>
public sealed partial class DrydockCodec
{
    /// <summary>The row an entity's manifest members travel in, keyed <c>Component.Member</c>.</summary>
    public const string ManifestRow = "~manifest";

    /// <summary>What a <see cref="DrydockMemberKind.Rederive"/> member's row holds: only that it is owed.</summary>
    private const string RederiveMarker = "~rederive";

    private static readonly ILookup<string, DrydockManifestMember> MembersByComponent =
        DrydockCodecManifestMembers.Members.ToLookup(member => member.Component, StringComparer.Ordinal);

    private static readonly Dictionary<string, DrydockManifestMember> MembersByKey =
        DrydockCodecManifestMembers.Members.ToDictionary(member => member.Key, StringComparer.Ordinal);

    /// <summary>
    /// An entity's manifest members, or null when it carries none. A member the codec carries already is not written
    /// again (<see cref="DrydockMemberKind.ReapplyCarried"/>), nor is one owed with a handler that does not exist yet
    /// (<see cref="DrydockManifestMember.OwedWith"/>), nor one whose <see cref="DrydockManifestMember.SkipWhen"/> flag is
    /// set, nor a dictionary entry the dictionary does not hold. A member the loader works out again
    /// (<see cref="DrydockMemberKind.Rederive"/>) is written as a marker and read back as null. A null value is
    /// written as an explicit null, so the read sets it rather than leaving whatever non-null default the prototype gave
    /// the new component. A value no serializer can write is added to <paramref name="unwritable"/>, named, and left out.
    ///
    /// <para>OWED at the store: leaving one out loses it, so the store is to refuse the whole hull instead, naming the
    /// entity and the member, as it is to for a component write that throws and for an entity stored before its map
    /// init. The build-time test writes a sample of every type <see cref="WrittenAs"/> gives, so this is the
    /// backstop.</para>
    /// </summary>
    public MappingDataNode? WriteManifest(
        Entity<MetaDataComponent> entity,
        IEnumerable<IComponent> components,
        IComponentFactory factory,
        ICollection<DrydockUnwritableMember> unwritable)
    {
        MappingDataNode? row = null;
        var carried = components.ToList();
        var names = carried.Select(c => factory.GetComponentName(c.GetType())).ToHashSet(StringComparer.Ordinal);
        foreach (var component in carried)
        {
            var name = factory.GetComponentName(component.GetType());
            foreach (var member in MembersByComponent[name])
            {
                if (member.Kind == DrydockMemberKind.ReapplyCarried || member.OwedWith != null || member.OnlyWith is { } with && !names.Contains(with))
                    continue;

                if (member.SkipWhen is { } flag
                    && DrydockCodecManifestMembers.Resolve(component.GetType(), flag) is { } flagInfo
                    && Get(flagInfo, component) is true)
                {
                    continue;
                }

                var info = DrydockCodecManifestMembers.Resolve(component.GetType(), member.Member)
                           ?? throw new InvalidOperationException($"Drydock codec: {member.Key} is in the manifest and not on the component.");

                // Owed, not carried: the loader works it out again from the loaded entity.
                if (member.Kind == DrydockMemberKind.Rederive)
                {
                    (row ??= new MappingDataNode())[member.Key] = new ValueDataNode(RederiveMarker);
                    continue;
                }

                var value = Get(info, component);
                if (member.EntryKey is { } entryKey)
                {
                    if (value is not IDictionary entries || !entries.Contains(entryKey))
                        continue;

                    value = entries[entryKey];
                }

                DataNode node;
                try
                {
                    node = WriteMember(entity, member, info, value);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    unwritable.Add(new DrydockUnwritableMember(entity.Comp.EntityPrototype?.ID, member, e.GetType().Name, e.Message));
                    continue;
                }

                (row ??= new MappingDataNode())[member.Key] = node;
            }
        }

        return row;
    }

    private DataNode WriteMember(Entity<MetaDataComponent> entity, DrydockManifestMember member, MemberInfo info, object? value)
    {
        if (value == null)
            return ValueDataNode.Null();

        if (member.Kind == DrydockMemberKind.AbsoluteTime)
            return _pass.WriteTime(entity, (TimeSpan) value);

        var type = WrittenAs(member, info)
                   ?? throw new InvalidOperationException($"Drydock codec: {member.Key} is a {member.Kind}, which the manifest does not write.");

        return _serialization.WriteValue(type, value switch
        {
            // A receiver's provider is kept as the provider entity, so it travels as a stable id like every reference.
            Entity<ExtensionCableProviderComponent> provider => provider.Owner,
            // A network id means nothing in another round, so the entity it names travels, as a stable id.
            NetEntity net when member.Kind == DrydockMemberKind.Reference => _entMan.GetEntity(net),
            _ => value,
        }, alwaysWrite: true, context: Context);
    }

    /// <summary>
    /// The type the manifest hands the serializer for a member's value, or null when it hands it none: a re-applied member
    /// is not written, a re-derived one is written as a marker and a time goes through the time-offset adapter. A network
    /// reference and a cable receiver's provider travel as the entity they name. The build-time test writes a sample of
    /// every type this gives.
    /// </summary>
    public static Type? WrittenAs(DrydockManifestMember member, MemberInfo info)
    {
        if (member.Kind is DrydockMemberKind.ReapplyCarried or DrydockMemberKind.Rederive or DrydockMemberKind.AbsoluteTime)
            return null;

        var type = TypeOf(member, info);
        return member.Kind == DrydockMemberKind.Reference || (Nullable.GetUnderlyingType(type) ?? type) == typeof(Entity<ExtensionCableProviderComponent>)
            ? typeof(EntityUid)
            : type;
    }

    /// <summary>The type a member's value travels as: the entry's for a dictionary entry, the member's own otherwise.</summary>
    private static Type TypeOf(DrydockManifestMember member, MemberInfo info) =>
        member.EntryType ?? DrydockCodecManifestMembers.MemberType(info);

    /// <summary>
    /// The members of <paramref name="moment"/> a manifest row holds, decoded: a reference resolves through the load's
    /// stable ids, a game time against the clock at load, an explicit null to null. A network id comes back as the loaded
    /// entity's own, or null when it named nothing on the image. A member set through its system (a receiver's provider, a
    /// pinpointer's target) comes back as the entity, for the loader to hand to that system.
    /// </summary>
    public IEnumerable<(DrydockManifestMember Member, object? Value)> ReadManifest(MappingDataNode row, DrydockApplyMoment moment, IComponentFactory factory)
    {
        foreach (var (key, node) in row)
        {
            if (!MembersByKey.TryGetValue(key, out var member))
                throw new FormatException($"Drydock codec: a manifest row carries '{key}', which the manifest does not list.");

            if (member.Moment != moment)
                continue;

            if (node is ValueDataNode { IsNull: true } || member.Kind == DrydockMemberKind.Rederive)
            {
                yield return (member, null);
                continue;
            }

            var type = TypeOf(member, DrydockCodecManifestMembers.Resolve(factory.GetRegistration(member.Component).Type, member.Member)!);

            object? value;
            if (member.Kind == DrydockMemberKind.AbsoluteTime)
                value = _pass.ReadTime((ValueDataNode) node);
            else if (member.Kind == DrydockMemberKind.Reference)
                value = (EntityUid) _serialization.Read(typeof(EntityUid), node, context: Context, notNullableOverride: true)! is { Valid: true } named
                    ? _entMan.GetNetEntity(named)
                    : null;
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

    /// <summary>
    /// Sets a manifest member on a component: a field or property, public or not, on the type or a base, or one entry of a
    /// dictionary member.
    /// </summary>
    public static void SetMember(IComponent component, DrydockManifestMember member, object? value)
    {
        var info = DrydockCodecManifestMembers.Resolve(component.GetType(), member.Member);
        if (member.EntryKey is { } entryKey)
        {
            if (info == null || Get(info, component) is not IDictionary entries)
                throw new InvalidOperationException($"Drydock codec: {member.Key} is not an entry of a dictionary on the component.");

            entries[entryKey] = value;
            return;
        }

        switch (info)
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

    /// <summary>Reads a manifest member off a component; for a dictionary entry, its value, or null when it is absent.</summary>
    public static object? GetMember(IComponent component, DrydockManifestMember member)
    {
        var value = Get(DrydockCodecManifestMembers.Resolve(component.GetType(), member.Member)!, component);
        if (member.EntryKey is not { } entryKey)
            return value;

        return value is IDictionary entries && entries.Contains(entryKey) ? entries[entryKey] : null;
    }

    private static object? Get(MemberInfo info, object target) => info switch
    {
        FieldInfo field => field.GetValue(target),
        PropertyInfo property => property.GetValue(target),
        _ => null,
    };
}
