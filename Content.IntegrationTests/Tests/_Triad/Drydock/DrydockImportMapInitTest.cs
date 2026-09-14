#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server.DeviceNetwork.Systems;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.StationRecords;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A legacy import's first map init keeps what it spawns. The old save writer never carried an
    /// airlock's door electronics (the loader's map init used to fill them), and an airlock reads its
    /// access from that board: an empty one refuses everyone. The retrieve's transaction deletes
    /// whatever map init spawns, so it cannot put the board back; the import's has to.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockFidelitySystem))]
    public sealed class DrydockImportMapInitTest
    {
        private const string AirlockProto = "AirlockExternalGlass";
        private const string BoardContainer = "board";

        [Test]
        public async Task AnImportKeepsTheDoorElectronicsTheRetrieveWouldDelete()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var drydock = server.System<DrydockSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var containers = server.System<SharedContainerSystem>();
            var access = server.System<AccessReaderSystem>();

            var map = await pair.CreateTestMap();
            await pair.MakeCleanupImmune(map.Grid.Owner);
            var grid = map.Grid.Owner;

            EntityUid stripped = default;
            EntityUid kept = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockMapInitRefire, "revert");
                stripped = entMan.SpawnEntity(AirlockProto, map.GridCoords);
                kept = entMan.SpawnEntity(AirlockProto, map.GridCoords);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(Boards(stripped), Is.EqualTo(1), "The control: a first map init fills an airlock's board.");
                Assert.That(Allowed(stripped), Is.True, "The control: an airlock with its electronics lets an unrestricted user through.");
            });

            // The legacy document: the airlock arrives with its board empty.
            await server.WaitPost(() =>
            {
                foreach (var board in new List<EntityUid>(containers.GetContainer(stripped, BoardContainer).ContainedEntities))
                    entMan.DeleteEntity(board);
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(Boards(stripped), Is.Zero);
                Assert.That(Allowed(stripped), Is.False, "An airlock with no electronics refuses everyone: the lockout.");
            });

            // The retrieve's transaction over the same hull spawns the board and deletes it again.
            DrydockMapInitReport revert = default!;
            await server.WaitPost(() =>
                revert = fidelity.RefireMapInitSliced(grid, new DrydockSyncSlice(DrydockPhases.Retrieve), DrydockMapInitMode.Revert)
                    .GetAwaiter().GetResult());

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(revert.Appeared.GetValueOrDefault("DoorElectronics"), Is.EqualTo(1),
                    "The retrieve's map init filled the empty board, and only that one.");
                Assert.That(Boards(stripped), Is.Zero, "Revert deleted the board it spawned, so the airlock stays locked.");
                Assert.That(Allowed(stripped), Is.False);
            });

            DrydockMapInitReport import = default!;
            await server.WaitPost(() => import = drydock.InitializeImportedShip(grid));

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(import.Mode, Is.EqualTo(DrydockMapInitMode.Import), "revert on the knob is the import's keep-spawns mode.");
                Assert.That(import.Appeared.GetValueOrDefault("DoorElectronics"), Is.EqualTo(1));
                Assert.That(Boards(stripped), Is.EqualTo(1), "The import kept the board it spawned.");
                Assert.That(Allowed(stripped), Is.True, "The imported airlock opens again.");
                Assert.That(Boards(kept), Is.EqualTo(1), "A board that was already filled gets nothing on top.");
                Assert.That(import.HasLeaks, Is.False, import.Detail());
            });

            await server.WaitPost(() =>
            {
                entMan.DeleteEntity(stripped);
                entMan.DeleteEntity(kept);
            });

            await pair.CleanReturnAsync();

            int Boards(EntityUid airlock) => containers.GetContainer(airlock, BoardContainer).ContainedEntities.Count;

            bool Allowed(EntityUid airlock) => access.IsAllowed(
                new List<ProtoId<AccessLevelPrototype>>(),
                new List<StationRecordKey>(),
                airlock,
                entMan.GetComponent<AccessReaderComponent>(airlock));
        }

        /// <summary>
        /// A legacy document carries no device-network state, so the transaction's map init connects
        /// every device fresh: the network files it under a generated address and the frequencies are
        /// resolved from their ids. Putting the stored empty values back left the component naming an
        /// address the network never held, and every device-link signal sent to it was lost. A bought
        /// ship carries all three from its purchase and is the control the play test gave.
        /// </summary>
        [Test]
        public async Task ADeviceStoredWithoutAnAddressComesBackReachable()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var fidelity = server.System<DrydockFidelitySystem>();
            var network = server.System<DeviceNetworkSystem>();

            var map = await pair.CreateTestMap();
            await pair.MakeCleanupImmune(map.Grid.Owner);
            var grid = map.Grid.Owner;

            EntityUid airlock = default;
            await server.WaitPost(() => airlock = entMan.SpawnEntity(AirlockProto, map.GridCoords));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var device = entMan.GetComponent<DeviceNetworkComponent>(airlock);
                Assert.That(device.Address, Is.Not.Empty, "The control: a first map init gives the airlock an address.");
                Assert.That(network.IsDeviceConnected(airlock, device), Is.True);
            });

            // The legacy document: no address, no resolved frequencies, not on any network. Written
            // by reflection because the component's access rules reserve these fields for the network
            // system, and no call of its produces the state a document that never held them loads as.
            await server.WaitPost(() =>
            {
                var device = entMan.GetComponent<DeviceNetworkComponent>(airlock);
                network.DisconnectDevice(airlock, device, preventAutoConnect: false);
                typeof(DeviceNetworkComponent).GetField(nameof(DeviceNetworkComponent.Address))!.SetValue(device, string.Empty);
                typeof(DeviceNetworkComponent).GetField(nameof(DeviceNetworkComponent.ReceiveFrequency))!.SetValue(device, null);
                typeof(DeviceNetworkComponent).GetField(nameof(DeviceNetworkComponent.TransmitFrequency))!.SetValue(device, null);
                Assert.That(network.IsDeviceConnected(airlock, device), Is.False, "The control: the device is off the network.");
            });

            DrydockMapInitReport report = default!;
            await server.WaitPost(() =>
                report = fidelity.RefireMapInitSliced(grid, new DrydockSyncSlice(DrydockPhases.Retrieve), DrydockMapInitMode.Revert)
                    .GetAwaiter().GetResult());

            await server.WaitAssertion(() =>
            {
                var device = entMan.GetComponent<DeviceNetworkComponent>(airlock);
                Assert.That(report.Changed.ContainsKey("DeviceNetworkComponent.Address"), Is.True,
                    "The control: the transaction saw map init write the address.");
                Assert.That(device.Address, Is.Not.Empty, "The address map init gave the device was kept.");
                Assert.That(network.IsAddressPresent(device.DeviceNetId, device.Address), Is.True,
                    "The network holds the device under the address its component names.");
                Assert.That(network.IsDeviceConnected(airlock, device), Is.True);
                Assert.That(device.ReceiveFrequency, Is.Not.Null, "The resolved receive frequency was kept.");
            });

            await server.WaitPost(() => entMan.DeleteEntity(airlock));
            await pair.CleanReturnAsync();
        }
    }
}
