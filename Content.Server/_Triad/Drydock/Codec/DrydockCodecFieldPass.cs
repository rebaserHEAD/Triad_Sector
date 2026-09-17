using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.Definition;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// One pass over a component's data fields, run after the engine's generated writer and after its
/// generated reader, fixing the handful of fields whose engine serializer answers a question the
/// codec did not ask.
///
/// <para>Why a pass and not a set of serializers. The three fields below are handled by engine
/// serializers that branch on the calling context, and neither door into that branch is open to us.
/// A named <c>customTypeSerializer</c> resolves from the manager's own type-keyed cache and never
/// consults a context at all
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
/// </summary>
public sealed class DrydockCodecFieldPass
{
    private readonly IEntityManager _entMan;
    private readonly IGameTiming _timing;
    private readonly MetaDataSystem _metaData;

    private readonly ConcurrentDictionary<Type, ImmutableArray<Entry>> _cache = new();

    public DrydockCodecFieldPass(IEntityManager entMan, IGameTiming timing)
    {
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
        if (entries.Length == 0)
            return;

        // The engine's write subtracts the clock reading at which the entity paused. Content cannot
        // see that field, so reconstruct it: GetPauseTime is how long it has been paused.
        var pauseTime = entity.Comp.EntityPaused
            ? _timing.CurTime - _metaData.GetPauseTime(entity.Owner, entity.Comp)
            : (TimeSpan?) null;

        foreach (var entry in entries)
        {
            switch (entry.Case)
            {
                case FieldCase.TimeOffset:
                    // A null deadline means there is no deadline, which the engine already writes
                    // correctly. Only a live one needs the offset.
                    if (entry.Get(component) is not TimeSpan deadline)
                        break;

                    mapping[entry.Key] = DrydockTimeOffsetAdapter.Write(
                        deadline,
                        entity.Comp.EntityLifeStage,
                        _timing.CurTime,
                        pauseTime);
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
            }
        }
    }

    /// <summary>
    /// Runs on the component the engine's reader produced, against the mapping it was read from.
    /// </summary>
    /// <remarks>
    /// Only the time-offset case has a read half. The two dropped fields are re-derived rather than
    /// restored: chunks come back from our own tables, and the grid's fixtures are rebuilt from
    /// those chunks, so both are correct by having been left alone.
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
    }

    private ImmutableArray<Entry> EntriesFor(Type componentType) =>
        _cache.GetOrAdd(componentType, static type => Build(type));

    private static ImmutableArray<Entry> Build(Type componentType)
    {
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;

        var entries = ImmutableArray.CreateBuilder<Entry>();

        // Walked one declaring type at a time, because a base type's private and internal fields are
        // not returned by a query on the derived type, and MapGridComponent.Chunks is internal.
        for (var type = componentType; type != null && type != typeof(object); type = type.BaseType)
        {
            var members = type.GetFields(flags).Cast<MemberInfo>().Concat(type.GetProperties(flags));

            foreach (var member in members)
            {
                // IncludeDataField is deliberately not matched: it inlines its target's fields under
                // no key of its own, so there is nothing here to address.
                if (member.GetCustomAttribute<DataFieldAttribute>() is not { } data)
                    continue;

                if (Classify(member, data) is not { } fieldCase)
                    continue;

                entries.Add(new Entry(data.Tag ?? DataDefinitionUtility.AutoGenerateTag(member.Name), member, fieldCase));
            }
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

    private static FieldCase? Classify(MemberInfo member, DataFieldAttribute data)
    {
        if (data.CustomTypeSerializer == typeof(TimeOffsetSerializer))
            return FieldCase.TimeOffset;

        if (data.CustomTypeSerializer == typeof(FixtureSerializer))
            return FieldCase.GridFixtures;

        if (member.DeclaringType == typeof(MapGridComponent) && IsChunkStore(MemberType(member)))
            return FieldCase.GridChunks;

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

    private static Type? MemberType(MemberInfo member) => member switch
    {
        FieldInfo field => field.FieldType,
        PropertyInfo property => property.PropertyType,
        _ => null,
    };

    /// <summary>
    /// The exceptions this pass knows about. Each one is a field the engine writes correctly for a
    /// map file and incorrectly for an image row.
    /// </summary>
    private enum FieldCase : byte
    {
        TimeOffset,
        GridChunks,
        GridFixtures,
    }

    private readonly record struct Entry(string Key, MemberInfo Member, FieldCase Case)
    {
        public object? Get(object target) => Member switch
        {
            FieldInfo field => field.GetValue(target),
            PropertyInfo property => property.GetValue(target),
            _ => null,
        };

        public void Set(object target, object? value)
        {
            switch (Member)
            {
                case FieldInfo field:
                    field.SetValue(target, value);
                    break;
                case PropertyInfo property:
                    property.SetValue(target, value);
                    break;
            }
        }
    }
}
