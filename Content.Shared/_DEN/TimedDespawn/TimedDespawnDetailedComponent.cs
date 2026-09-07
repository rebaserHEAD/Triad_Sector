using Robust.Shared.Audio;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad

namespace Content.Shared._DEN.TimedDespawn;

/// <summary>
/// This is used for a more detailed timed despawn component.
/// </summary>
[RegisterComponent]
public sealed partial class TimedDespawnDetailedComponent : Component
{
    /// <summary>
    /// When the timer started, as an absolute game time.
    /// </summary>
    /// <remarks>
    /// Triad: written through <see cref="TimeOffsetSerializer"/>. This is a CurTime stamp, and the
    /// clock it was taken from does not survive into the round that reads the save back, so a raw
    /// value put the deadline as far in the future as the old server had been up: a holofan stored
    /// while lit came back and never expired.
    /// </remarks>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan StartTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// How long the entity will exist, in seconds, before despawning.
    /// </summary>
    [DataField]
    public float Lifetime = 5f;

    [DataField("examineText")]
    public LocId? ExamineLocId { get; set; } = "timed-despawn-holoprojection-examine";

    [DataField]
    public SoundSpecifier? StartSound { get; set; } = new SoundPathSpecifier("/Audio/_DEN/Effects/holofan_sound_looped.ogg");

    [DataField]
    public AudioParams StartSoundParams { get; set; } = AudioParams.Default;

    [DataField]
    public SoundSpecifier? EndSound { get; set; }

    [DataField]
    public AudioParams EndSoundParams { get; set; } = AudioParams.Default;
}
