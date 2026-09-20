#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.DeviceNetwork.Components;
using Content.Server.DeviceNetwork.Systems;
using Content.Shared.DeviceNetwork.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// H18: a restored device is in its network again. Map init joins a device only when it auto-connects, and a restore
    /// raises no map init, so without the restore handler every device on a loaded grid is silent: it cannot send, and
    /// nothing reaches it. The handler mirrors that gate and no more: a device that auto-connects joins, a device an admin
    /// or a player disconnected stays out (its cleared AutoConnect is saved), and a singleton server joins when it was the
    /// active one. A device keeps the address it had.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DeviceNetworkSystem))]
    public sealed class DrydockDeviceNetworkRestoreTest
    {
        [Test]
        public async Task ARestoredDeviceJoinsItsNetworkByTheMapInitGateAndKeepsItsAddress()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var networks = server.System<DeviceNetworkSystem>();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();

            var grid = map.Grid.Owner;
            EntityUid joined = default, disconnected = default, crewServer = default;
            string joinedAddress = string.Empty, serverAddress = string.Empty;
            bool joinedBefore = false, serverBefore = false, disconnectedBefore = true;
            await server.WaitPost(() =>
            {
                server.System<SharedMapSystem>().SetTile(grid, entMan.GetComponent<MapGridComponent>(grid), new Vector2i(1, 0), map.Tile.Tile);

                // Spawned on a live map, so map init has joined the auto-connecting ones.
                joined = entMan.SpawnEntity("AirAlarm", new EntityCoordinates(grid, 0.5f, 0.5f));
                disconnected = entMan.SpawnEntity("AirAlarm", new EntityCoordinates(grid, 0.5f, 0.5f));
                crewServer = entMan.SpawnEntity("CrewMonitoringServer", new EntityCoordinates(grid, 1.5f, 0.5f));

                // A manual disconnect clears AutoConnect, and that is saved.
                networks.DisconnectDevice(disconnected, null, preventAutoConnect: true);

                // The crew server has autoConnect off and Active on by default (SingletonDeviceNetServerComponent.cs:18-19); its own
                // system joins it to the network when something asks for the active server, and this does the same by the public call.
                networks.ConnectDevice(crewServer);

                joinedBefore = networks.IsDeviceConnected(joined, null);
                disconnectedBefore = networks.IsDeviceConnected(disconnected, null);
                serverBefore = networks.IsDeviceConnected(crewServer, null);
                joinedAddress = entMan.GetComponent<DeviceNetworkComponent>(joined).Address;
                serverAddress = entMan.GetComponent<DeviceNetworkComponent>(crewServer).Address;
            });

            Assert.Multiple(() =>
            {
                Assert.That(joinedBefore, Is.True, "The control: an auto-connecting device is in its network after map init.");
                Assert.That(disconnectedBefore, Is.False, "The control: a disconnected device is out of it.");
                Assert.That(serverBefore, Is.True, "The control: the active crew server is in it.");
                Assert.That(joinedAddress, Is.Not.Empty);
                Assert.That(serverAddress, Is.Not.Empty);
            });

            DrydockLoadResult result = default!;
            await server.WaitPost(() =>
            {
                var stored = image.Store(grid);
                image.Despawn(grid);
                result = image.Load(stored.Image, map.MapUid);
            });

            await server.WaitAssertion(() =>
            {
                EntityUid Find(string prototype, bool wantAutoConnect) => fidelity.GridTreeList(result.Grid)
                    .Where(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == prototype)
                    .Single(uid => prototype != "AirAlarm" || entMan.GetComponent<DeviceNetworkComponent>(uid).AutoConnect == wantAutoConnect);

                var joinedAfter = Find("AirAlarm", true);
                var disconnectedAfter = Find("AirAlarm", false);
                var serverAfter = Find("CrewMonitoringServer", true);

                Assert.Multiple(() =>
                {
                    Assert.That(networks.IsDeviceConnected(joinedAfter, null), Is.True, "An auto-connecting device has to be in its network after a load.");
                    Assert.That(entMan.GetComponent<DeviceNetworkComponent>(joinedAfter).Address, Is.EqualTo(joinedAddress), "And keep the address it had.");

                    Assert.That(networks.IsDeviceConnected(disconnectedAfter, null), Is.False, "A device that was disconnected has to stay out.");
                    Assert.That(entMan.GetComponent<DeviceNetworkComponent>(disconnectedAfter).AutoConnect, Is.False);

                    Assert.That(entMan.GetComponent<SingletonDeviceNetServerComponent>(serverAfter).Active, Is.True, "The crew server was active and stays so.");
                    Assert.That(networks.IsDeviceConnected(serverAfter, null), Is.True, "An active singleton server has to be in its network after a load.");
                    Assert.That(entMan.GetComponent<DeviceNetworkComponent>(serverAfter).Address, Is.EqualTo(serverAddress), "And keep its address.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The restore handler contract: an entry an earlier handler deleted is skipped without a throw, a device already in its
        /// network is left alone (its address is not reassigned), and no component is added to any entity on the list.
        /// </summary>
        [Test]
        public async Task TheHandlerSkipsAGoneEntryLeavesAJoinedDeviceAloneAndAddsNoComponent()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var networks = server.System<DeviceNetworkSystem>();

            var grid = map.Grid.Owner;
            EntityUid gone = default, present = default;
            string addressBefore = string.Empty;
            var componentsBefore = new List<string>();
            await server.WaitPost(() =>
            {
                present = entMan.SpawnEntity("AirAlarm", new EntityCoordinates(grid, 0.5f, 0.5f));
                gone = entMan.SpawnEntity("AirAlarm", new EntityCoordinates(grid, 0.5f, 0.5f));
                addressBefore = entMan.GetComponent<DeviceNetworkComponent>(present).Address;
                componentsBefore = entMan.GetComponents(present).Select(component => component.GetType().Name).OrderBy(name => name).ToList();
                entMan.DeleteEntity(gone);

                // The stale entry first, then the live device, which map init already joined.
                var ev = new GridRestoringEvent(grid, new List<EntityUid> { gone, present });
                entMan.EventBus.RaiseEvent(EventSource.Local, ref ev);
            });

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(networks.IsDeviceConnected(present, null), Is.True, "The joined device stays joined.");
                    Assert.That(entMan.GetComponent<DeviceNetworkComponent>(present).Address, Is.EqualTo(addressBefore),
                        "A device already in its network keeps its address: connecting it again would have given it a new one.");
                    Assert.That(entMan.GetComponents(present).Select(component => component.GetType().Name).OrderBy(name => name), Is.EqualTo(componentsBefore),
                        "The handler adds no component to a restored entity.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}
