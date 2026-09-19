#nullable enable

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Coordinates;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// <see cref="ExtensionCableSystem.TryPairReceiver"/>: a receiver handed a provider that is not the nearest one takes it
    /// with both sides' bookkeeping, and one it may not take changes nothing. The receiver side of the events is seen in the
    /// receiver's APC provider, which only its connected-event handler sets; the provider side in the provider's APC
    /// receiver list, which only its handler fills.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(ExtensionCableSystem))]
    public sealed class DrydockCablePairingTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: DrydockCableReceiverDummy
  components:
  - type: ApcPowerReceiver
  - type: ExtensionCableReceiver
  - type: Transform
    anchored: true
";

        [Test]
        public async Task AReceiverTakesTheProviderItIsGivenAndRefusesOneItMayNot()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var cables = server.System<ExtensionCableSystem>();

            EntityUid near = default, far = default, outOfRange = default, disconnected = default, receiver = default;
            EntityUid? startup = null, paired = null, afterRange = null, afterConnectable = null, apcProvider = null;
            bool pairedFar = false, refusedRange = true, refusedConnectable = true;
            bool farLists = false, nearLists = true, farApcLists = false, nearApcLists = true;

            await server.WaitPost(() =>
            {
                var mapSystem = entMan.System<SharedMapSystem>();
                mapSystem.CreateMap(out var mapId);
                var grid = mapSystem.CreateGridEntity(mapId);
                for (var i = 0; i < 9; i++)
                    mapSystem.SetTile(grid, new Vector2i(0, i), new Tile(1));

                // The receiver at y = 3: the near cable one tile off, the far one three (the default range, so in reach),
                // one four off and out of reach, and one in reach that is not connectable.
                far = entMan.SpawnEntity("CableApcExtension", grid.Owner.ToCoordinates(0, 0));
                near = entMan.SpawnEntity("CableApcExtension", grid.Owner.ToCoordinates(0, 2));
                outOfRange = entMan.SpawnEntity("CableApcExtension", grid.Owner.ToCoordinates(0, 7));
                disconnected = entMan.SpawnEntity("CableApcExtension", grid.Owner.ToCoordinates(0, 4));
                receiver = entMan.SpawnEntity("DrydockCableReceiverDummy", grid.Owner.ToCoordinates(0, 3));

                var receiverComp = entMan.GetComponent<ExtensionCableReceiverComponent>(receiver);
                startup = receiverComp.Provider?.Owner;

                pairedFar = cables.TryPairReceiver((receiver, receiverComp), (far, entMan.GetComponent<ExtensionCableProviderComponent>(far)));
                paired = receiverComp.Provider?.Owner;
                apcProvider = entMan.GetComponent<ApcPowerReceiverComponent>(receiver).Provider?.Owner;
                farLists = entMan.GetComponent<ExtensionCableProviderComponent>(far).LinkedReceivers.Any(r => r.Owner == receiver);
                nearLists = entMan.GetComponent<ExtensionCableProviderComponent>(near).LinkedReceivers.Any(r => r.Owner == receiver);
                farApcLists = entMan.GetComponent<ApcPowerProviderComponent>(far).LinkedReceivers.Any(r => r.Owner == receiver);
                nearApcLists = entMan.GetComponent<ApcPowerProviderComponent>(near).LinkedReceivers.Any(r => r.Owner == receiver);

                refusedRange = !cables.TryPairReceiver((receiver, receiverComp), (outOfRange, entMan.GetComponent<ExtensionCableProviderComponent>(outOfRange)));
                afterRange = receiverComp.Provider?.Owner;

                // Unanchored, as a cable pried up is: the provider's anchor handler takes its connectable flag away.
                entMan.System<SharedTransformSystem>().Unanchor(disconnected);
                var off = entMan.GetComponent<ExtensionCableProviderComponent>(disconnected);
                refusedConnectable = !off.Connectable && !cables.TryPairReceiver((receiver, receiverComp), (disconnected, off));
                afterConnectable = receiverComp.Provider?.Owner;
            });

            Assert.Multiple(() =>
            {
                Assert.That(startup, Is.EqualTo(near), "The control: at startup the receiver takes the nearest cable, which is what can go wrong.");

                Assert.That(pairedFar, Is.True, "A cable in range and connectable has to be accepted.");
                Assert.That(paired, Is.EqualTo(far), "The receiver has to be on the cable it was given.");
                Assert.That(farLists && !nearLists, Is.True, "The given cable has to list the receiver, and the one it left must not.");
                Assert.That(apcProvider, Is.EqualTo(far), "The receiver's connected event has to have run: its APC provider follows the cable.");
                Assert.That(farApcLists && !nearApcLists, Is.True, "The provider's connected event has to have run: its APC receiver list follows too.");

                Assert.That(refusedRange, Is.True, "A cable out of range has to be refused.");
                Assert.That(afterRange, Is.EqualTo(far), "And the refusal must change nothing.");
                Assert.That(refusedConnectable, Is.True, "A cable that is not connectable has to be refused.");
                Assert.That(afterConnectable, Is.EqualTo(far), "And that refusal must change nothing either.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
