#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Mono.TargetSeekingAlert;
using Content.Server._Triad.Drydock.Codec;
using Content.Server.Power.Components;
using Content.Shared.Coordinates;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The manifest's row (<see cref="DrydockCodec.WriteManifest"/>, <see cref="DrydockCodec.ReadManifest"/>): a member the
    /// component rows cannot carry goes into a row of its own and comes back at its moment, a null as an explicit null, and a
    /// data field the manifest lists as not carried is taken out of its component's row.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockCodec))]
    public sealed class DrydockCodecManifestRowTest
    {
        [TestPrototypes]
        private const string Prototypes = @"
- type: entity
  id: DrydockManifestReceiverDummy
  components:
  - type: ApcPowerReceiver
  - type: ExtensionCableReceiver
  - type: Transform
    anchored: true
";

        private const string ProviderKey = "ExtensionCableReceiver.Provider";

        [Test]
        public async Task AReceiversProviderTravelsInTheManifestRow()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var factory = server.ResolveDependency<IComponentFactory>();

            EntityUid cable = default, paired = default, lone = default;
            EntityUid? pairedAtStartup = null, pairedRead = null, loneRead = EntityUid.Invalid;
            bool pairedRowHasKey = false, loneRowNull = false, pairedBeforeInit = true;
            var unwritable = new Dictionary<string, int>();

            await server.WaitPost(() =>
            {
                var mapSystem = entMan.System<SharedMapSystem>();
                mapSystem.CreateMap(out var mapId);
                var grid = mapSystem.CreateGridEntity(mapId);
                for (var i = 0; i < 12; i++)
                    mapSystem.SetTile(grid, new Vector2i(0, i), new Tile(1));

                // One receiver a tile from a cable, and one eight tiles off with nothing in reach.
                cable = entMan.SpawnEntity("CableApcExtension", grid.Owner.ToCoordinates(0, 0));
                paired = entMan.SpawnEntity("DrydockManifestReceiverDummy", grid.Owner.ToCoordinates(0, 1));
                lone = entMan.SpawnEntity("DrydockManifestReceiverDummy", grid.Owner.ToCoordinates(0, 9));
                pairedAtStartup = entMan.GetComponent<ExtensionCableReceiverComponent>(paired).Provider?.Owner;

                var codec = Codec(server.ResolveDependency<ISerializationManager>(), entMan, server.ResolveDependency<IGameTiming>(), cable, paired, lone);

                var pairedRow = Manifest(entMan, factory, codec, paired, unwritable);
                pairedRowHasKey = pairedRow?.Has(ProviderKey) == true;
                pairedBeforeInit = pairedRow != null && codec.ReadManifest(pairedRow, DrydockApplyMoment.BeforeInit, factory).Any(m => m.Member.Member == "Provider");
                pairedRead = pairedRow == null
                    ? null
                    : (EntityUid?) codec.ReadManifest(pairedRow, DrydockApplyMoment.AfterStart, factory).Single(m => m.Member.Member == "Provider").Value;

                var loneRow = Manifest(entMan, factory, codec, lone, unwritable);
                loneRowNull = loneRow != null && loneRow.TryGet<ValueDataNode>(ProviderKey, out var node) && node.IsNull;
                loneRead = loneRow == null
                    ? EntityUid.Invalid
                    : (EntityUid?) codec.ReadManifest(loneRow, DrydockApplyMoment.AfterStart, factory).Single(m => m.Member.Member == "Provider").Value;
            });

            Assert.Multiple(() =>
            {
                Assert.That(pairedAtStartup, Is.EqualTo(cable), "The control: the near receiver has to have paired with the cable at startup.");
                Assert.That(unwritable, Is.Empty, "Nothing on either receiver may be left out as unwritable.");

                Assert.That(pairedRowHasKey, Is.True, "The paired receiver's provider has to be in its manifest row.");
                Assert.That(pairedBeforeInit, Is.False, "The provider is an after-startup member and must not be read before init.");
                Assert.That(pairedRead, Is.EqualTo(cable), "It has to come back as the cable entity, through the stable ids.");

                Assert.That(loneRowNull, Is.True, "An unpaired receiver's provider has to be written as an explicit null.");
                Assert.That(loneRead, Is.Null, "And read back as null, not as a default or an invalid entity.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A grid's alerter list is a data field its power handler rebuilds, so a carried copy doubles on every restore (F36).
        /// The engine's own write carries it, which is the control; the codec's must not.
        /// </summary>
        [Test]
        public async Task ANotCarriedDataFieldIsLeftOutOfItsRow()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var serialization = server.ResolveDependency<ISerializationManager>();

            bool engineHasIt = false, codecHasIt = true;
            await server.WaitPost(() =>
            {
                var holder = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                var extra = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                var alert = entMan.AddComponent<TargetSeekerAlertGridComponent>(holder);
                alert.Alerters.Add(extra);

                var codec = Codec(serialization, entMan, server.ResolveDependency<IGameTiming>(), holder, extra);
                var engine = serialization.WriteValueAs<MappingDataNode>(typeof(TargetSeekerAlertGridComponent), alert, alwaysWrite: true, context: codec.Context);
                engineHasIt = engine.Has("alerters");
                codecHasIt = codec.Write((holder, entMan.GetComponent<MetaDataComponent>(holder)), alert).Has("alerters");
            });

            Assert.Multiple(() =>
            {
                Assert.That(engineHasIt, Is.True, "The control: the engine's write has to carry the alerter list, or this proves nothing.");
                Assert.That(codecHasIt, Is.False, "The codec's row must not carry a data field the manifest lists as not carried.");
            });

            await pair.CleanReturnAsync();
        }

        private static MappingDataNode? Manifest(IEntityManager entMan, IComponentFactory factory, DrydockCodec codec, EntityUid uid, Dictionary<string, int> unwritable) =>
            codec.WriteManifest((uid, entMan.GetComponent<MetaDataComponent>(uid)), entMan.GetComponents(uid).ToList(), factory, unwritable);

        /// <summary>A codec whose stable ids are the given entities' positions, resolved back to the same entities.</summary>
        private static DrydockCodec Codec(ISerializationManager serialization, IEntityManager entMan, IGameTiming timing, params EntityUid[] entities)
        {
            var ids = entities.Select((uid, i) => (uid, id: (long) i + 1)).ToDictionary(e => e.uid, e => e.id);
            return new DrydockCodec(serialization, entMan, timing,
                uid => ids.TryGetValue(uid, out var id) ? id : null,
                id => entities[(int) id - 1]);
        }
    }
}
