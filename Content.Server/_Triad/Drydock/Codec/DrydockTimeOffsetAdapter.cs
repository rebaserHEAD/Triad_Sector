using System;
using System.Globalization;
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
/// <see cref="TimeSpan.Zero"/>. The engine's map-init gate goes too: it writes zero for an entity
/// below MapInitialized because in a map file that is setup data, but every entity the drydock stores
/// is live whatever its stage, and every hull built in the round has one below it, its grid
/// (SharedMapSystem.Grid.cs:64-65), whose time fields are deadlines like any other.</para>
///
/// <para>The two halves are deliberately not mirrors, and that asymmetry is the engine's.
/// <see cref="Write"/> has a pause branch and <see cref="Read"/> does not, so an entity stored while
/// paused comes back with its remaining time measured from the load clock. Its distance survives the
/// time the load then spends paused only if the load pauses it, as a load onto a paused map does
/// (<see cref="Loader.DrydockLoadSession"/>), and something pays that time back on unpause:
/// <c>[AutoPausedField]</c>'s shift
/// (<c>RobustToolbox/Robust.Serialization.Generator/ComponentPauseGenerator.cs:169</c>) or a
/// hand-written handler, and for a field neither reaches, <see cref="Loader.DrydockImageSystem.Thaw"/>.</para>
/// </summary>
public static class DrydockTimeOffsetAdapter
{
    /// <summary>
    /// The row tokens for the three values the codebase uses as sentinels rather than deadlines: zero for "never" (or
    /// "overdue since the start"), and the two ends for "not before the end of time". Each travels as itself, never as a
    /// distance from the clock, because a distance turns "never" into a deadline already past (a microwave's malfunction
    /// time, zero until it cooks metal, came back overdue and blew the microwave up). The engine never meets this: its
    /// writer leaves a field at its default out of the file, and every such field defaults to zero.
    /// </summary>
    public const string ZeroToken = "~zero";

    public const string MaxToken = "~max";

    public const string MinToken = "~min";

    /// <summary>Whether a time is one of the three sentinels <see cref="Write"/> stores as a token rather than as a distance.</summary>
    public static bool IsSentinel(TimeSpan value) =>
        value == TimeSpan.Zero || value == TimeSpan.MaxValue || value == TimeSpan.MinValue;

    /// <summary>
    /// What a live field stores. A sentinel stores its token. Otherwise the deadline's distance from the moment its
    /// entity paused, or from the clock if it is running, at any life stage.
    /// </summary>
    /// <param name="pauseTime">
    /// <c>MetaDataComponent.PauseTime</c>: the clock reading at which the entity paused, or null
    /// while it runs.
    /// </param>
    public static ValueDataNode Write(TimeSpan value, TimeSpan curTime, TimeSpan? pauseTime)
    {
        if (value == TimeSpan.Zero)
            return new ValueDataNode(ZeroToken);

        if (value == TimeSpan.MaxValue)
            return new ValueDataNode(MaxToken);

        if (value == TimeSpan.MinValue)
            return new ValueDataNode(MinToken);

        var offset = value - (pauseTime ?? curTime);
        return new ValueDataNode(offset.TotalSeconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// What a stored offset becomes. A sentinel token comes back exactly. Otherwise one branch, unlike
    /// <see cref="Write"/>: the stored distance measured forward from the clock at load, clamped rather than wrapped at
    /// either end.
    /// </summary>
    public static TimeSpan Read(ValueDataNode node, TimeSpan curTime)
    {
        switch (node.Value)
        {
            case ZeroToken:
                return TimeSpan.Zero;
            case MaxToken:
                return TimeSpan.MaxValue;
            case MinToken:
                return TimeSpan.MinValue;
        }

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
