#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Trade;
using Content.Server._Triad.Cargo;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Cargo.Systems;
using Content.Shared._NF.Trade;
using Content.Shared.Labels.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A trade crate's destination, carried by <see cref="TradeCrateCarrySystem"/> on the carried-values seam. A crate draws
    /// its destination at component init, so without the carry a restored crate points at a random station and pays the
    /// elsewhere price where it was going. Four destinations off the grid, each on its own prototype, as the ladder places
    /// them.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(TradeCrateCarrySystem))]
    public sealed class DrydockTradeCrateCarryTest
    {
        private const string CrateId = "CrateTradeSecureNormal";

        private static readonly (string Name, string Proto)[] Destinations =
        {
            ("Depot A", "CargoA"), ("Depot B", "CargoB"), ("Depot C", "CargoC"), ("Beacon", "Beacon"),
        };

        /// <summary>
        /// At the head of the restore, before any directed handler ran: records each crate's destination and, when armed,
        /// binds every crate to <see cref="SetOnHeadTo"/>, so init's random draw cannot pass a test by chance. A crate with
        /// the probe asks for a second directed raise.
        /// </summary>
        private sealed class DrydockTradeCrateRecorderSystem : EntitySystem
        {
            public EntityUid? SetOnHeadTo;
            public readonly Dictionary<EntityUid, string?> AtHead = new();
            private bool _reraising;

            public override void Initialize()
            {
                base.Initialize();
                SubscribeLocalEvent<GridRestoringEvent>(OnHead);
                SubscribeLocalEvent<DrydockTradeCrateProbeComponent, GridRestoredEvent>(OnRestored);
            }

            private void OnHead(ref GridRestoringEvent ev)
            {
                // Resolved here, not injected: a test assembly's systems are registered on the client too, which has no
                // cargo system, and only the server raises this event.
                var cargo = EntityManager.System<CargoSystem>();
                foreach (var uid in ev.Entities)
                {
                    if (!TryComp<TradeCrateComponent>(uid, out var crate))
                        continue;

                    if (SetOnHeadTo is { } to)
                        cargo.SetTradeCrateDestination((uid, crate), to);

                    AtHead[uid] = cargo.TradeCrateDestinationProto(crate);
                }
            }

            private void OnRestored(Entity<DrydockTradeCrateProbeComponent> ent, ref GridRestoredEvent args)
            {
                if (!ent.Comp.RaiseTwice || _reraising)
                    return;

                _reraising = true;
                var again = args;
                RaiseLocalEvent(ent.Owner, ref again);
                _reraising = false;
            }
        }

        private readonly record struct CrateState(EntityUid Destination, string? Icon, string? Label, string Name);

        private static CrateState Read(IEntityManager entMan, EntityUid crate)
        {
            var icon = entMan.System<SharedAppearanceSystem>().TryGetData<string>(crate, TradeCrateVisuals.DestinationIcon, out var data) ? data : null;
            var label = entMan.TryGetComponent<LabelComponent>(crate, out var labelComp) ? labelComp.CurrentLabel : null;
            return new CrateState(entMan.GetComponent<TradeCrateComponent>(crate).DestinationStation, icon, label, entMan.GetComponent<MetaDataComponent>(crate).EntityName);
        }

        /// <summary>The four destinations, off the grid on the map, by prototype id.</summary>
        private static Dictionary<string, EntityUid> PlaceDestinations(IEntityManager entMan, MapId mapId)
        {
            var placed = new Dictionary<string, EntityUid>();
            for (var i = 0; i < Destinations.Length; i++)
            {
                var (name, proto) = Destinations[i];
                placed[proto] = PlaceDestination(entMan, mapId, name, proto, i);
            }

            return placed;
        }

        private static EntityUid PlaceDestination(IEntityManager entMan, MapId mapId, string name, string proto, int slot)
        {
            var marker = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(5000f + slot * 10f, 5000f), mapId));
            entMan.System<MetaDataSystem>().SetEntityName(marker, name);
            entMan.AddComponent(marker, new TradeCrateDestinationComponent { DestinationProto = proto });
            return marker;
        }

        /// <summary>A crate on the grid, bound to <paramref name="destination"/>.</summary>
        private static EntityUid PlaceCrate(IEntityManager entMan, EntityUid grid, EntityUid destination)
        {
            var crate = entMan.SpawnEntity(CrateId, new EntityCoordinates(grid, 0.5f, 0.5f));
            entMan.System<CargoSystem>().SetTradeCrateDestination((crate, entMan.GetComponent<TradeCrateComponent>(crate)), destination);
            return crate;
        }

        private static EntityUid Restored(IEntityManager entMan, DrydockFidelitySystem fidelity, EntityUid grid) =>
            fidelity.GridTreeList(grid).Single(entMan.HasComponent<TradeCrateComponent>);

        private static bool CarriesKey(DrydockImage image) =>
            image.Entities.Any(e => e.Rows.TryGetValue(DrydockImageSystem.CarriedRow, out var row) && row.Contains(TradeCrateCarrySystem.DestinationKey));

        private readonly record struct Rig(TestPair Pair, IEntityManager EntMan, TestMapData Map, DrydockImageSystem Image,
            DrydockFidelitySystem Fidelity, DrydockTradeCrateRecorderSystem Recorder);

        private static async Task<Rig> Start()
        {
            var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
            var server = pair.Server;
            var map = await pair.CreateTestMap();
            var recorder = server.System<DrydockTradeCrateRecorderSystem>();
            recorder.AtHead.Clear();
            recorder.SetOnHeadTo = null;
            return new Rig(pair, server.EntMan, map, server.System<DrydockImageSystem>(), server.System<DrydockFidelitySystem>(), recorder);
        }

        /// <summary>
        /// Test 1: a crate bound to the <c>CargoB</c> destination comes back bound to it: the uid the price reads, its icon and
        /// its label. Control: at the head of the restore the recorder bound it to <c>Beacon</c>, so only the handler can have
        /// put it back on <c>CargoB</c>, whatever init drew.
        /// </summary>
        [Test]
        public async Task ARestoredCrateKeepsItsDestination()
        {
            var rig = await Start();
            var (pair, entMan, map) = (rig.Pair, rig.EntMan, rig.Map);

            DrydockLoadResult result = default!;
            Dictionary<string, EntityUid> markers = default!;
            await pair.Server.WaitPost(() =>
            {
                markers = PlaceDestinations(entMan, map.MapId);
                PlaceCrate(entMan, map.Grid.Owner, markers["CargoB"]);

                var stored = rig.Image.Store(map.Grid.Owner);
                rig.Image.Despawn(map.Grid.Owner);
                rig.Recorder.SetOnHeadTo = markers["Beacon"];
                try
                {
                    result = rig.Image.Load(stored.Image, map.MapUid);
                }
                finally
                {
                    rig.Recorder.SetOnHeadTo = null;
                }
            });

            await pair.Server.WaitAssertion(() =>
            {
                var crate = Restored(entMan, rig.Fidelity, result.Grid);
                var state = Read(entMan, crate);

                Assert.Multiple(() =>
                {
                    Assert.That(rig.Recorder.AtHead[crate], Is.EqualTo("Beacon"), "The control: at the head the crate was bound elsewhere.");
                    Assert.That(state.Destination, Is.EqualTo(markers["CargoB"]), "The destination the price reads.");
                    Assert.That(state.Icon, Is.EqualTo("CargoB"));
                    Assert.That(state.Label, Is.EqualTo("Depot B"));
                    Assert.That(state.Name, Does.Contain("Depot B"), "The label names the crate.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 2: the key is the destination's prototype, not its uid or its name. The <c>CargoB</c> destination is replaced
        /// between the store and the load by a new one with the same prototype and another name, and the crate binds to the
        /// new one, with its name on the label. Control: as test 1's.
        /// </summary>
        [Test]
        public async Task ARestoredCrateBindsToTheDestinationHoldingItsPrototypeThisRound()
        {
            var rig = await Start();
            var (pair, entMan, map) = (rig.Pair, rig.EntMan, rig.Map);

            DrydockLoadResult result = default!;
            Dictionary<string, EntityUid> markers = default!;
            EntityUid replacement = default;
            await pair.Server.WaitPost(() =>
            {
                markers = PlaceDestinations(entMan, map.MapId);
                PlaceCrate(entMan, map.Grid.Owner, markers["CargoB"]);

                var stored = rig.Image.Store(map.Grid.Owner);
                rig.Image.Despawn(map.Grid.Owner);
                entMan.DeleteEntity(markers["CargoB"]);
                replacement = PlaceDestination(entMan, map.MapId, "Depot B Rebuilt", "CargoB", 5);

                rig.Recorder.SetOnHeadTo = markers["Beacon"];
                try
                {
                    result = rig.Image.Load(stored.Image, map.MapUid);
                }
                finally
                {
                    rig.Recorder.SetOnHeadTo = null;
                }
            });

            await pair.Server.WaitAssertion(() =>
            {
                var crate = Restored(entMan, rig.Fidelity, result.Grid);
                var state = Read(entMan, crate);

                Assert.Multiple(() =>
                {
                    Assert.That(rig.Recorder.AtHead[crate], Is.EqualTo("Beacon"), "The control: at the head the crate was bound elsewhere.");
                    Assert.That(state.Destination, Is.EqualTo(replacement), "The destination holding the prototype this round.");
                    Assert.That(state.Icon, Is.EqualTo("CargoB"));
                    Assert.That(state.Label, Is.EqualTo("Depot B Rebuilt"), "The label is this round's destination's name.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 3: a carried prototype no destination holds this round leaves the crate on the destination init drew, one of
        /// the other three, with its uid, icon and label agreeing. The warning is not asserted: the server's test log fails
        /// on errors only. Control: the image carried the key.
        /// </summary>
        [Test]
        public async Task ACrateWhoseDestinationIsGoneKeepsTheOneDrawnAtLoad()
        {
            var rig = await Start();
            var (pair, entMan, map) = (rig.Pair, rig.EntMan, rig.Map);

            DrydockLoadResult result = default!;
            DrydockImageStoreResult stored = default!;
            Dictionary<string, EntityUid> markers = default!;
            await pair.Server.WaitPost(() =>
            {
                markers = PlaceDestinations(entMan, map.MapId);
                PlaceCrate(entMan, map.Grid.Owner, markers["CargoB"]);

                stored = rig.Image.Store(map.Grid.Owner);
                rig.Image.Despawn(map.Grid.Owner);
                entMan.DeleteEntity(markers["CargoB"]);
                result = rig.Image.Load(stored.Image, map.MapUid);
            });

            await pair.Server.WaitAssertion(() =>
            {
                var crate = Restored(entMan, rig.Fidelity, result.Grid);
                var state = Read(entMan, crate);
                var live = Destinations.Where(d => d.Proto != "CargoB").ToDictionary(d => markers[d.Proto], d => d);

                Assert.Multiple(() =>
                {
                    Assert.That(CarriesKey(stored.Image), Is.True, "The control: the image carried the destination.");
                    Assert.That(live.Keys, Does.Contain(state.Destination), "A live destination, drawn at load.");
                    if (live.TryGetValue(state.Destination, out var drawn))
                    {
                        Assert.That(state.Icon, Is.EqualTo(drawn.Proto), "The icon is the drawn destination's.");
                        Assert.That(state.Label, Is.EqualTo(drawn.Name), "The label is the drawn destination's.");
                    }
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 4: when the round has no destination at all, the crate comes back with none, no icon and no label, as a crate
        /// born in such a round is, rather than with the stored icon and label claiming one. Control: before the store it had
        /// both.
        /// </summary>
        [Test]
        public async Task ACrateInARoundWithNoDestinationHasNone()
        {
            var rig = await Start();
            var (pair, entMan, map) = (rig.Pair, rig.EntMan, rig.Map);

            DrydockLoadResult result = default!;
            CrateState before = default;
            await pair.Server.WaitPost(() =>
            {
                var markers = PlaceDestinations(entMan, map.MapId);
                var crate = PlaceCrate(entMan, map.Grid.Owner, markers["CargoB"]);
                before = Read(entMan, crate);

                var stored = rig.Image.Store(map.Grid.Owner);
                rig.Image.Despawn(map.Grid.Owner);
                foreach (var marker in markers.Values)
                    entMan.DeleteEntity(marker);

                result = rig.Image.Load(stored.Image, map.MapUid);
            });

            await pair.Server.WaitAssertion(() =>
            {
                var state = Read(entMan, Restored(entMan, rig.Fidelity, result.Grid));

                Assert.Multiple(() =>
                {
                    Assert.That(before.Icon, Is.EqualTo("CargoB"), "The control: the stored crate had an icon.");
                    Assert.That(before.Label, Is.EqualTo("Depot B"), "The control: and a label.");
                    Assert.That(state.Destination, Is.EqualTo(EntityUid.Invalid));
                    Assert.That(state.Icon, Is.Null);
                    Assert.That(state.Label, Is.Null);
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 5: a crate with no destination carries nothing, and loads with the destination init draws. Control: it had no
        /// destination when stored, because none existed when it was spawned.
        /// </summary>
        [Test]
        public async Task ACrateWithNoDestinationCarriesNothing()
        {
            var rig = await Start();
            var (pair, entMan, map) = (rig.Pair, rig.EntMan, rig.Map);

            DrydockLoadResult result = default!;
            DrydockImageStoreResult stored = default!;
            CrateState before = default;
            Dictionary<string, EntityUid> markers = default!;
            await pair.Server.WaitPost(() =>
            {
                var crate = entMan.SpawnEntity(CrateId, new EntityCoordinates(map.Grid.Owner, 0.5f, 0.5f));
                before = Read(entMan, crate);

                stored = rig.Image.Store(map.Grid.Owner);
                rig.Image.Despawn(map.Grid.Owner);
                markers = PlaceDestinations(entMan, map.MapId);
                result = rig.Image.Load(stored.Image, map.MapUid);
            });

            await pair.Server.WaitAssertion(() =>
            {
                var state = Read(entMan, Restored(entMan, rig.Fidelity, result.Grid));

                Assert.Multiple(() =>
                {
                    Assert.That(before.Destination, Is.EqualTo(EntityUid.Invalid), "The control: the crate had no destination.");
                    Assert.That(CarriesKey(stored.Image), Is.False, "Nothing carried.");
                    Assert.That(markers.Values, Does.Contain(state.Destination), "The destination init drew at load.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Test 6: the handler contract. A second directed raise in the same restore leaves the crate bound as the first did
        /// and adds no component, and a raise at a crate that is gone is tolerated. Control: the probe asked for the raise.
        /// </summary>
        [Test]
        public async Task ASecondRaiseChangesNothingAndAGoneCrateIsTolerated()
        {
            var rig = await Start();
            var (pair, entMan, map) = (rig.Pair, rig.EntMan, rig.Map);

            DrydockLoadResult result = default!;
            Dictionary<string, EntityUid> markers = default!;
            List<string> componentsBefore = new();
            await pair.Server.WaitPost(() =>
            {
                markers = PlaceDestinations(entMan, map.MapId);
                var crate = PlaceCrate(entMan, map.Grid.Owner, markers["CargoB"]);
                entMan.EnsureComponent<DrydockTradeCrateProbeComponent>(crate).RaiseTwice = true;
                componentsBefore = entMan.GetComponents(crate).Select(c => c.GetType().Name).OrderBy(n => n).ToList();

                var stored = rig.Image.Store(map.Grid.Owner);
                rig.Image.Despawn(map.Grid.Owner);
                result = rig.Image.Load(stored.Image, map.MapUid);
            });

            await pair.Server.WaitAssertion(() =>
            {
                var crate = Restored(entMan, rig.Fidelity, result.Grid);
                var state = Read(entMan, crate);
                var gone = entMan.SpawnEntity(CrateId, new EntityCoordinates(result.Grid, 0.5f, 0.5f));
                entMan.DeleteEntity(gone);
                var ev = new GridRestoredEvent(result.Grid);

                Assert.Multiple(() =>
                {
                    Assert.That(entMan.GetComponent<DrydockTradeCrateProbeComponent>(crate).RaiseTwice, Is.True, "The control: the probe asked for the second raise.");
                    Assert.That(state.Destination, Is.EqualTo(markers["CargoB"]));
                    Assert.That(state.Icon, Is.EqualTo("CargoB"));
                    Assert.That(state.Label, Is.EqualTo("Depot B"));
                    Assert.That(entMan.GetComponents(crate).Select(c => c.GetType().Name).OrderBy(n => n), Is.EqualTo(componentsBefore), "The handler adds no component.");
                    Assert.That(() => entMan.EventBus.RaiseLocalEvent(gone, ref ev), Throws.Nothing, "A raise at a crate that is gone is not fatal.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Test 7: the store only reads: no component of a bound crate is dirtied by it. Control: the key was carried.</summary>
        [Test]
        public async Task TheStoreDirtiesNothingOnACrate()
        {
            var rig = await Start();
            var (pair, entMan, map) = (rig.Pair, rig.EntMan, rig.Map);
            var timing = pair.Server.ResolveDependency<IGameTiming>();

            EntityUid crate = default;
            await pair.Server.WaitPost(() =>
            {
                var markers = PlaceDestinations(entMan, map.MapId);
                crate = PlaceCrate(entMan, map.Grid.Owner, markers["CargoB"]);
            });

            await pair.RunTicksSync(5);

            await pair.Server.WaitAssertion(() =>
            {
                var before = entMan.GetComponents(crate).ToDictionary(c => c.GetType().Name, c => c.LastModifiedTick);
                var stored = rig.Image.Store(map.Grid.Owner);
                var changed = entMan.GetComponents(crate)
                    .Where(c => !before.TryGetValue(c.GetType().Name, out var tick) || tick != c.LastModifiedTick)
                    .Select(c => c.GetType().Name)
                    .ToList();

                Assert.Multiple(() =>
                {
                    Assert.That(CarriesKey(stored.Image), Is.True, "The control: the destination was carried.");
                    Assert.That(before.Values.Max(), Is.LessThan(timing.CurTick), "The control: nothing was dirtied this tick before the store.");
                    Assert.That(changed, Is.Empty, "The store dirtied nothing.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }

    /// <summary>
    /// The test's own marker on a crate: asks the recorder for a second directed raise. Registered because the integration
    /// test assembly is a content assembly (PoolManager.cs:101).
    /// </summary>
    [RegisterComponent]
    public sealed partial class DrydockTradeCrateProbeComponent : Component
    {
        [Robust.Shared.Serialization.Manager.Attributes.DataField]
        public bool RaiseTwice;
    }
}
