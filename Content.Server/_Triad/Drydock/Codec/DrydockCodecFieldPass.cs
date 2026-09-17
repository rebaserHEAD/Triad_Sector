using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.Definition;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// One pass over a component's data fields, run after the engine's generated writer and after its
/// generated reader, fixing the handful of fields whose engine serializer answers a question the
/// codec did not ask.
///
/// <para>Why a pass and not a set of serializers. The context-sensitive fields below are handled by
/// engine serializers that branch on the calling context, and neither door into that branch is open
/// to us. A named <c>customTypeSerializer</c> resolves from the manager's own type-keyed cache and
/// never consults a context at all
/// (<c>RobustToolbox/Robust.Shared/Serialization/Manager/SerializationManager.Writing.cs:298-302</c>,
/// <c>SerializationManager.CustomSerializers.cs:8-13</c>), so registering an adapter on
/// <see cref="DrydockCodecContext"/> would not be reached. Driving the engine's own
/// <c>EntitySerializer</c> instead fails at its constructor, which registers itself into its own
/// sealed provider (<c>EntitySerializer.cs:38</c>, <c>EntityDeserializer.cs:30</c>). So the
/// correction happens once, by field, here.</para>
///
/// <para>A field only appears in this pass if it hits one of the cases in
/// <see cref="FieldCase"/>. Everything else is the engine's and is never touched, which is the point:
/// the pass is a short, enumerable list of exceptions rather than a second serializer.</para>
///
/// <para><see cref="FieldCase.ReadOnly"/> is the one case that recurses. A <c>readOnly</c> field is
/// read from YAML and never written back, and the generator enforces that by skipping the field in
/// the writer it emits (<c>RobustToolbox/Robust.Serialization.Generator/Generator.cs:1013</c>; it
/// skips them in equality at <c>:1164</c> as well, and nowhere in the reader, which is why the read
/// half needs nothing here). For a prototype that is right, because the value is setup data. For an
/// image it is a hole: the live entity holds a value nothing will write. So the pass writes those
/// fields itself, per field, wherever they sit: on the component, inside a data definition it holds,
/// and inside the elements of a collection it holds, because a definition is just as reachable
/// through a dictionary value as through a field.</para>
/// </summary>
public sealed class DrydockCodecFieldPass
{
    private const BindingFlags MemberFlags = BindingFlags.Instance
                                             | BindingFlags.Public
                                             | BindingFlags.NonPublic
                                             | BindingFlags.DeclaredOnly;

    private readonly ISerializationManager _serialization;
    private readonly DrydockCodecContext _context;
    private readonly IEntityManager _entMan;
    private readonly IGameTiming _timing;
    private readonly MetaDataSystem _metaData;

    private readonly ConcurrentDictionary<Type, ImmutableArray<Entry>> _cache = new();
    private readonly ConcurrentDictionary<Type, ImmutableArray<Entry>> _readOnlyCache = new();
    private readonly ConcurrentDictionary<Type, ImmutableArray<Computed>> _computedCache = new();
    private readonly ConcurrentDictionary<(Type Value, Type Serializer), MethodInfo?> _writers = new();

    public DrydockCodecFieldPass(
        ISerializationManager serialization,
        DrydockCodecContext context,
        IEntityManager entMan,
        IGameTiming timing)
    {
        _serialization = serialization;
        _context = context;
        _entMan = entMan;
        _timing = timing;
        _metaData = entMan.System<MetaDataSystem>();
    }

    /// <summary>
    /// Runs on the mapping the engine's whole-component writer produced, in place.
    /// </summary>
    public void AfterWrite(Entity<MetaDataComponent> entity, IComponent component, MappingDataNode mapping)
    {
        var entries = EntriesFor(component.GetType());
        var computedFields = ComputedFor(component.GetType());
        if (entries.Length == 0 && computedFields.Length == 0)
            return;

        // The engine's write subtracts the clock reading at which the entity paused. Content cannot
        // see that field, so reconstruct it: GetPauseTime is how long it has been paused.
        var pauseTime = entity.Comp.EntityPaused
            ? _timing.CurTime - _metaData.GetPauseTime(entity.Owner, entity.Comp)
            : (TimeSpan?) null;

        var walk = new Walk(entity.Comp.EntityLifeStage, _timing.CurTime, pauseTime);

        foreach (var entry in entries)
        {
            switch (entry.Case)
            {
                case FieldCase.TimeOffset:
                    WriteTimeOffset(entry, component, mapping, walk);
                    break;

                case FieldCase.GridChunks:
                    // Tiles are stored as our own tilemap and chunk rows. The engine's chunk
                    // serializer resolves tile ids through a tilemap only its own loader builds, and
                    // writing the chunks here would store the same tiles a second time in a form
                    // nothing can read back.
                    mapping.Remove(entry.Key);
                    break;

                case FieldCase.GridFixtures:
                    // Only on a grid: there the fixture set is derived from the tiles, and
                    // SharedGridFixtureSystem rebuilds and re-announces it on load
                    // (RobustToolbox/Robust.Shared/GameObjects/Systems/SharedGridFixtureSystem.cs:69-117).
                    // On anything else a fixture is authored content and stays.
                    if (_entMan.HasComponent<MapGridComponent>(entity.Owner))
                        mapping.Remove(entry.Key);
                    break;

                case FieldCase.ReadOnly:
                    WriteReadOnly(entry, component, mapping, walk, component.GetType().Name);
                    break;
            }
        }

        foreach (var computed in computedFields)
        {
            // The engine wrote the computed field, and what it wrote is the getter's answer, which
            // the setter will refuse. The backing member goes in its place, under its own name.
            mapping.Remove(computed.ComputedKey);

            switch (computed.Case)
            {
                case FieldCase.TimeOffset:
                    if (GetValue(computed.Backing, component) is TimeSpan deadline)
                        mapping[computed.BackingKey] = DrydockTimeOffsetAdapter.Write(deadline, walk.LifeStage, walk.CurTime, walk.PauseTime);
                    break;

                default:
                    throw new InvalidOperationException($"Drydock codec: {Name(computed.Backing)} is a backing member the pass has no rule for writing as {computed.Case}.");
            }
        }
    }

    /// <summary>
    /// Runs on the component the engine's reader produced, against the mapping it was read from.
    /// </summary>
    /// <remarks>
    /// Only the time-offset case has a read half. The two dropped fields are re-derived rather than
    /// restored: chunks come back from our own tables, and the grid's fixtures are rebuilt from
    /// those chunks, so both are correct by having been left alone. A <c>readOnly</c> field needs no
    /// read half either: the generator's skip is in the writer and in equality, never in the reader,
    /// so what this pass wrote is read back by the engine's own generated reader.
    /// </remarks>
    public void AfterRead(IComponent component, MappingDataNode mapping)
    {
        foreach (var entry in EntriesFor(component.GetType()))
        {
            if (entry.Case != FieldCase.TimeOffset)
                continue;

            if (!mapping.TryGet<ValueDataNode>(entry.Key, out var node) || node.IsNull)
                continue;

            entry.Set(component, DrydockTimeOffsetAdapter.Read(node, _timing.CurTime));
        }

        foreach (var computed in ComputedFor(component.GetType()))
        {
            // The codec never writes the computed key, so a row carrying one was not written by us.
            // Reading it would hand the setter a value it refuses and leave the field at nothing,
            // which is the very failure the entry exists to prevent, so it fails loudly instead.
            if (mapping.Has(computed.ComputedKey))
                throw new FormatException($"Drydock codec: a stored row carries '{computed.ComputedKey}', which is a computed field the codec never writes.");

            if (computed.Case != FieldCase.TimeOffset)
                throw new InvalidOperationException($"Drydock codec: {Name(computed.Backing)} is a backing member the pass has no rule for reading as {computed.Case}.");

            if (!mapping.TryGet<ValueDataNode>(computed.BackingKey, out var backing) || backing.IsNull)
                continue;

            SetValue(computed.Backing, component, DrydockTimeOffsetAdapter.Read(backing, _timing.CurTime));
        }
    }

    private void WriteTimeOffset(Entry entry, object owner, MappingDataNode into, Walk walk)
    {
        // A null deadline means there is no deadline, which the engine already writes correctly.
        // Only a live one needs the offset.
        if (entry.Get(owner) is not TimeSpan deadline)
            return;

        into[entry.Key] = DrydockTimeOffsetAdapter.Write(deadline, walk.LifeStage, walk.CurTime, walk.PauseTime);
    }

    /// <summary>
    /// A <c>readOnly</c> member, written by us because the generated writer will not, and then
    /// walked for whatever <c>readOnly</c> members its own value holds.
    /// </summary>
    private void WriteReadOnly(Entry entry, object owner, MappingDataNode into, Walk walk, string path)
    {
        var value = entry.Get(owner);
        var node = WriteMember(entry, value, path);

        if (entry.AsymmetricKey is { } named)
            into[named] = node;
        else if (!entry.Inline)
            into[entry.Key] = node;
        else if (value != null)
            MergeInline(node, into, path);

        // Where the value landed is where its own readOnly members belong: an inline member's are
        // the owner's mapping, everything else's are the node just written.
        var inlined = entry.Inline && entry.AsymmetricKey == null;
        WalkValue(value, inlined ? into : node, walk, path);
    }

    /// <summary>
    /// Everything below a written member: a data definition by its members, a list by index, a
    /// dictionary by the key's own written form. Anything that is neither is left alone, because
    /// only a data definition can carry a <c>readOnly</c> member.
    /// </summary>
    private void WalkValue(object? value, DataNode node, Walk walk, string path)
    {
        if (value == null || value is string)
            return;

        var type = value.GetType();

        if (IsDataDefinition(type))
        {
            // A definition with nothing readOnly on it has nothing here to write, whatever shape it
            // wrote as. Asking first keeps a scalar-writing definition, of which content has
            // several, from being refused for a walk it never needed.
            var members = ReadOnlyMembersFor(type);
            if (members.Length == 0)
                return;

            if (node is not MappingDataNode mapping)
                throw new InvalidOperationException($"Drydock codec: {path} is a data definition carrying readOnly fields and wrote as {node.GetType().Name} rather than a mapping.");

            // A definition that reaches itself would otherwise walk forever. The engine cannot write
            // such a graph either, so this is loud rather than a quiet stop.
            if (!type.IsValueType && !walk.Seen.Add(value))
                throw new InvalidOperationException($"Drydock codec: the object graph loops back on itself at {path}.");

            foreach (var member in members)
            {
                switch (member.Case)
                {
                    case FieldCase.TimeOffset:
                        WriteTimeOffset(member, value, mapping, walk);
                        break;

                    case FieldCase.ReadOnly:
                        WriteReadOnly(member, value, mapping, walk, $"{path}.{Name(member.Member)}");
                        break;
                }
            }

            return;
        }

        switch (value)
        {
            case IDictionary dictionary:
                WalkDictionary(dictionary, node, walk, path);
                break;

            // A dictionary is an IEnumerable too, so this arm is only reached by everything else.
            case IEnumerable enumerable:
                WalkSequence(enumerable, node, walk, path);
                break;
        }
    }

    private void WalkDictionary(IDictionary dictionary, DataNode node, Walk walk, string path)
    {
        if (dictionary.Count == 0)
            return;

        if (node is not MappingDataNode mapping)
        {
            RefuseUnwalkable(dictionary.Values, node, path);
            return;
        }

        foreach (DictionaryEntry pair in dictionary)
        {
            if (pair.Value is not { } element || !NeedsWalking(element))
                continue;

            // The key as the manager wrote it, which is the key DictionarySerializer put in the
            // node. Asking the manager rather than calling ToString is what keeps a key type with
            // its own serializer matching.
            var keyNode = _serialization.WriteValue(pair.Key.GetType(), pair.Key, alwaysWrite: true, context: _context);
            if (keyNode is not ValueDataNode { Value: var key } || !mapping.TryGet(key, out var child))
                throw new InvalidOperationException($"Drydock codec: {path} holds an entry keyed {pair.Key} that its own written mapping has no member for.");

            WalkValue(element, child, walk, $"{path}[{key}]");
        }
    }

    private void WalkSequence(IEnumerable enumerable, DataNode node, Walk walk, string path)
    {
        if (node is not SequenceDataNode sequence)
        {
            RefuseUnwalkable(enumerable, node, path);
            return;
        }

        var index = 0;
        foreach (var element in enumerable)
        {
            if (element != null && NeedsWalking(element))
            {
                if (index >= sequence.Count)
                    throw new InvalidOperationException($"Drydock codec: {path} holds {index + 1} elements or more and its own written sequence holds {sequence.Count}.");

                WalkValue(element, sequence[index], walk, $"{path}[{index}]");
            }

            index++;
        }
    }

    /// <summary>
    /// A collection whose node this pass cannot walk. Silent is only acceptable when there was
    /// nothing below it to write.
    /// </summary>
    private void RefuseUnwalkable(IEnumerable elements, DataNode node, string path)
    {
        foreach (var element in elements)
        {
            if (element != null && NeedsWalking(element))
                throw new InvalidOperationException($"Drydock codec: {path} holds data definitions and wrote as {node.GetType().Name}, which this pass cannot walk.");
        }
    }

    /// <summary>
    /// Is there anything under this value worth walking? A definition carries members; a collection
    /// may hold definitions. Everything else is a leaf the engine already wrote.
    /// </summary>
    private bool NeedsWalking(object value) =>
        value is not string
        && (value is IEnumerable
            || (IsDataDefinition(value.GetType()) && ReadOnlyMembersFor(value.GetType()).Length > 0));

    private DataNode WriteMember(Entry entry, object? value, string path)
    {
        var declared = entry.DeclaredType
                       ?? throw new InvalidOperationException($"Drydock codec: {path} is neither a field nor a property.");

        // Through the member's own serializer when that serializer can write at all, and by the
        // member's declared type otherwise. DrydockCodecManifest.CanWrite is the one test, asked by
        // the audit as well, so the two cannot disagree about what a serializer covers.
        if (value != null
            && entry.CustomSerializer is { } serializer
            && WriterFor(declared, serializer) is { } write)
        {
            return (DataNode) write.Invoke(_serialization, new object?[] { value, true, _context, false })!;
        }

        return _serialization.WriteValue(declared, value, alwaysWrite: true, context: _context);
    }

    private static void MergeInline(DataNode node, MappingDataNode into, string path)
    {
        if (node is not MappingDataNode inline)
            throw new InvalidOperationException($"Drydock codec: {path} is an inline field, so it must write as a mapping rather than as {node.GetType().Name}.");

        // Inline is what IncludeDataField means: the pairs belong to the owner's mapping, under no
        // key of their own.
        foreach (var (key, child) in inline)
        {
            into[key] = child;
        }
    }

    /// <summary>
    /// <c>WriteValue&lt;T, TWriter&gt;</c> bound to this pair, or null when the serializer has no
    /// writer for the type.
    /// </summary>
    private MethodInfo? WriterFor(Type valueType, Type serializer) =>
        _writers.GetOrAdd((valueType, serializer), static key =>
        {
            if (!DrydockCodecManifest.CanWrite(key.Value, key.Serializer))
                return null;

            return typeof(ISerializationManager).GetMethods()
                .First(method => method.Name == nameof(ISerializationManager.WriteValue)
                                 && method.GetGenericArguments().Length == 2)
                .MakeGenericMethod(key.Value, key.Serializer);
        });

    private ImmutableArray<Entry> EntriesFor(Type componentType) =>
        _cache.GetOrAdd(componentType, static type => Build(type));

    private ImmutableArray<Entry> ReadOnlyMembersFor(Type type) =>
        _readOnlyCache.GetOrAdd(type, static walked => BuildReadOnly(walked));

    private ImmutableArray<Computed> ComputedFor(Type componentType) =>
        _computedCache.GetOrAdd(componentType, static type => BuildComputed(type));

    /// <summary>
    /// The manifest's entries for one component, resolved to members. A missing member throws here
    /// rather than going quiet, and the build-time audit catches it long before a store does.
    /// </summary>
    private static ImmutableArray<Computed> BuildComputed(Type componentType)
    {
        var entries = ImmutableArray.CreateBuilder<Computed>();

        foreach (var field in DrydockCodecManifest.ComputedFields)
        {
            if (field.Component != componentType)
                continue;

            var computed = FindMember(componentType, field.ComputedMember)
                           ?? throw new InvalidOperationException($"Drydock codec: {componentType.Name}.{field.ComputedMember} is in the computed-field manifest and does not exist.");

            var backing = FindMember(componentType, field.BackingMember)
                          ?? throw new InvalidOperationException($"Drydock codec: {componentType.Name}.{field.BackingMember} is in the computed-field manifest and does not exist.");

            var computedKey = computed.GetCustomAttribute<DataFieldAttribute>()?.Tag
                              ?? DataDefinitionUtility.AutoGenerateTag(computed.Name);

            entries.Add(new Computed(
                computedKey,
                DataDefinitionUtility.AutoGenerateTag(backing.Name),
                backing,
                field.BackingCase));
        }

        return entries.ToImmutable();
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        for (var declaring = type; declaring != null && declaring != typeof(object); declaring = declaring.BaseType)
        {
            var member = (MemberInfo?) declaring.GetField(name, MemberFlags)
                         ?? declaring.GetProperty(name, MemberFlags);

            if (member != null)
                return member;
        }

        return null;
    }

    private static ImmutableArray<Entry> Build(Type componentType)
    {
        var entries = ImmutableArray.CreateBuilder<Entry>();

        foreach (var (member, attribute) in DataMembers(componentType))
        {
            if (Classify(member, attribute) is not { } fieldCase)
                continue;

            entries.Add(EntryFor(member, attribute, fieldCase));
        }

        var built = entries.ToImmutable();

        // The grid's tiles are the one field this pass has to find rather than merely recognise.
        // Writing them through the engine's chunk serializer produces tile ids resolved against a
        // tilemap only the engine's own loader builds, so a grid that quietly stopped matching here
        // would store tiles nothing can read back.
        if (componentType == typeof(MapGridComponent))
        {
            var chunkFields = built.Count(entry => entry.Case == FieldCase.GridChunks);
            if (chunkFields != 1)
            {
                throw new InvalidOperationException(
                    $"Drydock codec: expected exactly one chunk store on {nameof(MapGridComponent)}, found {chunkFields}.");
            }
        }

        return built;
    }

    /// <summary>
    /// The members the pass owns one level below a component: the <c>readOnly</c> ones, since every
    /// other member of a nested definition was written by that definition's own generated writer.
    /// </summary>
    private static ImmutableArray<Entry> BuildReadOnly(Type type)
    {
        var entries = ImmutableArray.CreateBuilder<Entry>();

        foreach (var (member, attribute) in DataMembers(type))
        {
            if (!attribute.ReadOnly)
                continue;

            var fieldCase = Classify(member, attribute) ?? FieldCase.ReadOnly;
            if (fieldCase is FieldCase.GridChunks or FieldCase.GridFixtures)
                throw new InvalidOperationException($"Drydock codec: {Name(member)} is a grid field below a component, where one cannot occur.");

            entries.Add(EntryFor(member, attribute, fieldCase));
        }

        return entries.ToImmutable();
    }

    /// <summary>
    /// Walked one declaring type at a time, because a base type's private and internal members are
    /// not returned by a query on the derived type, and <c>MapGridComponent.Chunks</c> is internal.
    /// </summary>
    private static IEnumerable<(MemberInfo Member, DataFieldBaseAttribute Attribute)> DataMembers(Type type)
    {
        for (var declaring = type; declaring != null && declaring != typeof(object); declaring = declaring.BaseType)
        {
            var members = declaring.GetFields(MemberFlags).Cast<MemberInfo>().Concat(declaring.GetProperties(MemberFlags));

            foreach (var member in members)
            {
                if (member.GetCustomAttribute<DataFieldBaseAttribute>() is { } attribute)
                    yield return (member, attribute);
            }
        }
    }

    private static Entry EntryFor(MemberInfo member, DataFieldBaseAttribute attribute, FieldCase fieldCase)
    {
        // IncludeDataField inlines its target's members under no key of its own, so it has no tag to
        // take a key from and none is invented for it.
        var inline = attribute is not DataFieldAttribute;
        var key = attribute is DataFieldAttribute data
            ? data.Tag ?? DataDefinitionUtility.AutoGenerateTag(member.Name)
            : string.Empty;

        if (inline && fieldCase != FieldCase.ReadOnly)
            throw new InvalidOperationException($"Drydock codec: {Name(member)} is an inline field, and the pass has no rule for one carrying {fieldCase}.");

        return new Entry(
            key,
            member,
            fieldCase,
            MemberType(member),
            attribute.CustomTypeSerializer,
            inline,
            DrydockCodecManifest.AsymmetricKey(member));
    }

    private static FieldCase? Classify(MemberInfo member, DataFieldBaseAttribute data)
    {
        if (data.CustomTypeSerializer == typeof(TimeOffsetSerializer))
            return FieldCase.TimeOffset;

        if (data.CustomTypeSerializer == typeof(FixtureSerializer))
            return FieldCase.GridFixtures;

        if (member.DeclaringType == typeof(MapGridComponent) && IsChunkStore(MemberType(member)))
            return FieldCase.GridChunks;

        // Last, so a field that is readOnly as well as one of the above is handled as the above: the
        // three named cases all write the key themselves, which is what a readOnly field needed.
        if (data.ReadOnly)
            return FieldCase.ReadOnly;

        return null;
    }

    /// <summary>
    /// The grid's tiles: a store keyed by chunk index. Spelled by its key type rather than by its
    /// value type or by a field name, because <c>MapChunk</c> is internal to the engine and cannot be
    /// named from content, and a name comparison would go quiet on a rename instead of failing.
    /// <see cref="Build"/> asserts the match is found.
    /// </summary>
    private static bool IsChunkStore(Type? type) =>
        type is { IsGenericType: true }
        && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
        && type.GetGenericArguments()[0] == typeof(Vector2i);

    private static bool IsDataDefinition(Type type) =>
        type.GetCustomAttribute<DataDefinitionAttribute>() != null;

    private static Type? MemberType(MemberInfo member) => member switch
    {
        FieldInfo field => field.FieldType,
        PropertyInfo property => property.PropertyType,
        _ => null,
    };

    private static string Name(MemberInfo member) => $"{member.DeclaringType?.Name}.{member.Name}";

    /// <summary>
    /// The exceptions this pass knows about. Each one is a field the engine writes correctly for a
    /// map file and incorrectly for an image row. Public because
    /// <see cref="DrydockCodecManifest.ComputedFields"/> names the case a backing field is written
    /// through.
    /// </summary>
    public enum FieldCase : byte
    {
        TimeOffset,
        GridChunks,
        GridFixtures,
        ReadOnly,
    }

    /// <summary>
    /// The state one component's write shares with everything under it: the owning entity's pause
    /// state, which a deadline at any depth is measured against, and what the walk has already seen.
    /// </summary>
    private sealed class Walk(EntityLifeStage lifeStage, TimeSpan curTime, TimeSpan? pauseTime)
    {
        public readonly EntityLifeStage LifeStage = lifeStage;
        public readonly TimeSpan CurTime = curTime;
        public readonly TimeSpan? PauseTime = pauseTime;
        public readonly HashSet<object> Seen = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>One manifest entry, resolved against the component it names.</summary>
    private readonly record struct Computed(string ComputedKey, string BackingKey, MemberInfo Backing, FieldCase Case);

    private static object? GetValue(MemberInfo member, object target) => member switch
    {
        FieldInfo field => field.GetValue(target),
        PropertyInfo property => property.GetValue(target),
        _ => null,
    };

    private static void SetValue(MemberInfo member, object target, object? value)
    {
        switch (member)
        {
            case FieldInfo field:
                field.SetValue(target, value);
                break;
            case PropertyInfo property:
                property.SetValue(target, value);
                break;
        }
    }

    private readonly record struct Entry(
        string Key,
        MemberInfo Member,
        FieldCase Case,
        Type? DeclaredType = null,
        Type? CustomSerializer = null,
        bool Inline = false,
        string? AsymmetricKey = null)
    {
        public object? Get(object target) => GetValue(Member, target);

        public void Set(object target, object? value) => SetValue(Member, target, value);
    }
}
