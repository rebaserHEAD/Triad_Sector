using Content.Server._Triad.Construction;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;

namespace Content.Server._Triad.Shuttles;

/// <summary>
/// Puts back what a thruster's own disable branch leaves on its power load. When a player or a signal disables a thruster, the
/// owner writes the receiver's <c>Load</c> to 1, and it is a data field, so the 1 is stored (<c>ThrusterSystem.cs:79-82</c> for
/// the signal path, <c>:178-181</c> for the toggle). <see cref="MachinePartsRestoreSystem"/> then runs the machine's part refresh,
/// and <c>UpgradePowerSystem.OnRefreshParts</c> writes the full draw onto the receiver whatever the thruster's state, so a
/// disabled gyroscope would come back drawing its full load while it shows off. This is ordered after that system and undoes
/// exactly that write.
///
/// <para>For a thruster with <c>Enabled == false</c> it applies the owner's own guard, <c>OriginalLoad != 0 &amp;&amp; Load != 1</c>,
/// and sets the load to 1. It does not call <c>DisableThruster</c> (a restored thruster is off until its power edge) and does
/// nothing for an enabled thruster, whose load the owner leaves at its running value. No prototype sets <c>enabled</c>, and the
/// only writers of <c>Enabled</c> are the owner's four toggle sites, each of which goes through that branch, so a stored
/// disabled thruster already held 1: this restores a stored value, it does not create one. <c>OriginalLoad</c> has to be in place
/// for the guard, and is carried by the manifest before init. It adds no component, and the directed raise skips an entity
/// that is gone.</para>
/// </summary>
public sealed class ThrusterRestoreSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ThrusterComponent, GridRestoredEvent>(OnGridRestored, after: new[] { typeof(MachinePartsRestoreSystem) });
    }

    private void OnGridRestored(EntityUid uid, ThrusterComponent thruster, ref GridRestoredEvent args)
    {
        if (thruster.Enabled)
            return;

        if (TryComp<ApcPowerReceiverComponent>(uid, out var receiver) && thruster.OriginalLoad != 0 && receiver.Load != 1)
            receiver.Load = 1;
    }
}
