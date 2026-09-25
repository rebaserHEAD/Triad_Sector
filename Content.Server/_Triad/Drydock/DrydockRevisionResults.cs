namespace Content.Server._Triad.Drydock;

/// <summary>The outcome of <see cref="DrydockStore.TryPinRevision"/> or <see cref="DrydockStore.TryUnpinRevision"/>.</summary>
public enum DrydockPinResult : byte
{
    /// <summary>The flag moved and the timeline row was written.</summary>
    Success,

    /// <summary>
    /// The ship or the revision is unknown. For a pin, also a revision whose document pruning has
    /// already taken: there is nothing left to protect.
    /// </summary>
    NotFound,

    /// <summary>The revision was already pinned, or already unpinned. Nothing was written, including no timeline row.</summary>
    AlreadyInState,
}
