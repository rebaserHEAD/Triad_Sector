#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Mono.TargetSeekingAlert;
using Content.Shared._FarHorizons.Power.Generation.FissionGenerator;
using Content.Server._Triad.Drydock.Codec;
using Content.Server.Power.Components;
using Content.Server.Wires;
using Content.Shared.Coordinates;
using Content.Shared.Power;
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
    /// component rows cannot carry goes into a row of its own and comes back at its moment, a null as an explicit null, a
    /// dictionary entry under its own key and an owed one not at all, and a data field the manifest lists as not carried
    /// is taken out of its component's row.
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
            EntityUid? pairedAtStartup = null;
            (bool Found, object? Value) pairedRead = default, loneRead = default, pairedBeforeInit = default;
            bool pairedRowHasKey = false, loneRowNull = false;
            var unwritable = new List<DrydockUnwritableMember>();

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
                pairedBeforeInit = ReadAt(codec, factory, pairedRow, DrydockApplyMoment.BeforeInit, ProviderKey);
                pairedRead = ReadAt(codec, factory, pairedRow, DrydockApplyMoment.AfterStart, ProviderKey);

                var loneRow = Manifest(entMan, factory, codec, lone, unwritable);
                loneRowNull = loneRow != null && loneRow.TryGet<ValueDataNode>(ProviderKey, out var node) && node.IsNull;
                loneRead = ReadAt(codec, factory, loneRow, DrydockApplyMoment.AfterStart, ProviderKey);
            });

            Assert.Multiple(() =>
            {
                Assert.That(pairedAtStartup, Is.EqualTo(cable), "The control: the near receiver has to have paired with the cable at startup.");
                Assert.That(unwritable, Is.Empty, "Nothing on either receiver may be left out as unwritable.");

                Assert.That(pairedRowHasKey, Is.True, "The paired receiver's provider has to be in its manifest row.");
                Assert.That(pairedBeforeInit.Found, Is.False, "The provider is an after-startup member and must not be read before init.");
                Assert.That(pairedRead.Found, Is.True, "The provider has to be read at its moment, after startup.");
                Assert.That(pairedRead.Value, Is.EqualTo(cable), "It has to come back as the cable entity, through the stable ids.");

                Assert.That(loneRowNull, Is.True, "An unpaired receiver's provider has to be written as an explicit null.");
                Assert.That(loneRead.Found, Is.True, "The null has to be read at the provider's moment too.");
                Assert.That(loneRead.Value, Is.Null, "And read back as null, not as a default or an invalid entity.");
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

        /// <summary>
        /// A power wire's cut count travels as an entry of the wires' state data, before init, so the layout rebuild finds it
        /// in place; a pulse does not, because nothing re-arms the timer that would clear it (owed with H12).
        /// </summary>
        [Test]
        public async Task APowerWiresCutCountTravelsAndAPulseDoesNot()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var factory = server.ResolveDependency<IComponentFactory>();
            var map = await pair.CreateTestMap();

            bool hadCut = false, hadPulse = false, pulseWritten = true;
            (bool Found, object? Value) cut = default;
            object? setBack = null;
            var unwritable = new List<DrydockUnwritableMember>();

            await server.WaitPost(() =>
            {
                var wiresSystem = entMan.System<WiresSystem>();
                var airlock = entMan.SpawnEntity("Airlock", map.GridCoords);
                wiresSystem.SetData(airlock, PowerWireActionKey.CutWires, 2);
                wiresSystem.SetData(airlock, PowerWireActionKey.Pulsed, true);
                hadCut = wiresSystem.TryGetData<int?>(airlock, PowerWireActionKey.CutWires, out var before) && before == 2;
                hadPulse = wiresSystem.TryGetData<bool>(airlock, PowerWireActionKey.Pulsed, out var pulsed) && pulsed;

                var codec = Codec(server.ResolveDependency<ISerializationManager>(), entMan, server.ResolveDependency<IGameTiming>(), airlock);
                var row = Manifest(entMan, factory, codec, airlock, unwritable);
                var cutMember = DrydockCodecManifestMembers.Members.Single(m => Equals(m.EntryKey, PowerWireActionKey.CutWires));
                var pulseMember = DrydockCodecManifestMembers.Members.Single(m => Equals(m.EntryKey, PowerWireActionKey.Pulsed));
                pulseWritten = row?.Has(pulseMember.Key) == true;
                cut = ReadAt(codec, factory, row, DrydockApplyMoment.BeforeInit, cutMember.Key);

                // Set back onto the airlock's own state data with the entry taken out first, as a fresh component holds none.
                var wires = entMan.GetComponent<WiresComponent>(airlock);
                wiresSystem.RemoveData(airlock, PowerWireActionKey.CutWires, wires);
                DrydockCodec.SetMember(wires, cutMember, cut.Value);
                setBack = wiresSystem.TryGetData<int?>(airlock, PowerWireActionKey.CutWires, out var after, wires) ? after : null;
            });

            Assert.Multiple(() =>
            {
                Assert.That(hadCut && hadPulse, Is.True, "The control: the airlock has to hold both entries before the write.");
                Assert.That(unwritable, Is.Empty, "Nothing on the airlock may be left out as unwritable.");
                Assert.That(cut.Found, Is.True, "The cut count has to be in the row and read before init.");
                Assert.That(cut.Value, Is.EqualTo(2), "And read as the count it was.");
                Assert.That(setBack, Is.EqualTo(2), "Setting it back has to put it under its own key, where the power wire reads it.");
                Assert.That(pulseWritten, Is.False, "A pulse is owed with H12 and must not be written.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A network id means nothing in another round, so a monitor's turbine travels as the entity it names and comes back
        /// as that entity's network id; one naming nothing on the image comes back null.
        /// </summary>
        [Test]
        public async Task AMonitorsNetworkReferenceTravelsAsTheEntity()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var factory = server.ResolveDependency<IComponentFactory>();

            NetEntity turbineNet = default;
            (bool Found, object? Value) named = default, offImage = default;
            var unwritable = new List<DrydockUnwritableMember>();
            const string key = "GasTurbineMonitor.turbine";

            await server.WaitPost(() =>
            {
                var monitor = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                var turbine = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                var stray = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                var comp = entMan.AddComponent<GasTurbineMonitorComponent>(monitor);
                turbineNet = entMan.GetNetEntity(turbine);
                comp.turbine = turbineNet;

                var codec = Codec(server.ResolveDependency<ISerializationManager>(), entMan, server.ResolveDependency<IGameTiming>(), monitor, turbine);
                named = ReadAt(codec, factory, Manifest(entMan, factory, codec, monitor, unwritable), DrydockApplyMoment.BeforeInit, key);

                // The stray is not on the image, so the codec has no stable id for it.
                comp.turbine = entMan.GetNetEntity(stray);
                offImage = ReadAt(codec, factory, Manifest(entMan, factory, codec, monitor, unwritable), DrydockApplyMoment.BeforeInit, key);
            });

            Assert.Multiple(() =>
            {
                Assert.That(unwritable, Is.Empty, "Nothing on the monitor may be left out as unwritable.");
                Assert.That(named.Found, Is.True, "The turbine reference has to be in the row and read before init.");
                Assert.That(named.Value, Is.EqualTo(turbineNet), "It has to come back as the named entity's network id.");
                Assert.That(offImage.Found, Is.True, "A reference off the image has to be written too.");
                Assert.That(offImage.Value, Is.Null, "And read back as null, not as an invalid network id.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>A member read from a row at a moment, and whether the row held it at that moment at all.</summary>
        private static (bool Found, object? Value) ReadAt(DrydockCodec codec, IComponentFactory factory, MappingDataNode? row, DrydockApplyMoment moment, string key)
        {
            if (row == null)
                return (false, null);

            foreach (var (member, value) in codec.ReadManifest(row, moment, factory))
            {
                if (member.Key == key)
                    return (true, value);
            }

            return (false, null);
        }

        private static MappingDataNode? Manifest(IEntityManager entMan, IComponentFactory factory, DrydockCodec codec, EntityUid uid, List<DrydockUnwritableMember> unwritable) =>
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
