using Content.Server._Triad.Speech.EntitySystems; // Triad: DrawlAccentSystem + IDrawlAccentComponent relocated to _Triad
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(DrawlAccentSystem))] // Triad: was SouthernAccentSystem; folded into the shared drawl engine
public sealed partial class SouthernAccentComponent : Component, IDrawlAccentComponent // Triad: shared drawl config
{
    // Triad: drawl is now data-driven so Southern and Cowboy can share one system.
    [DataField]
    public string Accent { get; set; } = "southern";

    // Triad: Southern carries NO prefix/suffix tics. The old genteel/pious tic pool read as bolt-on
    // clown-noise on short fragments ("Turrets" -> "Mercy me, turrets") and shifted meaning. The charm
    // now rides entirely on the drawl phonetics (DrawlAccentSystem) + the curated single-word dictionary
    // (southern.ftl). Pools left as empty DataFields so an admin could opt tics back in per-entity.
    [DataField]
    public List<string> Prefixes { get; set; } = new();

    [DataField]
    public float PrefixProb { get; set; } = 0f;

    [DataField]
    public List<string> Suffixes { get; set; } = new();

    [DataField]
    public float SuffixProb { get; set; } = 0f;
}
