#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Power.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// H11: a restored machine has its part-derived ratings again. Twenty-four systems compute a rating from a machine's parts
    /// on <c>RefreshPartsEvent</c>, and the event is raised only by map init and by the part exchanger, so a restore leaves
    /// each derived member at its initializer (a biomass reclaimer yields nothing; a gyroscope draws nothing). The handler
    /// raises it once per machine after every entity has started, by the owner's public <c>RefreshParts</c>.
    ///
    /// <para>One machine for each join row that is observable here, each compared member by member with its value before the
    /// store: the derived member of a biomass reclaimer (rows 130, 131), a cloning pod (157), a deep fryer (193), a gas recycler
    /// (247, 248), a medical scanner (306), a microwave (311), a seed extractor (373) and the load a gyroscope's power draw
    /// writes onto its receiver (the manifest's <c>BaseLoad</c>, rows 423-425, is in place before it).</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(Content.Server.Construction.ConstructionSystem))]
    public sealed class DrydockMachinePartsRestoreTest
    {
        private static readonly (string Prototype, Func<IEntityManager, EntityUid, object?[]> Derived)[] Machines =
        {
            ("BiomassReclaimer", (e, u) =>
            {
                var c = e.GetComponent<Content.Server.Medical.BiomassReclaimer.BiomassReclaimerComponent>(u);
                return new object?[] { c.YieldPerUnitMass, c.ProcessingTimePerUnitMass };
            }),
            ("CloningPod", (e, u) =>
            {
                var c = e.GetComponent<Content.Shared.Cloning.CloningPodComponent>(u);
                return new object?[] { c.BiomassRequirementMultiplier, c.CloningTime };
            }),
            ("KitchenDeepFryer", (e, u) => new object?[] { e.GetComponent<Content.Server.Nyanotrasen.Kitchen.Components.DeepFryerComponent>(u).StorageMaxEntities }),
            ("GasRecycler", (e, u) =>
            {
                var c = e.GetComponent<Content.Server.Atmos.Piping.Binary.Components.GasRecyclerComponent>(u);
                return new object?[] { c.MinTemp, c.MinPressure };
            }),
            ("MedicalScanner", (e, u) => new object?[] { e.GetComponent<Content.Server.Medical.Components.MedicalScannerComponent>(u).CloningFailChanceMultiplier }),
            ("KitchenMicrowave", (e, u) => new object?[] { e.GetComponent<Content.Server.Kitchen.Components.MicrowaveComponent>(u).FinalCookTimeMultiplier }),
            ("SeedExtractor", (e, u) => new object?[] { e.GetComponent<Content.Server.Botany.Components.SeedExtractorComponent>(u).SeedAmountMultiplier }),
            ("Gyroscope", (e, u) => new object?[] { e.GetComponent<ApcPowerReceiverComponent>(u).Load }),
        };

        [Test]
        public async Task ARestoredMachineHasItsPartDerivedRatingsAgain()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();

            var grid = map.Grid.Owner;
            var before = new Dictionary<string, object?[]>();
            await server.WaitPost(() =>
            {
                var maps = server.System<SharedMapSystem>();
                var gridComp = entMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid);
                for (var i = 1; i < Machines.Length; i++)
                    maps.SetTile(grid, gridComp, new Vector2i(i, 0), map.Tile.Tile);

                for (var i = 0; i < Machines.Length; i++)
                {
                    var (prototype, derived) = Machines[i];
                    var uid = entMan.SpawnEntity(prototype, new EntityCoordinates(grid, i + 0.5f, 0.5f));
                    before[prototype] = derived(entMan, uid);
                }
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
                Assert.Multiple(() =>
                {
                    foreach (var (prototype, derived) in Machines)
                    {
                        var uid = fidelity.GridTreeList(result.Grid).Single(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == prototype);
                        Assert.That(derived(entMan, uid), Is.EqualTo(before[prototype]), $"{prototype}'s derived members have to be what they were before the store.");
                    }
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The restore handler contract: a second raise leaves every derived member as it was (each subscriber computes from the
        /// base and the parts, not from its own last output), no component is added, and a raise at a gone entity is not fatal.
        /// </summary>
        [Test]
        public async Task ASecondRestoreRaiseChangesNothingAddsNoComponentAndAGoneEntityIsTolerated()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var grid = map.Grid.Owner;

            await server.WaitPost(() =>
            {
                var maps = server.System<SharedMapSystem>();
                var gridComp = entMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid);
                for (var i = 1; i < Machines.Length; i++)
                    maps.SetTile(grid, gridComp, new Vector2i(i, 0), map.Tile.Tile);

                var spawned = new List<(EntityUid Uid, Func<IEntityManager, EntityUid, object?[]> Derived, object?[] Before, List<string> Components)>();
                for (var i = 0; i < Machines.Length; i++)
                {
                    var (prototype, derived) = Machines[i];
                    var uid = entMan.SpawnEntity(prototype, new EntityCoordinates(grid, i + 0.5f, 0.5f));
                    spawned.Add((uid, derived, derived(entMan, uid), entMan.GetComponents(uid).Select(c => c.GetType().Name).OrderBy(n => n).ToList()));
                }

                var gone = entMan.SpawnEntity("Gyroscope", new EntityCoordinates(grid, 0.5f, 0.5f));
                entMan.DeleteEntity(gone);

                var ev = new GridRestoredEvent(grid);
                foreach (var machine in spawned)
                {
                    entMan.EventBus.RaiseLocalEvent(machine.Uid, ref ev);
                    entMan.EventBus.RaiseLocalEvent(machine.Uid, ref ev);
                }

                Assert.Multiple(() =>
                {
                    foreach (var machine in spawned)
                    {
                        Assert.That(machine.Derived(entMan, machine.Uid), Is.EqualTo(machine.Before), $"{entMan.ToPrettyString(machine.Uid)}: a second refresh changes nothing.");
                        Assert.That(entMan.GetComponents(machine.Uid).Select(c => c.GetType().Name).OrderBy(n => n), Is.EqualTo(machine.Components), "The handler adds no component.");
                    }

                    Assert.That(() => entMan.EventBus.RaiseLocalEvent(gone, ref ev), Throws.Nothing, "A raise at an entity that is gone is not fatal.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}
