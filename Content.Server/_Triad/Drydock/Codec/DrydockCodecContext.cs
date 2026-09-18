using System;
using System.Globalization;
using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// The context every codec read and write runs under. It replaces the engine's
/// <see cref="EntityUid"/> writer and reader pair, and nothing else.
///
/// <para>The engine's context is <c>EntitySerializer</c>, whose <see cref="EntityUid"/> writer
/// returns the entity's position in the one document being written
/// (<c>RobustToolbox/Robust.Shared/EntitySerialization/EntitySerializer.cs:1006-1007</c>) and whose
/// reader looks that number up in the same document's map
/// (<c>RobustToolbox/Robust.Shared/EntitySerialization/EntityDeserializer.cs:1199-1200</c>). Here a
/// reference writes as the target's <see cref="DrydockStableIdComponent.Value"/>, the same id that
/// entity's own row carries, so a row addresses another row without either of them being read as
/// part of a document.</para>
///
/// <para>Everything else the manager asks a context is answered the way the engine answers it.
/// <see cref="WritingReadingPrototypes"/> is false because an image holds entities, not prototypes:
/// several engine serializers take a different branch on that flag, and claiming true would make
/// <c>TimeOffsetSerializer</c> answer zero on both sides
/// (<c>RobustToolbox/Robust.Shared/Serialization/TypeSerializers/Implementations/Custom/TimeOffsetSerializer.cs:32</c>).</para>
///
/// <para>This context is not an <c>EntitySerializer</c>, so the engine's three caller-sensitive
/// serializers take their fallback branch under it. That is handled once, by field, in
/// <see cref="DrydockCodecFieldPass"/>, rather than by trying to register adapters here: a named
/// <c>customTypeSerializer</c> resolves from the manager's own type-keyed cache and never consults
/// a context (<c>RobustToolbox/Robust.Shared/Serialization/Manager/SerializationManager.Writing.cs:298-302</c>).</para>
/// </summary>
public sealed class DrydockCodecContext : ISerializationContext, ITypeSerializer<EntityUid, ValueDataNode>
{
    /// <summary>
    /// What a reference writes as when it points outside the image, and what an id that no row
    /// claims reads back as. The engine spells its own out-of-document reference the same way, so a
    /// row written here stays legible to anyone reading it with the engine's eyes.
    /// </summary>
    public const string InvalidReference = "invalid";

    private readonly Func<EntityUid, long?> _allocate;
    private readonly Func<long, EntityUid> _resolve;

    /// <inheritdoc/>
    public SerializationManager.SerializerProvider SerializerProvider { get; }

    /// <inheritdoc/>
    public bool WritingReadingPrototypes => false;

    /// <param name="allocate">
    /// The entity's stable id, minting one if this is the first capture that has seen it. Null means
    /// the entity is not part of this image, and the reference is written as
    /// <see cref="InvalidReference"/>.
    /// </param>
    /// <param name="resolve">
    /// The live entity a stored id names, or <see cref="EntityUid.Invalid"/> if nothing in this load
    /// claims it.
    /// </param>
    public DrydockCodecContext(
        ISerializationManager serialization,
        IEntityManager entMan,
        Func<EntityUid, long?> allocate,
        Func<long, EntityUid> resolve)
    {
        _allocate = allocate;
        _resolve = resolve;

        SerializerProvider = new SerializationManager.SerializerProvider(serialization);
        SerializerProvider.RegisterSerializer(this);

        // The two captured types, which the engine cannot write at all
        // (<see cref="DrydockSerializationGap.CapturedTypes"/>). They are registered here rather
        // than as the engine's default for their type, because the gap is ours: the serializability
        // audit measures what the engine covers without us, and it must keep measuring that.
        SerializerProvider.RegisterSerializer(new DrydockLatheRecipeBatchSerializer(this, entMan));
        SerializerProvider.RegisterSerializer(new DrydockMarketDataSerializer());
    }

    public ValidationNode Validate(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        if (node.Value == InvalidReference)
            return new ValidatedValueNode(node);

        return long.TryParse(node.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? new ValidatedValueNode(node)
            : new ErrorNode(node, "Not a drydock stable id");
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        EntityUid value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        if (!value.IsValid() || _allocate(value) is not { } stableId)
            return new ValueDataNode(InvalidReference);

        return new ValueDataNode(stableId.ToString(CultureInfo.InvariantCulture));
    }

    public EntityUid Read(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<EntityUid>? instanceProvider = null)
    {
        if (node.Value == InvalidReference)
            return EntityUid.Invalid;

        // A row that is not a number is corruption, not a missing entity, and the two must not read
        // the same: a load that silently turns garbage into Invalid restores a ship with holes in it.
        if (!long.TryParse(node.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stableId))
            throw new FormatException($"Drydock codec: an entity reference held '{node.Value}', which is neither a stable id nor '{InvalidReference}'.");

        return _resolve(stableId);
    }
}
