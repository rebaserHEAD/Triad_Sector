namespace Content.Server._Triad.Drydock;

/// <summary>
/// Version stamps for the formats the drydock owns, as opposed to the ones the engine owns.
/// </summary>
public static class DrydockFormat
{
    /// <summary>
    /// Written to <c>drydock_format_ver</c> on every revision. Covers the sidecar encoding and the
    /// manifest shape together, because a revision is only readable if both are understood.
    ///
    /// Bump this when either encoding changes in a way an older reader would misread, and give the
    /// re-bake ladder a step that migrates the old version forward. A stored ship is only as
    /// durable as our ability to tell which encoding it is written in.
    /// </summary>
    public const int Current = 1;

    /// <summary>
    /// The oldest <see cref="Current"/> value a retrieve will still read. Raising this abandons
    /// every revision below it, so it moves only after the ladder has re-baked them all.
    /// </summary>
    public const int MinimumSupported = 1;
}
