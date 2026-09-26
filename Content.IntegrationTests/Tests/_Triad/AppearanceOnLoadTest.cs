#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad
{
    /// <summary>
    /// Visuals a source seeds in a <see cref="MapInitEvent"/> handler and nowhere else come back on a load through the
    /// drydock image's appearance row, and through nothing at the source: no system seeds them on a startup without a
    /// map init, which is what a load looks like.
    ///
    /// <para>A map created with <c>runMapInit: false</c> gives component startup without map init. Each row's prototype
    /// is spawned on a map-initialised grid, stored through the image, and loaded onto such a map, and every key it had
    /// before the store has to come back with its value. The control is the same prototype spawned bare on the
    /// uninitialised map: the key is absent there, so the row is what brings it back.</para>
    /// </summary>
    /// <remarks>
    /// Keyed by component rather than prototype id, so a rename cannot quietly empty a row. A key absent before the store,
    /// or present on the bare spawn, is reported apart as a row that measures nothing.
    /// </remarks>
    [TestFixture]
    [TestOf(typeof(DrydockImageSystem))]
    public sealed class AppearanceOnLoadTest
    {
        /// <summary>
        /// Component to the appearance keys its system seeds only at map init, as <c>EnumType.Member</c>.
        /// </summary>
        private static readonly (string Component, string[] Keys)[] Expected =
        {
            ("PoweredLight", new[] { "PoweredLightVisuals.BulbState" }),
            ("AtmosAlarmable", new[] { "AtmosMonitorVisuals.AlarmType" }),
            ("BasicEntityAmmoProvider", new[] { "AmmoVisuals.HasAmmo", "AmmoVisuals.AmmoCount", "AmmoVisuals.AmmoMax" }),
            ("MagazineAmmoProvider", new[] { "AmmoVisuals.MagLoaded" }),
            ("PowerCellSlot", new[] { "PowerCellSlotVisuals.Enabled" }),
            ("VendingMachine", new[] { "VendingMachineVisuals.VisualState" }),
            ("Smes", new[] { "SmesVisuals.LastChargeLevel" }),
            ("PowerCharge", new[] { "PowerChargeVisuals.Charge", "PowerChargeVisuals.Active" }),
            ("GasOutletInjector", new[] { "OutletInjectorVisuals.Enabled" }),
            ("Toilet", new[] { "ToiletVisuals.SeatVisualState" }),
            ("ReagentGrinder", new[] { "ReagentGrinderVisualState.BeakerAttached" }),
            ("RadiationCollector", new[] { "RadiationCollectorVisuals.TankInserted" }),
            ("AtmosPlaque", new[] { "AtmosPlaqueVisuals.State" }),
            ("EnergySword", new[] { "ToggleableVisuals.Color" }),
        };

        [Test]
        public async Task TheImageCarriesAppearanceASourceSeedsOnlyAtMapInit()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSys = server.System<SharedMapSystem>();

            EntityUid preInit = default;
            MapId preInitId = default;
            await server.WaitPost(() => preInit = mapSys.CreateMap(out preInitId, runMapInit: false));

            // A SMES seeds its charge level at map init only once the clock is past its visuals delay after its last
            // change, from zero (SmesSystem.UpdateSmesState, SmesComponent.VisualsChangeDelay of a second). Two seconds
            // of ticks put a fresh server past it.
            await pair.RunTicksSync(2 * server.ResolveDependency<IGameTiming>().TickRate);

            // The control on the whole method. If the load side were initialised, every row below would pass by way of
            // the map-init handler rather than the row.
            Assert.That(mapSys.IsInitialized(preInit), Is.False,
                "The load side has to be an uninitialised map, or this test asserts nothing.");

            var missing = new List<string>();
            var changed = new List<string>();
            var staleRows = new List<string>();
            var covered = 0;

            foreach (var (component, keys) in Expected)
            {
                var proto = protoMan.EnumeratePrototypes<EntityPrototype>()
                    .Where(p => !p.Abstract
                                && p.MapSavable
                                && p.Components.ContainsKey(component)
                                && p.Components.ContainsKey("Appearance"))
                    .OrderBy(p => p.ID)
                    .FirstOrDefault();

                if (proto == null)
                {
                    staleRows.Add($"{component}: no savable prototype carries it with an Appearance, so this row measures nothing.");
                    continue;
                }

                var (before, after) = await RoundTrip(pair, proto.ID, preInit);
                var bare = await BareSpawn(pair, proto.ID, preInitId);

                foreach (var key in keys)
                {
                    if (!before.TryGetValue(key, out var value))
                    {
                        staleRows.Add($"{component} ({proto.ID}): {key} is absent even after a map init.");
                        continue;
                    }

                    if (bare.ContainsKey(key))
                    {
                        staleRows.Add($"{component} ({proto.ID}): {key} is seeded on a startup without a map init, so the row is not what brings it back.");
                        continue;
                    }

                    covered++;
                    if (!after.TryGetValue(key, out var loaded))
                        missing.Add($"{component} ({proto.ID}): {key}");
                    else if (!Equals(loaded, value))
                        changed.Add($"{component} ({proto.ID}): {key} {value} became {loaded}");
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(staleRows, Is.Empty,
                    "Rows that measure nothing:" + Environment.NewLine + string.Join(Environment.NewLine, staleRows));

                Assert.That(missing, Is.Empty,
                    $"{missing.Count} appearance key(s) seeded only at map init did not come back through the image:"
                    + Environment.NewLine + string.Join(Environment.NewLine, missing));

                Assert.That(changed, Is.Empty,
                    $"{changed.Count} appearance key(s) came back through the image with another value:"
                    + Environment.NewLine + string.Join(Environment.NewLine, changed));

                Assert.That(covered, Is.GreaterThan(0), "The control: an empty walk proves nothing.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Spawns <paramref name="proto"/> on a grid on a map-initialised map, reads its appearance, stores the grid through
        /// the image and loads it onto <paramref name="preInit"/>, and reads the loaded entity's appearance.
        /// </summary>
        private static async Task<(Dictionary<string, object> Before, Dictionary<string, object> After)> RoundTrip(
            Pair.TestPair pair, string proto, EntityUid preInit)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var mapSys = server.System<SharedMapSystem>();
            var images = server.System<DrydockImageSystem>();

            var before = new Dictionary<string, object>();
            var after = new Dictionary<string, object>();

            await server.WaitPost(() =>
            {
                var map = mapSys.CreateMap(out var mapId);
                var grid = mapSys.CreateGridEntity(mapId);
                mapSys.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));

                var source = entMan.SpawnEntity(proto, new EntityCoordinates(grid.Owner, 0.5f, 0.5f));
                Read(entMan, source, before);

                var stored = images.Store(grid.Owner);
                images.Despawn(grid.Owner);
                entMan.DeleteEntity(map);

                var loaded = images.Load(stored.Image, preInit);
                var entity = loaded.Ids.Keys.First(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == proto);
                Read(entMan, entity, after);

                entMan.DeleteEntity(loaded.Grid);
            });

            return (before, after);
        }

        /// <summary>The appearance of <paramref name="proto"/> spawned on <paramref name="preInit"/> with no grid and no image.</summary>
        private static async Task<Dictionary<string, object>> BareSpawn(Pair.TestPair pair, string proto, MapId preInit)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var keys = new Dictionary<string, object>();

            await server.WaitPost(() =>
            {
                var uid = entMan.SpawnEntity(proto, new MapCoordinates(0, 0, preInit));
                Read(entMan, uid, keys);
                entMan.DeleteEntity(uid);
            });

            return keys;
        }

        /// <summary>
        /// An entity's live appearance keys as <c>EnumType.Member</c>, with their values. The component state is the
        /// supported read: the dictionary is engine-internal and its component is access-locked to the appearance system.
        /// </summary>
        private static void Read(IEntityManager entMan, EntityUid uid, Dictionary<string, object> into)
        {
            if (entMan.TryGetComponent(uid, out AppearanceComponent? appearance)
                && entMan.GetComponentState(entMan.EventBus, appearance, null, GameTick.Zero) is AppearanceComponentState state)
            {
                foreach (var (key, value) in state.Data)
                    into[$"{key.GetType().Name}.{key}"] = value;
            }
        }
    }
}
