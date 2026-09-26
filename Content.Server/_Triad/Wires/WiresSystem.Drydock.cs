namespace Content.Server.Wires;

public sealed partial class WiresSystem
{
    /// <summary>
    /// Refills <see cref="WiresComponent.Statuses"/> from the wires and pushes the panel state, as every change to the wires'
    /// data does (<see cref="UpdateUserInterface"/>). Opening the panel pushes nothing (<see cref="OnInteractUsing"/>), so a
    /// wire list built by anything but map init shows an empty panel until this runs.
    /// </summary>
    public void RefreshUserInterface(EntityUid uid, WiresComponent? wires = null)
    {
        UpdateUserInterface(uid, wires);
    }

    /// <summary>
    /// The wire timers running on <paramref name="uid"/> (<see cref="StartWireAction"/>): each one's key, the seconds it has
    /// left, and the event it raises when it ends. A cancelled timer reads zero seconds left, because its end runs at the
    /// next update whatever time it had (<see cref="Update"/>).
    /// </summary>
    public IEnumerable<(object Key, float TimeLeft, TimedWireEvent OnFinish)> RunningTimers(EntityUid uid)
    {
        if (!_activeWires.TryGetValue(uid, out var timers))
            yield break;

        foreach (var timer in timers)
            yield return (timer.Id, timer.CancelToken.IsCancellationRequested ? 0f : timer.TimeLeft, timer.OnFinish);
    }
}
