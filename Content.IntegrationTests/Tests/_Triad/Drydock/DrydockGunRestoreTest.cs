#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// H02: a restored gun fires again. The nine <c>*Modified</c> members a gun fires from are networked view-only fields, so
    /// they are not in the image and restore at their initializers, and a restore raises no map init, the one place a gun
    /// computes them on load. <c>FireRateModified</c> restores 0, and the shoot gate (<c>SharedGunSystem.cs:359-362</c>) returns
    /// while it is not above 0. A ship turret never wields, equips or toggles, so nothing else would ever refresh it.
    ///
    /// <para>Two guns are used. <c>WeaponTurretTDF</c> is a Triad ship turret, powered by its own battery and needing no
    /// grid power, which is the case nothing else refreshes. <c>WeaponRiflePike</c> sets its own burst and camera recoil, so its
    /// modified members differ from the defaults a wrong restore would leave.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(GunComponent))]
    public sealed class DrydockGunRestoreTest
    {
        private static readonly string[] Guns = { "WeaponTurretTDF", "WeaponRiflePike" };

        /// <summary>A sound specifier by value: the specifier classes compare by reference, so the path or collection and the parameters stand for it.</summary>
        private static string? SoundKey(Robust.Shared.Audio.SoundSpecifier? sound) => sound switch
        {
            null => null,
            Robust.Shared.Audio.SoundPathSpecifier path => $"path {path.Path} {path.Params}",
            Robust.Shared.Audio.SoundCollectionSpecifier collection => $"collection {collection.Collection} {collection.Params}",
            _ => sound.ToString(),
        };

        /// <summary>The nine modified members, by name.</summary>
        private static Dictionary<string, object?> Modified(GunComponent gun) => new()
        {
            [nameof(GunComponent.SoundGunshotModified)] = SoundKey(gun.SoundGunshotModified),
            [nameof(GunComponent.CameraRecoilScalarModified)] = gun.CameraRecoilScalarModified,
            [nameof(GunComponent.AngleIncreaseModified)] = gun.AngleIncreaseModified,
            [nameof(GunComponent.AngleDecayModified)] = gun.AngleDecayModified,
            [nameof(GunComponent.MaxAngleModified)] = gun.MaxAngleModified,
            [nameof(GunComponent.MinAngleModified)] = gun.MinAngleModified,
            [nameof(GunComponent.ShotsPerBurstModified)] = gun.ShotsPerBurstModified,
            [nameof(GunComponent.FireRateModified)] = gun.FireRateModified,
            [nameof(GunComponent.ProjectileSpeedModified)] = gun.ProjectileSpeedModified,
        };

        [Test]
        public async Task ARestoredGunHasAllNineModifiedMembersAgainAndPassesTheShootGate()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var image = server.System<DrydockImageSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();

            var grid = map.Grid.Owner;
            var before = new Dictionary<string, Dictionary<string, object?>>();
            await server.WaitPost(() =>
            {
                server.System<SharedMapSystem>().SetTile(grid, entMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid), new Vector2i(1, 0), map.Tile.Tile);
                var x = 0.5f;
                foreach (var prototype in Guns)
                {
                    var gun = entMan.SpawnEntity(prototype, new EntityCoordinates(grid, x, 0.5f));
                    before[prototype] = Modified(entMan.GetComponent<GunComponent>(gun));
                    x += 1f;
                }
            });

            Assert.Multiple(() =>
            {
                foreach (var prototype in Guns)
                    Assert.That((float) before[prototype][nameof(GunComponent.FireRateModified)]!, Is.GreaterThan(0), $"The control: a spawned {prototype} has had its modifiers computed at map init.");
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
                    foreach (var prototype in Guns)
                    {
                        var uid = fidelity.GridTreeList(result.Grid).Single(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == prototype);
                        var after = Modified(entMan.GetComponent<GunComponent>(uid));

                        foreach (var (member, value) in before[prototype])
                            Assert.That(after[member], Is.EqualTo(value), $"{prototype}.{member} has to be what it was before the store.");

                        // The gate AttemptShoot uses (SharedGunSystem.cs:359-362): a gun with no fire rate does not fire.
                        Assert.That(entMan.GetComponent<GunComponent>(uid).FireRateModified, Is.GreaterThan(0), $"{prototype} has to pass the shoot gate again.");
                    }
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The restore handler contract: a second raise changes nothing (a second <c>RefreshModifiers</c> writes the same nine
        /// values), no component is added to the gun, and a raise at an entity that is gone is not fatal.
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
                var gun = entMan.SpawnEntity("WeaponRiflePike", new EntityCoordinates(grid, 0.5f, 0.5f));
                var gone = entMan.SpawnEntity("WeaponRiflePike", new EntityCoordinates(grid, 0.5f, 0.5f));
                var modifiedBefore = Modified(entMan.GetComponent<GunComponent>(gun));
                var componentsBefore = entMan.GetComponents(gun).Select(c => c.GetType().Name).OrderBy(n => n).ToList();
                entMan.DeleteEntity(gone);

                var ev = new GridRestoredEvent(grid);
                entMan.EventBus.RaiseLocalEvent(gun, ref ev);
                entMan.EventBus.RaiseLocalEvent(gun, ref ev);

                Assert.Multiple(() =>
                {
                    Assert.That(Modified(entMan.GetComponent<GunComponent>(gun)), Is.EqualTo(modifiedBefore), "A second refresh changes nothing.");
                    Assert.That(entMan.GetComponents(gun).Select(c => c.GetType().Name).OrderBy(n => n), Is.EqualTo(componentsBefore), "The handler adds no component.");
                    Assert.That(() => entMan.EventBus.RaiseLocalEvent(gone, ref ev), Throws.Nothing, "A raise at an entity that is gone is not fatal.");
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}
