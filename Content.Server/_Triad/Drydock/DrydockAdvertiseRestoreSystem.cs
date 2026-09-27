using System.Collections.Generic;
using System.Linq;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Advertise.Components;
using Content.Server.Advertise.EntitySystems;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Puts a restored advertiser back in <see cref="AdvertiseSystem"/>'s queue, which is filled only at MapInit and after an
/// advert (<c>AdvertiseSystem.cs:49-66</c>, <c>:129-131</c>): a retrieved entity is already past MapInit, so without this
/// it never advertises again. The entity is noted at its restore raise and enqueued once it is unpaused, not at the
/// raise: the thaw can still move its deadline (<see cref="DrydockImageSystem.Thaw"/>), and the queue drops an entry
/// whose time no longer matches the component's (<c>AdvertiseSystem.cs:113-116</c>).
/// </summary>
public sealed class DrydockAdvertiseRestoreSystem : EntitySystem
{
    [Dependency] private readonly AdvertiseSystem _advertise = default!;

    private readonly HashSet<EntityUid> _pending = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AdvertiseComponent, GridRestoredEvent>(OnRestored);
    }

    private void OnRestored(Entity<AdvertiseComponent> ent, ref GridRestoredEvent args)
    {
        _pending.Add(ent.Owner);
    }

    public override void Update(float frameTime)
    {
        if (_pending.Count == 0)
            return;

        foreach (var uid in _pending.ToList())
        {
            if (TerminatingOrDeleted(uid) || !TryComp<AdvertiseComponent>(uid, out var advert))
            {
                _pending.Remove(uid);
                continue;
            }

            // Still on its staging map: the thaw has not run.
            if (MetaData(uid).EntityPaused)
                continue;

            _advertise.Enqueue((uid, advert));
            _pending.Remove(uid);
        }
    }
}
