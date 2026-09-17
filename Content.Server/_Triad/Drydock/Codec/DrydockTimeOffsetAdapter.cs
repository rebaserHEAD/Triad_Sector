using System;
using System.Globalization;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Triad.Drydock.Codec;

/// <summary>
/// The arithmetic for a field carrying <c>TimeOffsetSerializer</c>: store a deadline as its distance
/// from now, restore it as that distance from the new clock.
///
/// <para>A game time is meaningless in the next round. Written raw, a deadline arrives as one the
/// new clock will not reach for as long as the old server had been up, and whatever was waiting on
/// it never fires. That is finding F25's whole shape, and the reason ore silo magnets came back from
/// the drydock dead.</para>
///
/// <para>This is <c>TimeOffsetSerializer</c>'s own arithmetic with one thing removed: the test for
/// who is calling. The engine writes the offset only when the context is an <c>EntitySerializer</c>
/// and reads it only when the context is an <c>EntityDeserializer</c> on a post-init entity
/// (<c>RobustToolbox/Robust.Shared/Serialization/TypeSerializers/Implementations/Custom/TimeOffsetSerializer.cs:32-35</c>,
/// <c>:65-72</c>). The codec's rows are not either of those documents, so under
/// <see cref="DrydockCodecContext"/> the engine stores a literal zero and reads back
/// <see cref="TimeSpan.Zero"/>. Everything else the engine does is kept, including the map-init
/// gate, because that gate is about what a value means rather than about who is reading it: before
/// map-init a time field still holds setup data, not a deadline.</para>
///
/// <para>The two halves are deliberately not mirrors, and that asymmetry is the engine's.
/// <see cref="Write"/> has a pause branch and <see cref="Read"/> does not, so an entity stored while
/// paused comes back with its remaining time measured from the load clock. Its distance survives the
/// rest of the way only if the loader restores the pause state, because the shift that pays it back
/// is <c>[AutoPausedField]</c>'s, raised on unpause
/// (<c>RobustToolbox/Robust.Serialization.Generator/ComponentPauseGenerator.cs:169</c>).</para>
/// </summary>
public static class DrydockTimeOffsetAdapter
{
    /// <summary>
    /// What a live field stores. A map-initialized entity stores its deadline's distance from the
    /// moment it paused, or from the clock if it is running; anything earlier in its life stores a
    /// literal zero, matching the engine.
    /// </summary>
    /// <param name="pauseTime">
    /// <c>MetaDataComponent.PauseTime</c>: the clock reading at which the entity paused, or null
    /// while it runs.
    /// </param>
    public static ValueDataNode Write(TimeSpan value, EntityLifeStage lifeStage, TimeSpan curTime, TimeSpan? pauseTime)
    {
        if (lifeStage < EntityLifeStage.MapInitialized)
            return new ValueDataNode("0");

        var offset = value - (pauseTime ?? curTime);
        return new ValueDataNode(offset.TotalSeconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// What a stored offset becomes. One branch, unlike <see cref="Write"/>: the stored distance
    /// measured forward from the clock at load, clamped rather than wrapped at either end.
    /// </summary>
    public static TimeSpan Read(ValueDataNode node, TimeSpan curTime)
    {
        if (!double.TryParse(node.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            throw new FormatException($"Drydock codec: a time offset row held '{node.Value}', which is not a number.");

        // TimeSpan.FromSeconds throws on a value outside its range, where the engine's own clamp one
        // line below would have answered. Doing the range test in seconds keeps the clamp reachable.
        if (seconds >= TimeSpan.MaxValue.TotalSeconds)
            return TimeSpan.MaxValue;

        if (seconds <= TimeSpan.MinValue.TotalSeconds)
            return TimeSpan.MinValue;

        var stored = TimeSpan.FromSeconds(seconds);
        return stored > TimeSpan.MaxValue - curTime ? TimeSpan.MaxValue : stored + curTime;
    }
}
