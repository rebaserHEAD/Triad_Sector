using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Shared._Triad.CCVar;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The engine's YAML emission for a node tree, which the YAML document tooling and its tests still call.
/// </summary>
public sealed partial class DrydockSystem
{
    /// <summary>
    /// The node tree as YAML text, byte for byte the way the engine writes it
    /// (<c>MapLoaderSystem.Write</c>). The mapping fix and the emitter are both public, so this is
    /// the engine's own emission rather than a lookalike, which matters because the deserializer on
    /// the other end is the engine's.
    /// </summary>
    internal static string EmitDocument(MappingDataNode data) => EmitDocument(data, EmitterSettings.Default.NewLine);

    /// <summary>
    /// <see cref="EmitDocument(MappingDataNode)"/> with the line ending chosen, so a document re-written
    /// on a different platform from the one that stored it keeps its own. The emitter takes its line
    /// ending from its settings, not from the writer; every other setting is the default
    /// (<c>new Emitter(writer)</c> is <c>EmitterSettings.Default</c>).
    /// </summary>
    internal static string EmitDocument(MappingDataNode data, string newLine)
    {
        using var writer = new StringWriter();
        var document = new YamlDocument(data.ToYamlNode());
        var stream = new YamlStream { document };
        stream.Save(new YamlMappingFix(new Emitter(writer, EmitterSettings.Default.WithNewLine(newLine))), false);
        return writer.ToString();
    }
}
