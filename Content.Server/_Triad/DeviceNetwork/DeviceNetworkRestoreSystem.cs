using Content.Server._Triad.Drydock.Loader;
using Content.Server.DeviceNetwork.Components;
using Content.Server.DeviceNetwork.Systems;
using Content.Shared.DeviceNetwork.Components;

namespace Content.Server._Triad.DeviceNetwork;

/// <summary>
/// Joins a restored grid's devices to their networks. Map init joins a device to its network only when it auto-connects
/// (<c>DeviceNetworkSystem.OnMapInit</c>), and a restore raises no map init, so a loaded device is in no network: it cannot
/// send, and nothing reaches it. This does that join at the head of the restore, before any entity's own rebuild, with the
/// same gate and no more.
///
/// <para>The gate is <see cref="DeviceNetworkComponent.AutoConnect"/>, or a singleton server that is
/// <see cref="SingletonDeviceNetServerComponent.Active"/> (the crew monitoring server has <c>autoConnect: false</c> and is
/// joined by its own system when it is the active one). A device a player or an admin disconnected stays out, because
/// disconnecting clears <c>AutoConnect</c> and the clear is saved. The frequencies are not resolved again from their
/// prototype ids: the resolved values are data fields already, and resolving them again would undo a retune.</para>
///
/// <para>A device already in its network is left alone, because <c>ConnectDevice</c> on one that is connected with a
/// generated address finds its own address taken and gives it a new one. A device keeps the address it had. This adds no
/// component to a restored entity and skips an entity that is gone by the time the list reaches it.</para>
/// </summary>
public sealed class DeviceNetworkRestoreSystem : EntitySystem
{
    [Dependency] private DeviceNetworkSystem _networks = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GridRestoringEvent>(OnGridRestoring);
    }

    private void OnGridRestoring(ref GridRestoringEvent args)
    {
        foreach (var uid in args.Entities)
        {
            if (TerminatingOrDeleted(uid) || !TryComp<DeviceNetworkComponent>(uid, out var device))
                continue;

            // Read only: the singleton's Active is its own system's to write.
            var joins = device.AutoConnect
                        || (TryComp<SingletonDeviceNetServerComponent>(uid, out var server) && server.Active);
            if (!joins || _networks.IsDeviceConnected(uid, device))
                continue;

            if (!_networks.ConnectDevice(uid, device))
                Log.Warning($"A restored device {ToPrettyString(uid)} could not join its network: its address {device.Address} is taken.");
        }
    }
}
