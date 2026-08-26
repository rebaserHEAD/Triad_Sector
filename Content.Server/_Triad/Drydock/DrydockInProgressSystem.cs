using Robust.Shared.Containers;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Refuses container insertion anywhere on a grid that is mid-store.
///
/// <para>The subscription is broadcast rather than component-directed on purpose: the engine raises
/// the attempt on the container's owner, which is the crate or the locker, not the grid carrying
/// the marker, so a directed subscription would never see it. The check is two component lookups
/// and the marker exists only for the length of one store.</para>
/// </summary>
public sealed class DrydockInProgressSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ContainerIsInsertingAttemptEvent>(OnInsertAttempt);
    }

    private void OnInsertAttempt(ContainerIsInsertingAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (Transform(args.Container.Owner).GridUid is { } grid && HasComp<DrydockInProgressComponent>(grid))
            args.Cancel();
    }
}
