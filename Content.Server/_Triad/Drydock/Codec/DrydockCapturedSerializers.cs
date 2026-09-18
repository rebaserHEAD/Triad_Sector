using System;
using System.Globalization;
using Content.Shared._NF.Market;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// The two types in <see cref="DrydockSerializationGap.CapturedTypes"/>: state a player made that the
/// engine's serializer cannot write at all, so the codec writes it. Both are registered on
/// <see cref="DrydockCodecContext"/> rather than as the engine's default for their type, because the
/// gap is ours and the audit measures the engine's coverage without us.
///
/// <para>Registration on the context is enough to be reached. A regular type serializer is looked up
/// on the calling context's provider before anything else, on the way out
/// (<c>RobustToolbox/Robust.Shared/Serialization/Manager/SerializationManager.Writing.cs:185-203</c>)
/// and on the way back in (<c>SerializationManager.Reading.cs:77-120</c>), so a
/// <c>List&lt;LatheRecipeBatch&gt;</c> written by a component's generated writer reaches this one per
/// element.</para>
/// </summary>
internal static class DrydockCapturedKeys
{
    internal static ValueDataNode Number(int value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Round-trip format, so a price that is not a round number comes back as itself.</summary>
    internal static ValueDataNode Number(double value) => new(value.ToString("R", CultureInfo.InvariantCulture));

    internal static string Text(MappingDataNode node, string key)
    {
        if (!node.TryGet<ValueDataNode>(key, out var value) || value.IsNull)
            throw new FormatException($"Drydock codec: a captured row is missing '{key}'.");

        return value.Value;
    }

    internal static int Integer(MappingDataNode node, string key)
    {
        if (!int.TryParse(Text(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new FormatException($"Drydock codec: a captured row held '{key}' as something that is not a whole number.");

        return parsed;
    }

    internal static double Real(MappingDataNode node, string key)
    {
        if (!double.TryParse(Text(node, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            throw new FormatException($"Drydock codec: a captured row held '{key}' as something that is not a number.");

        return parsed;
    }

    internal static ValidationNode Require(MappingDataNode node, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!node.Has(key))
                return new ErrorNode(node, $"A captured row is missing '{key}'.");
        }

        return new ValidatedValueNode(node);
    }
}

/// <summary>
/// Production a player queued up. Written because the engine cannot: <see cref="LatheRecipeBatch"/>
/// is not a data definition and has no serializer, so a lathe's queue comes back empty from the
/// engine's own write.
///
/// <para><see cref="LatheRecipeBatch.Recipe"/> is stored as its prototype id rather than as the
/// prototype, and <see cref="LatheRecipeBatch.Index"/> is stored because it is state: the queue
/// de-queues by it, and the constructor hands out a fresh one from a static counter, so a restored
/// batch that took a new index would not be the batch the player queued.</para>
///
/// <para><see cref="LatheRecipeBatch.Actor"/> is a <see cref="NetEntity"/>, which means nothing in
/// the next round. It is written as the entity it names, through the context's own entity writer, so
/// it becomes a stable id like every other reference in the image and comes back resolved through
/// the same pair.</para>
/// </summary>
public sealed class DrydockLatheRecipeBatchSerializer : ITypeSerializer<LatheRecipeBatch, MappingDataNode>
{
    private const string IndexKey = "index";
    private const string RecipeKey = "recipe";
    private const string ActorKey = "actor";
    private const string PrintedKey = "itemsPrinted";
    private const string RequestedKey = "itemsRequested";

    private readonly DrydockCodecContext _context;
    private readonly IEntityManager _entMan;

    public DrydockLatheRecipeBatchSerializer(DrydockCodecContext context, IEntityManager entMan)
    {
        _context = context;
        _entMan = entMan;
    }

    public ValidationNode Validate(
        ISerializationManager serializationManager,
        MappingDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        return DrydockCapturedKeys.Require(node, IndexKey, RecipeKey, PrintedKey, RequestedKey);
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        LatheRecipeBatch value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        return new MappingDataNode
        {
            [IndexKey] = DrydockCapturedKeys.Number(value.Index),
            [RecipeKey] = new ValueDataNode(value.Recipe.ID),
            [PrintedKey] = DrydockCapturedKeys.Number(value.ItemsPrinted),
            [RequestedKey] = DrydockCapturedKeys.Number(value.ItemsRequested),
            [ActorKey] = value.Actor is { } actor
                ? _context.Write(serializationManager, _entMan.GetEntity(actor), dependencies, alwaysWrite, context)
                : ValueDataNode.Null(),
        };
    }

    public LatheRecipeBatch Read(
        ISerializationManager serializationManager,
        MappingDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<LatheRecipeBatch>? instanceProvider = null)
    {
        var recipe = dependencies.Resolve<IPrototypeManager>()
            .Index<LatheRecipePrototype>(DrydockCapturedKeys.Text(node, RecipeKey));

        NetEntity? actor = null;
        if (node.TryGet<ValueDataNode>(ActorKey, out var stored) && !stored.IsNull)
        {
            var uid = _context.Read(serializationManager, stored, dependencies, hookCtx, context);

            // An actor the image no longer holds is no actor, rather than a reference to nothing:
            // the batch is still the player's production, and only the credit for it is severed.
            if (uid.IsValid())
                actor = _entMan.GetNetEntity(uid);
        }

        return new LatheRecipeBatch(
            recipe,
            DrydockCapturedKeys.Integer(node, PrintedKey),
            DrydockCapturedKeys.Integer(node, RequestedKey),
            actor)
        {
            Index = DrydockCapturedKeys.Integer(node, IndexKey),
        };
    }
}

/// <summary>
/// Player-modified market state. <see cref="MarketData"/> is a plain class with no data definition,
/// so the engine has no way to write one and a cargo console's market comes back empty without this.
/// Every member is a value; there is no reference to carry.
/// </summary>
public sealed class DrydockMarketDataSerializer : ITypeSerializer<MarketData, MappingDataNode>
{
    private const string PrototypeKey = "prototype";
    private const string StackKey = "stackPrototype";
    private const string QuantityKey = "quantity";
    private const string PriceKey = "price";

    public ValidationNode Validate(
        ISerializationManager serializationManager,
        MappingDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        return DrydockCapturedKeys.Require(node, PrototypeKey, QuantityKey, PriceKey);
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        MarketData value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        return new MappingDataNode
        {
            [PrototypeKey] = new ValueDataNode(value.Prototype.Id),
            [StackKey] = value.StackPrototype is { } stack
                ? new ValueDataNode(stack.Id)
                : ValueDataNode.Null(),
            [QuantityKey] = DrydockCapturedKeys.Number(value.Quantity),
            [PriceKey] = DrydockCapturedKeys.Number(value.Price),
        };
    }

    public MarketData Read(
        ISerializationManager serializationManager,
        MappingDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<MarketData>? instanceProvider = null)
    {
        ProtoId<StackPrototype>? stack = null;
        if (node.TryGet<ValueDataNode>(StackKey, out var stored) && !stored.IsNull)
            stack = new ProtoId<StackPrototype>(stored.Value);

        return new MarketData(
            new EntProtoId(DrydockCapturedKeys.Text(node, PrototypeKey)),
            stack,
            DrydockCapturedKeys.Integer(node, QuantityKey),
            DrydockCapturedKeys.Real(node, PriceKey));
    }
}
