using Prometheus;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Every loss class the durability design records, as counters the dashboard reads. A fallback to an
/// older revision is an incident on sight.
/// </summary>
public static class DrydockMetrics
{
    /// <summary>
    /// A retrieve handed out an older revision than the current one: the current image would not load
    /// or was stepped past, and an older one loaded.
    /// </summary>
    public static readonly Counter RetrieveFallbacks = Metrics.CreateCounter(
        "drydock_retrieve_fallbacks",
        "Retrieves that used an older revision because a newer one could not be used.");

    /// <summary>A retrieve refused because the image references content that no longer resolves.</summary>
    public static readonly Counter DriftRefusals = Metrics.CreateCounter(
        "drydock_drift_refusals",
        "Retrieves refused because the stored image references content that no longer exists.");

    /// <summary>A store refused because its image did not read back as it was written.</summary>
    public static readonly Counter ValidationMismatches = Metrics.CreateCounter(
        "drydock_validation_mismatches",
        "Stores refused because the written image did not read back as it was written.");
}
