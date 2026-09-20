#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Shared.Interaction;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// H22: a thruster its owner had disabled comes back drawing what it drew. The owner's disable branch writes the receiver's
    /// <c>Load</c> to 1 (<c>ThrusterSystem.cs:79-82</c>, <c>:178-181</c>); <c>Load</c> is a data field, so the 1 is stored. H11's
    /// <c>RefreshParts</c> then writes the machine's full draw whatever the thruster's state (<c>UpgradePowerSystem.OnRefreshParts</c>),
    /// which would lift a disabled gyroscope to its full load while it shows off. The handler puts back what the owner's own
    /// disable branch leaves, under the owner's guard, and touches nothing else.
    ///
    /// <para>The last assertion is row 418's proof, not H22's: <c>OriginalLoad</c> (what an enable writes back onto <c>Load</c>)
    /// is a manifest member applied before init, and has to come back as it was, or the thruster runs on one watt for good.
    /// An enable itself needs power (<c>CanEnable</c>), which this grid does not have, so the member is asserted directly.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(ThrusterComponent))]
    public sealed class DrydockThrusterLoadRestoreTest
    {
        [Test]
        public async Task ADisabledGyroscopeRestoresAtOneWattAnEnabledOneKeepsItsDrawAndOriginalLoadComesBack()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();

            var grid = map.Grid.Owner;
            float enabledLoadBefore = 0, originalLoadBefore = 0, loadDisabledBefore = 0;
            var disabledBefore = false;
            await server.WaitPost(() =>
            {
                var maps = server.System<SharedMapSystem>();
                maps.SetTile(grid, entMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid), new Vector2i(1, 0), map.Tile.Tile);
                var user = entMan.SpawnEntity(null, new EntityCoordinates(grid, 0.5f, 1.5f));

                var enabled = entMan.SpawnEntity("Gyroscope", new EntityCoordinates(grid, 0.5f, 0.5f));
                var disabled = entMan.SpawnEntity("Gyroscope", new EntityCoordinates(grid, 1.5f, 0.5f));
                enabledLoadBefore = entMan.GetComponent<ApcPowerReceiverComponent>(enabled).Load;

                // The player's toggle, which is the owner's own disable branch.
                entMan.EventBus.RaiseLocalEvent(disabled, new ActivateInWorldEvent(user, disabled, true));
                disabledBefore = !entMan.GetComponent<ThrusterComponent>(disabled).Enabled;
                loadDisabledBefore = entMan.GetComponent<ApcPowerReceiverComponent>(disabled).Load;
                originalLoadBefore = entMan.GetComponent<ThrusterComponent>(disabled).OriginalLoad;
            });

            Assert.Multiple(() =>
            {
                Assert.That(disabledBefore, Is.True, "The control: the toggle has to have disabled the gyroscope.");
                Assert.That(loadDisabledBefore, Is.EqualTo(1f), "The control: the owner's disable branch leaves a disabled thruster at one watt.");
                Assert.That(originalLoadBefore, Is.GreaterThan(1f), "The control: and remembers the draw an enable puts back.");
                Assert.That(enabledLoadBefore, Is.GreaterThan(1f), "The control: an enabled one draws its full load.");
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
                var gyroscopes = fidelity.GridTreeList(result.Grid)
                    .Where(uid => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "Gyroscope").ToList();
                var disabledAfter = gyroscopes.Single(uid => !entMan.GetComponent<ThrusterComponent>(uid).Enabled);
                var enabledAfter = gyroscopes.Single(uid => entMan.GetComponent<ThrusterComponent>(uid).Enabled);

                Assert.Multiple(() =>
                {
                    Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(disabledAfter).Load, Is.EqualTo(1f),
                        "A disabled gyroscope has to come back at one watt, not at the full draw H11's refresh writes.");
                    Assert.That(entMan.GetComponent<ThrusterComponent>(disabledAfter).OriginalLoad, Is.EqualTo(originalLoadBefore),
                        "OriginalLoad (row 418) has to come back as it was, so an enable puts back the real draw.");
                    Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(enabledAfter).Load, Is.EqualTo(enabledLoadBefore),
                        "An enabled gyroscope keeps its full draw: the handler does nothing for it.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The restore handler contract: a second raise leaves the load where it was, no component is added, and a raise at an
        /// entity that is gone is not fatal.
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
                var user = entMan.SpawnEntity(null, new EntityCoordinates(grid, 0.5f, 1.5f));
                var gyroscope = entMan.SpawnEntity("Gyroscope", new EntityCoordinates(grid, 0.5f, 0.5f));
                var gone = entMan.SpawnEntity("Gyroscope", new EntityCoordinates(grid, 0.5f, 0.5f));
                entMan.EventBus.RaiseLocalEvent(gyroscope, new ActivateInWorldEvent(user, gyroscope, true));
                var componentsBefore = entMan.GetComponents(gyroscope).Select(c => c.GetType().Name).OrderBy(n => n).ToList();
                entMan.DeleteEntity(gone);

                var ev = new GridRestoredEvent(grid);
                entMan.EventBus.RaiseLocalEvent(gyroscope, ref ev);
                var afterFirst = entMan.GetComponent<ApcPowerReceiverComponent>(gyroscope).Load;
                entMan.EventBus.RaiseLocalEvent(gyroscope, ref ev);

                Assert.Multiple(() =>
                {
                    Assert.That(afterFirst, Is.EqualTo(1f), "A disabled gyroscope ends the raise at one watt, whatever the machine refresh wrote first.");
                    Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(gyroscope).Load, Is.EqualTo(afterFirst), "A second raise changes nothing.");
                    Assert.That(entMan.GetComponents(gyroscope).Select(c => c.GetType().Name).OrderBy(n => n), Is.EqualTo(componentsBefore), "The handler adds no component.");
                    Assert.That(() => entMan.EventBus.RaiseLocalEvent(gone, ref ev), Throws.Nothing, "A raise at an entity that is gone is not fatal.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}
