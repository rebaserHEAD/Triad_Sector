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
    /// <remarks>
    /// Version 2, 2026-09-06: the absolute-time fields listed in the state fidelity design gained
    /// <c>TimeOffsetSerializer</c>, so they are written as an offset from the storing server's clock
    /// rather than raw. A version 1 revision holds raw values, and this reader adds the current
    /// clock to them, which puts the machine's next tick that far further out: a stored microwave or
    /// AME idles until the inflated deadline passes. Deliberately not a refusal, because the effect
    /// is a delay on a few machines and it clears the next time the ship is stored, where refusing
    /// would strand every ship filed before today.
    /// </remarks>
    public const int Current = 2;

    /// <summary>
    /// The oldest <see cref="Current"/> value a retrieve will still read. Raising this abandons
    /// every revision below it, so it moves only after the ladder has re-baked them all.
    /// </summary>
    public const int MinimumSupported = 1;
}
