using System;
using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// One component in, one <see cref="MappingDataNode"/> out, and back again. Every value the drydock
/// stores passes through here, so this is where the two deviations from the engine's own path live
/// and the only place they live.
///
/// <para>The engine does the work. A component is written by its own generated whole-component
/// writer and read by its own generated reader, both under <see cref="DrydockCodecContext"/>, which
/// swaps the document-local entity number for a stable id and changes nothing else. Then one pass
/// over the component's data fields fixes the short list of fields whose engine serializer answers
/// for a map file rather than for a row (<see cref="DrydockCodecFieldPass"/>). No serializer is
/// written from scratch.</para>
///
/// <para><c>alwaysWrite</c> is true on the way out, matching what the engine does for an entity with
/// a prototype: the whole component is written, and the delta against the prototype is taken
/// afterwards, at the level where the prototype is known.</para>
/// </summary>
public sealed partial class DrydockCodec
{
    private readonly ISerializationManager _serialization;
    private readonly DrydockCodecFieldPass _pass;

    /// <summary>
    /// Exposed because the same context has to be used for anything written alongside a component,
    /// or a reference written next to a row would be numbered differently from the one inside it.
    /// </summary>
    public DrydockCodecContext Context { get; }

    /// <summary>
    /// Every severed reference this codec's reads met, by path, and whether its member was nullable and so read null
    /// (<see cref="DrydockCodecFieldPass.Severed"/>).
    /// </summary>
    public IReadOnlyList<(string Member, bool Nullable)> Severed => _pass.Severed;

    /// <summary>
    /// The prototype manager a manifest's prototype-id member reads through, where the caller has one. A caller that
    /// does not passes none and the codec asks the entity manager's dependencies for it, which is what a test harness
    /// can reach; server code injects instead.
    /// </summary>
    private readonly IPrototypeManager? _prototypes;

    /// <inheritdoc cref="DrydockCodec(ISerializationManager, IEntityManager, IGameTiming, Func{EntityUid, long?}, Func{long, EntityUid})"/>
    /// <param name="prototypes">The prototype manager, injected rather than resolved.</param>
    public DrydockCodec(
        ISerializationManager serialization,
        IEntityManager entMan,
        IPrototypeManager prototypes,
        IGameTiming timing,
        Func<EntityUid, long?> allocate,
        Func<long, EntityUid> resolve)
        : this(serialization, entMan, timing, allocate, resolve)
    {
        _prototypes = prototypes;
    }

    /// <param name="allocate">See <see cref="DrydockCodecContext"/>: the stable id for an entity.</param>
    /// <param name="resolve">See <see cref="DrydockCodecContext"/>: the entity a stable id names.</param>
    public DrydockCodec(
        ISerializationManager serialization,
        IEntityManager entMan,
        IGameTiming timing,
        Func<EntityUid, long?> allocate,
        Func<long, EntityUid> resolve)
    {
        _serialization = serialization;
        _factory = entMan.ComponentFactory;
        _entMan = entMan;
        Context = new DrydockCodecContext(serialization, entMan, allocate, resolve);
        _pass = new DrydockCodecFieldPass(serialization, Context, entMan, timing);
    }

    private readonly IComponentFactory _factory;
    private readonly IEntityManager _entMan;

    /// <summary>
    /// The component as it will be stored.
    /// </summary>
    /// <param name="entity">
    /// The component's owner and its metadata. Both are the pass's, not the writer's: the life stage
    /// and the pause state decide what a deadline stores as, and whether the owner is a grid decides
    /// whether its fixtures are content or a derivation.
    /// </param>
    public MappingDataNode Write(Entity<MetaDataComponent> entity, IComponent component)
    {
        var mapping = _serialization.WriteValueAs<MappingDataNode>(
            component.GetType(), component, alwaysWrite: true, context: Context);

        _pass.AfterWrite(entity, component, mapping);
        RemoveNotCarried(component.GetType(), mapping);
        return mapping;
    }

    /// <summary>
    /// The component a stored mapping describes, not yet attached to anything.
    /// </summary>
    public IComponent Read(Type componentType, MappingDataNode mapping)
    {
        var component = (IComponent) _serialization.Read(
            componentType, mapping, context: Context, notNullableOverride: true)!;

        _pass.AfterRead(component, mapping);
        return component;
    }

    /// <inheritdoc cref="Read(System.Type,Robust.Shared.Serialization.Markdown.Mapping.MappingDataNode)"/>
    public T Read<T>(MappingDataNode mapping) where T : IComponent =>
        (T) Read(typeof(T), mapping);

    /// <summary>
    /// A bare value, not a component, written under <see cref="Context"/>. No field pass runs, so a value whose type
    /// holds a time or an entity reference is the caller's to refuse before it gets here.
    /// </summary>
    public DataNode WriteValue(Type type, object value) =>
        _serialization.WriteValue(type, value, alwaysWrite: true, context: Context);

    /// <summary>A bare value written by <see cref="WriteValue"/>, read back under <see cref="Context"/>.</summary>
    public object? ReadValue(Type type, DataNode node) =>
        _serialization.Read(type, node, context: Context, notNullableOverride: true);
}
