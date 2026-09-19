using System;
using System.Collections.Generic;
using Content.Server._Triad.Drydock.Codec;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock.Loader;

public sealed partial class DrydockImageSystem
{
    /// <summary>
    /// The live appearance dictionary is not a data field (AppearanceComponent.cs:35), and the field the codec does
    /// write, <c>AppearanceDataInit</c>, is the prototype's initial data, so without this row every appearance
    /// entry is lost. Read through the component's own public state (the drydock's LiveAppearance does the same,
    /// DrydockFidelitySystem.cs:496-502), which is the live dictionary itself and is only read here.
    /// </summary>
    internal MappingDataNode? WriteAppearance(DrydockCodec codec, AppearanceComponent appearance, Dictionary<string, int> skipped)
    {
        if (EntityManager.GetComponentState(EntityManager.EventBus, appearance, null, GameTick.Zero) is not AppearanceComponentState { Data.Count: > 0 } state)
            return null;

        var row = new MappingDataNode();
        foreach (var (key, value) in state.Data)
        {
            try
            {
                var keyNode = (ValueDataNode) Serialization.WriteValue(typeof(Enum), key, alwaysWrite: true, context: codec.Context);

                // The value is declared as object, so its own type rides beside it for the read.
                row[keyNode.Value] = new MappingDataNode
                {
                    ["type"] = new ValueDataNode(value.GetType().AssemblyQualifiedName!),
                    ["value"] = Serialization.WriteValue(value.GetType(), value, alwaysWrite: true, context: codec.Context),
                };
            }
            catch (ArgumentException)
            {
                // A value type with no serializer (ShowLayerData, ruled skipped): named in the result, not stored.
                var type = value.GetType().Name;
                skipped[type] = skipped.GetValueOrDefault(type) + 1;
            }
        }

        return row.Count == 0 ? null : row;
    }

    /// <summary>
    /// Each stored entry through the public writer, SetData, which on an entity not yet initialized just sets it. The
    /// stored type name is a string out of the image, so it picks a type only through <see cref="DrydockAppearanceTypes"/>:
    /// an entry whose type it refuses is skipped and counted in <paramref name="refused"/> by that stored name.
    /// </summary>
    internal int ReadAppearance(DrydockCodec codec, EntityUid uid, MappingDataNode row, Dictionary<string, int> refused)
    {
        var applied = 0;

        foreach (var (keyText, entry) in row)
        {
            var stored = (MappingDataNode) entry;
            var typeName = stored.Get<ValueDataNode>("type").Value;
            if (!DrydockAppearanceTypes.TryResolve(Reflection, typeName, out var type))
            {
                refused[typeName] = refused.GetValueOrDefault(typeName) + 1;
                continue;
            }

            var key = (Enum) Serialization.Read(typeof(Enum), new ValueDataNode(keyText), context: codec.Context, notNullableOverride: true)!;
            var value = Serialization.Read(type, stored["value"], context: codec.Context, notNullableOverride: true)!;
            Appearances.SetData(uid, key, value);
            applied++;
        }

        return applied;
    }
}
