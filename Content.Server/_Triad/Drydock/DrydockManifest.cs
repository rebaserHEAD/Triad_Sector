using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// What a stored revision contained, recorded at store time from ingredients the fidelity layer's
/// recursive walk already has in hand. It is a forensic record, not a second serializer: it answers
/// "what was aboard" and the two kinds of change players actually dispute, and deliberately does
/// not attempt a general field diff.
/// </summary>
public sealed class DrydockManifest
{
    /// <summary>
    /// Mirrors <see cref="DrydockFormat.Current"/> at write time. Carried inside the document as
    /// well as in the revision column so a manifest stays readable if it is ever handled apart
    /// from its row.
    /// </summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = DrydockFormat.Current;

    [JsonPropertyName("e")]
    public List<DrydockManifestEntry> Entries { get; set; } = new();

    public string Serialize()
    {
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    public static DrydockManifest? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<DrydockManifest>(json, SerializerOptions);
    }

    /// <summary>
    /// Indefinite retention makes size a real cost: a large ship stored twice a round is tens of
    /// kilobytes per round, so property names are short and defaults are omitted on purpose.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
    };
}

/// <summary>
/// One entity that was aboard. Entries are written in walk order, and that order is load-bearing:
/// <see cref="Parent"/> indexes into the entry list rather than naming an entity, because entity
/// ids do not survive a serialization round trip and a manifest has to still mean something a year
/// later.
/// </summary>
public sealed class DrydockManifestEntry
{
    [JsonPropertyName("p")]
    public string Proto { get; set; } = string.Empty;

    /// <summary>
    /// Index of the containing entry, or null for the grid itself and anything directly on it.
    /// This is what makes "removed from the locker" answerable rather than just "removed".
    /// </summary>
    [JsonPropertyName("c")]
    public int? Parent { get; set; }

    /// <summary>Total damage across all types, from the same dictionary the damage sidecar reads.</summary>
    [JsonPropertyName("d")]
    public float Damage { get; set; }

    /// <summary>Stack count, the one round-trip invariant nothing else here covers.</summary>
    [JsonPropertyName("s")]
    public int Stack { get; set; }

    /// <summary>
    /// The captured-state keys the fidelity layer wrote for this entity, in
    /// <c>ComponentName|FieldName</c> form. A key that stops resolving after a rename is the drift
    /// this records, and comparing these is how a silent skip stops being silent.
    /// </summary>
    [JsonPropertyName("k")]
    public List<string>? CapturedKeys { get; set; }
}
