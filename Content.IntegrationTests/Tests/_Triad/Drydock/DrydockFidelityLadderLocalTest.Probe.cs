#nullable enable
using System.Collections.Generic;
using System.Linq;
using Content.Server._Triad.Drydock;
using Content.Server.Atmos.Monitor.Components;
using Content.Server.Power.Components;
using Content.Shared.DeviceNetwork.Components;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    // Scaffolding for one read, not for the suite: LADDER_CONTROL=registration traces when the hull's alarmables and
    // monitors power up against when the monitors record an alarm, from the hull's first load to each retrieve.
    public sealed partial class DrydockFidelityLadderLocalTest
    {
        private static bool RegistrationProbe => Environment.GetEnvironmentVariable("LADDER_CONTROL") == "registration";

        private static string RegistrationState(IEntityManager entMan, EntityUid grid)
        {
            int alarmables = 0, alarmablesPowered = 0, monitors = 0, monitorsPowered = 0, registrations = 0;
            foreach (var uid in entMan.System<DrydockFidelitySystem>().GridTreeList(grid))
            {
                var powered = entMan.TryGetComponent<ApcPowerReceiverComponent>(uid, out var receiver) && receiver.Powered;
                if (entMan.HasComponent<AtmosAlarmableComponent>(uid))
                {
                    alarmables++;
                    if (powered)
                        alarmablesPowered++;
                }

                if (entMan.TryGetComponent<AtmosMonitorComponent>(uid, out var monitor))
                {
                    monitors++;
                    if (powered)
                        monitorsPowered++;
                    registrations += monitor.RegisteredDevices.Count;
                }
            }

            return $"alarmables powered {alarmablesPowered}/{alarmables}, monitors powered {monitorsPowered}/{monitors}, registrations {registrations}";
        }

        private static IEnumerable<string> RegistrationDetail(IEntityManager entMan, EntityUid grid, string moment)
        {
            string Name(EntityUid uid)
            {
                var pos = entMan.GetComponent<TransformComponent>(uid).LocalPosition;
                return $"{entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID ?? "?"}@{(int) MathF.Floor(pos.X)},{(int) MathF.Floor(pos.Y)}";
            }

            bool Powered(EntityUid uid) => entMan.TryGetComponent<ApcPowerReceiverComponent>(uid, out var receiver) && receiver.Powered;

            foreach (var uid in entMan.System<DrydockFidelitySystem>().GridTreeList(grid))
            {
                if (!entMan.HasComponent<AirAlarmComponent>(uid)
                    || !entMan.TryGetComponent<DeviceNetworkComponent>(uid, out var net)
                    || !entMan.TryGetComponent<DeviceListComponent>(uid, out var list))
                {
                    continue;
                }

                var devices = list.Devices
                    .Where(entMan.EntityExists)
                    .Select(device =>
                    {
                        var registered = entMan.TryGetComponent<AtmosMonitorComponent>(device, out var monitor)
                            ? monitor.RegisteredDevices.Contains(net.Address) ? "registered" : "NOT registered"
                            : "no monitor";
                        return $"{Name(device)} {(Powered(device) ? "powered" : "UNPOWERED")} {registered}";
                    })
                    .OrderBy(line => line, StringComparer.Ordinal);

                yield return $"[probe] {moment}: alarm {Name(uid)} {net.Address} {(Powered(uid) ? "powered" : "UNPOWERED")}, "
                             + $"{list.Devices.Count} listed: {string.Join("; ", devices)}";
            }
        }
    }
}
