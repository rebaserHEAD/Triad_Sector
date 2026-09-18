using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Serialization;
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
///
/// <para>A time-offset field gets the same reach, on both halves. Below the component level the
/// engine does write one, through a serializer that answers zero to every caller but its own, so a
/// nested deadline is not skewed but gone; the walk descends to it for the same reason it descends
/// to a <c>readOnly</c> field, and the read walk mirrors the write walk to put the value back.</para>
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
    private readonly ConcurrentDictionary<Type, ImmutableArray<Entry>> _nestedCache = new();
    private readonly ConcurrentDictionary<Type, ImmutableArray<Computed>> _computedCache = new();
    private readonly ConcurrentDictionary<(Type Value, Type Serializer), MethodInfo?> _writers = new();

    /// <summary>
    /// Reachability by declared type, which is a fact about the code rather than about a codec, so
    /// it is shared and answered once per type.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, bool> CarriesCorrectionCache = new();

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

        var walk = new WalkState(entity.Comp.EntityLifeStage, _timing.CurTime, pauseTime);

        foreach (var entry in entries)
        {
            switch (entry.Case)
            {
                case FieldCase.TimeOffset:
                    WriteTimeOffset(entry, component, mapping, walk);
                    break;

                case FieldCase.GridChunks:
                    // Tiles are stored apart, in DrydockTileTable under the image's own tile ids. The
                    // engine's chunk serializer resolves tile ids through a tilemap only its own
                    // loader and saver build, so the chunks written here would carry ids nothing
                    // can resolve, and store the same tiles a second time.
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

                case FieldCase.Flags:
                    WriteFlags(entry, component, mapping);
                    break;

                case FieldCase.Walk:
                    WalkMember(entry, component, mapping, walk, component.GetType().Name);
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
    /// Only the time-offset case has a read half of its own, at any depth. The two dropped fields
    /// are re-derived rather than restored: chunks come back from <see cref="DrydockTileTable"/>, and the grid's
    /// fixtures are rebuilt from those chunks, so both are correct by having been left alone. A
    /// <c>readOnly</c> field needs no read half either: the generator's skip is in the writer and in
    /// equality, never in the reader, so what this pass wrote is read back by the engine's own
    /// generated reader. The walk cases are here only as the way down to a nested time field, which
    /// the engine reads back as <see cref="TimeSpan.Zero"/> whatever the row holds.
    /// </remarks>
    public void AfterRead(IComponent component, MappingDataNode mapping)
    {
        foreach (var entry in EntriesFor(component.GetType()))
        {
            switch (entry.Case)
            {
                case FieldCase.TimeOffset:
                    ReadTimeOffset(entry, component, mapping);
                    break;

                case FieldCase.ReadOnly:
                case FieldCase.Walk:
                    ReadWalkMember(entry, component, mapping, component.GetType().Name);
                    break;
            }
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

    private void WriteTimeOffset(Entry entry, object owner, MappingDataNode into, WalkState walk)
    {
        // A null deadline means there is no deadline, which the engine already writes correctly.
        // Only a live one needs the offset.
        if (entry.Get(owner) is not TimeSpan deadline)
            return;

        into[entry.Key] = DrydockTimeOffsetAdapter.Write(deadline, walk.LifeStage, walk.CurTime, walk.PauseTime);
    }

    /// <summary>
    /// Finding F35. <c>FlagSerializer.Write</c> starts its bit loop at 1
    /// (<c>RobustToolbox/Robust.Shared/Serialization/TypeSerializers/Implementations/Custom/FlagSerializer.cs:73</c>),
    /// so bit 0 is never written: an airtight wall's <c>All</c> (15) writes as South, East, West and
    /// reads back as 14. Enums with a value using bit 31, such as <c>CollisionGroup.AllMask = -1</c>,
    /// escape by accident: their highest bit is 32, and <c>1 &lt;&lt; 32</c> is <c>1 &lt;&lt; 0</c>
    /// in C#. The key is rewritten with every set bit named, bit 0 included; the engine's own reader
    /// ORs the names back, so only the write is ours.
    /// </summary>
    private void WriteFlags(Entry entry, object owner, MappingDataNode into)
    {
        if (entry.Get(owner) is not int value)
            return;

        var tag = entry.CustomSerializer!.GetGenericArguments()[0];
        into[entry.Key] = FlagNode(_serialization.GetFlagTypeFromTag(tag), value);
    }

    /// <summary>
    /// The engine's flag layout with the lost bit put back: a sequence of names, one per set bit, or
    /// the named whole for -1 as the engine writes it. A set bit the enum has no name for would be
    /// dropped by a sequence, so the value is written as its decimal instead, which the serializer's
    /// scalar reader parses (<c>FlagSerializer.cs:39-45</c>).
    /// </summary>
    internal static DataNode FlagNode(Type flagType, int value)
    {
        if (value == -1 && Enum.GetName(flagType, -1) is { } whole)
            return new SequenceDataNode { new ValueDataNode(whole) };

        var names = new SequenceDataNode();
        for (var bit = 0; bit < 32; bit++)
        {
            var bitValue = 1 << bit;
            if ((value & bitValue) == 0)
                continue;

            if (Enum.GetName(flagType, bitValue) is not { } name)
                return new ValueDataNode(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            names.Add(new ValueDataNode(name));
        }

        return names;
    }

    internal static bool IsFlagSerializer(Type? serializer) =>
        serializer is { IsGenericType: true } && serializer.GetGenericTypeDefinition() == typeof(FlagSerializer<>);

    /// <summary>The read half of <see cref="WriteTimeOffset"/>: one branch, against the clock at load.</summary>
    private void ReadTimeOffset(Entry entry, object owner, MappingDataNode from)
    {
        if (!from.TryGet<ValueDataNode>(entry.Key, out var node) || node.IsNull)
            return;

        entry.Set(owner, DrydockTimeOffsetAdapter.Read(node, _timing.CurTime));
    }

    /// <summary>
    /// The read mirror of <see cref="WalkMember"/> and of the walk <see cref="WriteReadOnly"/>
    /// finishes with: down through the value the engine's reader built, against the row it built it
    /// from, to the time fields that reader restored as zero.
    /// </summary>
    private void ReadWalkMember(Entry entry, object owner, MappingDataNode from, string path)
    {
        // The asymmetric member's value is a flat dictionary its own reader already rebuilt from the
        // key it consumes; nothing in it is a definition.
        if (entry.Asymmetric != null)
            return;

        if (entry.Get(owner) is not { } value)
            return;

        var node = entry.Inline
            ? from
            : from.TryGet(entry.Key, out var stored) ? stored : null;

        if (node == null)
            return;

        ReadWalkValue(value, node, $"{path}.{entry.Member.Name}");

        // A struct came out of the member as a boxed copy, so the corrections the walk made are in
        // the box and not in the owner until it goes back.
        if (value.GetType().IsValueType)
            entry.Set(owner, value);
    }

    /// <remarks>
    /// No cycle guard, unlike the write walk, because the row bounds it rather than the object graph:
    /// every step goes down to a child node of the row, and only an inline member stays on the same
    /// node, as a different type the engine could not have written if it held itself. However the
    /// values underneath are shared or looped, the walk ends where the row does. A shape the row does not match is
    /// corruption of our own write, so it is a <see cref="FormatException"/>, the read posture the
    /// context takes.
    /// </remarks>
    private void ReadWalkValue(object? value, DataNode node, string path)
    {
        if (value == null || value is string)
            return;

        var type = value.GetType();

        if (IsDataDefinition(type))
        {
            var members = NestedMembersFor(type);
            if (members.Length == 0)
                return;

            if (node is not MappingDataNode mapping)
                throw new FormatException($"Drydock codec: {path} is a data definition the pass corrects, and its row holds {node.GetType().Name} rather than a mapping.");

            foreach (var member in members)
            {
                switch (member.Case)
                {
                    case FieldCase.TimeOffset:
                        ReadTimeOffset(member, value, mapping);
                        break;

                    case FieldCase.ReadOnly:
                    case FieldCase.Walk:
                        ReadWalkMember(member, value, mapping, path);
                        break;
                }
            }

            return;
        }

        switch (value)
        {
            case IDictionary dictionary:
                ReadWalkDictionary(dictionary, node, path);
                break;

            // A dictionary is an IEnumerable too, so this arm is only reached by everything else.
            case IEnumerable enumerable:
                ReadWalkSequence(enumerable, node, path);
                break;
        }
    }

    private void ReadWalkDictionary(IDictionary dictionary, DataNode node, string path)
    {
        if (dictionary.Count == 0 || node is not MappingDataNode mapping)
            return;

        // Structs are corrected in a boxed copy and written back once the enumeration is done,
        // rather than while it runs.
        List<(object Key, object Value)>? writeBack = null;

        foreach (DictionaryEntry pair in dictionary)
        {
            if (pair.Value is not { } element || !NeedsWalking(element))
                continue;

            var keyNode = _serialization.WriteValue(pair.Key.GetType(), pair.Key, alwaysWrite: true, context: _context);
            if (keyNode is not ValueDataNode { Value: var key } || !mapping.TryGet(key, out var child))
                throw new FormatException($"Drydock codec: {path} holds an entry keyed {pair.Key} that its row has no member for.");

            ReadWalkValue(element, child, $"{path}[{key}]");

            if (element.GetType().IsValueType)
                (writeBack ??= new()).Add((pair.Key, element));
        }

        if (writeBack == null)
            return;

        foreach (var (key, value) in writeBack)
        {
            dictionary[key] = value;
        }
    }

    private void ReadWalkSequence(IEnumerable enumerable, DataNode node, string path)
    {
        if (node is not SequenceDataNode sequence)
            return;

        List<(int Index, object Value)>? writeBack = null;

        var index = 0;
        foreach (var element in enumerable)
        {
            if (element != null && NeedsWalking(element))
            {
                if (index >= sequence.Count)
                    throw new FormatException($"Drydock codec: {path} holds {index + 1} elements or more and its row holds {sequence.Count}.");

                ReadWalkValue(element, sequence[index], $"{path}[{index}]");

                if (element.GetType().IsValueType)
                    (writeBack ??= new()).Add((index, element));
            }

            index++;
        }

        if (writeBack == null)
            return;

        // A struct corrected in a set, or anything else without an index, has nowhere to go back
        // to. That is refused rather than left as the zero the engine read, which is the loss this
        // walk exists to prevent.
        if (enumerable is not IList list)
            throw new InvalidOperationException($"Drydock codec: {path} holds value-type elements the pass corrected, in a {enumerable.GetType().Name} it cannot write them back into.");

        foreach (var (at, value) in writeBack)
        {
            list[at] = value;
        }
    }

    /// <summary>
    /// A <c>readOnly</c> member, written by us because the generated writer will not, and then
    /// walked for whatever <c>readOnly</c> members its own value holds.
    /// </summary>
    private void WriteReadOnly(Entry entry, object owner, MappingDataNode into, WalkState walk, string path)
    {
        var value = entry.Get(owner);
        var node = WriteMember(entry, value, path);

        if (entry.Asymmetric is { } asymmetric)
        {
            into[asymmetric.Key] = node;

            // The other keys this member's reader consumes. The live value already holds everything
            // they would contribute, so leaving one in the row has it counted a second time at every
            // read, and again at every write after that.
            foreach (var absent in asymmetric.AbsentKeys)
            {
                into.Remove(absent);
            }
        }
        else if (!entry.Inline)
        {
            into[entry.Key] = node;
        }
        else if (value != null)
        {
            MergeInline(node, into, path);
        }

        // Where the value landed is where its own readOnly members belong: an inline member's are
        // the owner's mapping, everything else's are the node just written.
        var inlined = entry.Inline && entry.Asymmetric == null;
        WalkValue(value, inlined ? into : node, walk, path);
    }

    /// <summary>
    /// A member the engine wrote correctly, descended into for the <c>readOnly</c> members its value
    /// holds. Only what the engine wrote is walked: this case never writes the member itself.
    /// </summary>
    private void WalkMember(Entry entry, object owner, MappingDataNode into, WalkState walk, string path)
    {
        if (entry.Get(owner) is not { } value)
            return;

        // An inline member's value was written into the owner's own mapping, under no key.
        var node = entry.Inline
            ? into
            : into.TryGet(entry.Key, out var written) ? written : null;

        if (node == null)
            return;

        WalkValue(value, node, walk, $"{path}.{entry.Member.Name}");
    }

    /// <summary>
    /// Everything below a written member: a data definition by its members, a list by index, a
    /// dictionary by the key's own written form. Anything that is neither is left alone, because
    /// only a data definition can carry a <c>readOnly</c> member.
    /// </summary>
    private void WalkValue(object? value, DataNode node, WalkState walk, string path)
    {
        if (value == null || value is string)
            return;

        var type = value.GetType();

        if (IsDataDefinition(type))
        {
            // A definition with nothing readOnly on it has nothing here to write, whatever shape it
            // wrote as. Asking first keeps a scalar-writing definition, of which content has
            // several, from being refused for a walk it never needed.
            var members = NestedMembersFor(type);
            if (members.Length == 0)
                return;

            if (node is not MappingDataNode mapping)
                throw new InvalidOperationException($"Drydock codec: {path} is a data definition carrying readOnly fields and wrote as {node.GetType().Name} rather than a mapping.");

            // A definition that reaches itself would otherwise walk forever. The engine cannot write
            // such a graph either, so this is loud rather than a quiet stop. The guard is the path the
            // walk is on, not everything it has visited: one instance shared from two places is a
            // graph and not a loop, and each place it sits has its own node to fill.
            var tracked = !type.IsValueType;
            if (tracked && !walk.OnPath.Add(value))
                throw new InvalidOperationException($"Drydock codec: the object graph loops back on itself at {path}.");

            try
            {
                foreach (var member in members)
                {
                    switch (member.Case)
                    {
                        case FieldCase.TimeOffset:
                            WriteTimeOffset(member, value, mapping, walk);
                            break;

                        case FieldCase.ReadOnly:
                            WriteReadOnly(member, value, mapping, walk, $"{path}.{member.Member.Name}");
                            break;

                        case FieldCase.Flags:
                            WriteFlags(member, value, mapping);
                            break;

                        case FieldCase.Walk:
                            WalkMember(member, value, mapping, walk, path);
                            break;
                    }
                }
            }
            finally
            {
                if (tracked)
                    walk.OnPath.Remove(value);
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

    private void WalkDictionary(IDictionary dictionary, DataNode node, WalkState walk, string path)
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

    private void WalkSequence(IEnumerable enumerable, DataNode node, WalkState walk, string path)
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
            || (IsDataDefinition(value.GetType()) && NestedMembersFor(value.GetType()).Length > 0));

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

    private ImmutableArray<Entry> NestedMembersFor(Type type) =>
        _nestedCache.GetOrAdd(type, static walked => BuildNested(walked));

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
    /// The members the pass owns one level below a component, by the same classification it uses at
    /// the component level: a <c>readOnly</c> member, a time-offset member, and a member that is a
    /// way down to either. Everything else of a nested definition was written correctly by that
    /// definition's own generated writer.
    ///
    /// <para>A time-offset member is owned here whether or not it is <c>readOnly</c>. The generated
    /// writer does write one, but through <c>TimeOffsetSerializer</c>, which answers a literal zero
    /// to any caller that is not an <c>EntitySerializer</c> and reads back
    /// <see cref="TimeSpan.Zero"/> for any caller that is not an <c>EntityDeserializer</c>
    /// (<c>RobustToolbox/Robust.Shared/Serialization/TypeSerializers/Implementations/Custom/TimeOffsetSerializer.cs:65-73</c>,
    /// <c>:32-36</c>). Written whole is not the same as written right: finding F27, where a do-after
    /// in progress came back having started at the beginning of time.</para>
    /// </summary>
    private static ImmutableArray<Entry> BuildNested(Type type)
    {
        var entries = ImmutableArray.CreateBuilder<Entry>();

        foreach (var (member, attribute) in DataMembers(type))
        {
            if (Classify(member, attribute) is not { } fieldCase)
                continue;

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

        if (inline && fieldCase is not (FieldCase.ReadOnly or FieldCase.Walk))
            throw new InvalidOperationException($"Drydock codec: {Name(member)} is an inline field, and the pass has no rule for one carrying {fieldCase}.");

        return new Entry(
            key,
            member,
            fieldCase,
            MemberType(member),
            attribute.CustomTypeSerializer,
            inline,
            DrydockCodecManifest.Asymmetric(member));
    }

    private static FieldCase? Classify(MemberInfo member, DataFieldBaseAttribute data)
    {
        if (data.CustomTypeSerializer == typeof(TimeOffsetSerializer))
            return FieldCase.TimeOffset;

        if (data.CustomTypeSerializer == typeof(FixtureSerializer))
            return FieldCase.GridFixtures;

        if (member.DeclaringType == typeof(MapGridComponent) && IsChunkStore(MemberType(member)))
            return FieldCase.GridChunks;

        if (IsFlagSerializer(data.CustomTypeSerializer))
            return FieldCase.Flags;

        // Last, so a field that is readOnly as well as one of the above is handled as the above: the
        // named cases all write the key themselves, which is what a readOnly field needed.
        if (data.ReadOnly)
            return FieldCase.ReadOnly;

        // Not a correction of this member, but a way down to one: a readOnly field or a time field
        // somewhere below it. Decided per declared member type and cached, so no component pays the
        // reflection walk twice.
        if (CarriesCorrection(MemberType(member)))
            return FieldCase.Walk;

        return null;
    }

    /// <summary>
    /// Does this type, or anything a collection of it holds, carry anywhere below it a field the pass
    /// must correct: a <c>readOnly</c> field, which the generated writer skips, a time-offset field,
    /// which the engine's serializer zeroes for any caller but its own, or a flag field, whose
    /// serializer loses bit 0? The question is asked of the declared type, once, because it decides
    /// whether a member is worth descending into at all, and a type that carries only a time or flag
    /// field is as much a reason to descend as one that carries a <c>readOnly</c> one (findings F27,
    /// F35).
    /// </summary>
    private static bool CarriesCorrection(Type? type)
    {
        if (type == null)
            return false;

        if (CarriesCorrectionCache.TryGetValue(type, out var known))
            return known;

        var carries = Carries(type, new HashSet<Type>());
        CarriesCorrectionCache[type] = carries;
        return carries;
    }

    /// <param name="polymorphic">
    /// Whether a polymorphic declared type counts as reachable on principle. Always true for the
    /// pass; false only so the audit can measure what that rule adds.
    /// </param>
    private static bool Carries(Type? type, HashSet<Type> seen, bool polymorphic = true)
    {
        if (IsLeaf(type) || !seen.Add(type))
            return false;

        // A collection is transparent: what matters is what it holds, and a dictionary's value type
        // is as much a way down as a field's type is.
        foreach (var held in HeldTypes(type))
        {
            if (Carries(held, seen, polymorphic))
                return true;
        }

        // Decided from the declared type, where the walk itself uses the runtime one. For a
        // polymorphic member the two differ, and the declared type carries none of its inheritors'
        // fields, so it is conservatively reachable and the runtime walk finds out what is there.
        if (polymorphic && IsPolymorphic(type))
            return true;

        if (!IsDataDefinition(type))
            return false;

        foreach (var (member, attribute) in DataMembers(type))
        {
            if (attribute.ReadOnly
                || attribute.CustomTypeSerializer == typeof(TimeOffsetSerializer)
                || IsFlagSerializer(attribute.CustomTypeSerializer)
                || Carries(MemberType(member), seen, polymorphic))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A type nothing can be below. <see cref="System.Enum"/> is here by name because it is abstract,
    /// which would make it polymorphic, while <see cref="Type.IsEnum"/> is false for it.
    /// </summary>
    private static bool IsLeaf([NotNullWhen(false)] Type? type) =>
        type == null || type.IsPrimitive || type == typeof(string) || type.IsEnum || type == typeof(Enum);

    /// <summary>
    /// What the walk descends into below a value besides its members: a nullable's underlying type,
    /// an array's elements, a dictionary's values and any other sequence's elements. Taken from the
    /// collection interfaces the type implements rather than from its own generic arguments,
    /// because that is what the walk tests at runtime: a <c>ProtoId&lt;T&gt;</c> or a tuple names a
    /// type it does not hold, and a class deriving from a list holds one it does not name. A
    /// dictionary's keys are left out because the walk never descends them.
    /// </summary>
    private static IEnumerable<Type> HeldTypes(Type type)
    {
        // A nullable boxes as its underlying value or as null, so the walk sees the value itself.
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return [underlying];

        if (type.IsArray)
            return [type.GetElementType()!];

        // An interface does not list itself among its interfaces.
        var interfaces = type.IsInterface ? type.GetInterfaces().Append(type) : type.GetInterfaces();
        var generic = interfaces.Where(candidate => candidate.IsGenericType).ToList();

        var values = generic
            .Where(candidate => candidate.GetGenericTypeDefinition() is var definition
                                && (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)))
            .Select(candidate => candidate.GetGenericArguments()[1])
            .Distinct()
            .ToList();

        // A dictionary is also a sequence of key-value pairs, which the walk does not treat as one.
        if (values.Count > 0)
            return values;

        return generic
            .Where(candidate => candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(candidate => candidate.GetGenericArguments()[0])
            .Distinct();
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

    /// <summary>
    /// Is this a type the engine's serializer treats as a data definition? Asked of the source
    /// generator's own decision rather than re-derived from attributes: the generator emits
    /// <see cref="ISerializationGenerated"/> for every type it accepts, records included
    /// (<c>RobustToolbox/Robust.Serialization.Generator/Generator.cs:55</c>, <c>:190</c>). The
    /// attribute alone misses three kinds, because <c>[DataDefinition]</c> is not inherited
    /// (<c>RobustToolbox/Robust.Shared/Serialization/Manager/Attributes/DataDefinitionAttribute.cs:13</c>):
    /// a subclass of a definition, a <c>[DataRecord]</c>, and an inheritor of an
    /// <c>[ImplicitDataDefinitionForInheritors]</c> base. The engine's own registry would be the
    /// better oracle and is internal to the engine.
    /// </summary>
    internal static bool IsDataDefinition(Type type) =>
        typeof(ISerializationGenerated).IsAssignableFrom(type);

    /// <summary>
    /// Can a member of this declared type hold, at runtime, a type the declaration does not name?
    /// Such a member is reachable on principle, because the declared type carries none of its
    /// inheritors' fields; the runtime walk then decides what is actually there.
    /// </summary>
    internal static bool IsPolymorphic(Type type) =>
        type.IsInterface
        || type.IsAbstract
        || (!type.IsSealed && !type.IsValueType && IsDataDefinition(type));

    /// <summary>
    /// For the audit: the members of this component the pass walks only because of the polymorphic
    /// rule, the ones the same classification would pass over if a polymorphic declared type did not
    /// count as reachable. What that rule costs, measured rather than guessed.
    /// </summary>
    internal static IEnumerable<MemberInfo> WalkedOnlyForPolymorphism(Type componentType)
    {
        foreach (var (member, attribute) in DataMembers(componentType))
        {
            if (Classify(member, attribute) != FieldCase.Walk)
                continue;

            if (!Carries(MemberType(member), new HashSet<Type>(), polymorphic: false))
                yield return member;
        }
    }

    /// <summary>
    /// For the audit: every time-offset and flag member the pass corrects below the component level,
    /// found by the pass's own classification rather than by reading source, so the list cannot drift
    /// from what the pass does. The caller tells the two apart by the member's serializer.
    ///
    /// <para>One thing the pass knows at runtime and a static list cannot: which concrete type a
    /// polymorphic member holds. The caller supplies the candidates, every concrete data definition
    /// the member could hold, and the audit reports what any of them would carry. Each result is the
    /// component member the walk starts from and the corrected member it ends at; the hops between are
    /// left out, because through polymorphic members they multiply into a path per inheritor.</para>
    /// </summary>
    internal sealed class NestedTimeAudit(Func<Type, IEnumerable<Type>> concreteDefinitions)
    {
        private readonly Dictionary<Type, HashSet<MemberInfo>> _definitions = new();
        private readonly Dictionary<Type, HashSet<MemberInfo>> _types = new();
        private readonly HashSet<Type> _visiting = new();

        public IEnumerable<(MemberInfo From, MemberInfo Corrected)> For(Type componentType)
        {
            foreach (var entry in Build(componentType))
            {
                if (entry.Case is not (FieldCase.ReadOnly or FieldCase.Walk))
                    continue;

                foreach (var corrected in Reach(entry.DeclaredType))
                {
                    yield return (entry.Member, corrected);
                }
            }
        }

        private HashSet<MemberInfo> Reach(Type? type)
        {
            var found = new HashSet<MemberInfo>();
            if (IsLeaf(type))
                return found;

            if (_types.TryGetValue(type, out var known))
                return known;

            // A collection is transparent, as it is to the pass.
            foreach (var held in HeldTypes(type))
            {
                found.UnionWith(Reach(held));
            }

            var candidates = IsPolymorphic(type)
                ? concreteDefinitions(type)
                : IsDataDefinition(type) ? new[] { type } : Array.Empty<Type>();

            foreach (var candidate in candidates)
            {
                found.UnionWith(Definition(candidate));
            }

            _types[type] = found;
            return found;
        }

        private HashSet<MemberInfo> Definition(Type definition)
        {
            if (_definitions.TryGetValue(definition, out var known))
                return known;

            // A definition that reaches itself contributes what it has found so far, the same
            // fixpoint the pass's own reachability takes.
            if (!_visiting.Add(definition))
                return new HashSet<MemberInfo>();

            var found = new HashSet<MemberInfo>();
            foreach (var member in BuildNested(definition))
            {
                switch (member.Case)
                {
                    case FieldCase.TimeOffset:
                    case FieldCase.Flags:
                        found.Add(member.Member);
                        break;

                    case FieldCase.ReadOnly:
                    case FieldCase.Walk:
                        found.UnionWith(Reach(member.DeclaredType));
                        break;
                }
            }

            _visiting.Remove(definition);
            _definitions[definition] = found;
            return found;
        }
    }

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

        /// <summary>
        /// A <c>FlagSerializer</c> field, rewritten because the engine's writer loses bit 0 (F35). The
        /// engine's reader restores it, so there is no read half.
        /// </summary>
        Flags,

        /// <summary>
        /// Not a correction of its own: a member the engine writes correctly whose value holds a
        /// readOnly member somewhere below it. The pass descends into the node the engine wrote and
        /// fills those in, because "every readOnly field, per field" cannot mean only the ones
        /// reachable through another readOnly field.
        /// </summary>
        Walk,
    }

    /// <summary>
    /// The state one component's write shares with everything under it: the owning entity's pause
    /// state, which a deadline at any depth is measured against, and the path the walk is on.
    /// </summary>
    private sealed class WalkState(EntityLifeStage lifeStage, TimeSpan curTime, TimeSpan? pauseTime)
    {
        public readonly EntityLifeStage LifeStage = lifeStage;
        public readonly TimeSpan CurTime = curTime;
        public readonly TimeSpan? PauseTime = pauseTime;
        /// <summary>The definitions on the walk's current path, which is what a loop returns to.</summary>
        public readonly HashSet<object> OnPath = new(ReferenceEqualityComparer.Instance);
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
        DrydockCodecManifest.AsymmetricInlineField? Asymmetric = null)
    {
        public object? Get(object target) => GetValue(Member, target);

        public void Set(object target, object? value) => SetValue(Member, target, value);
    }
}
