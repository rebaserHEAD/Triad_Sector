using Content.Server._Triad.Drydock.Loader;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;

namespace Content.Server._Triad.Weapons;

/// <summary>
/// Computes a restored gun's modified members. A gun's nine <c>*Modified</c> members are networked view-only fields, so they
/// are not in the image and restore at their initializers, and the one place a gun computes them on load is map init
/// (<c>SharedGunSystem.OnMapInit</c>, which does nothing else), which a restore does not raise. <c>FireRateModified</c>
/// restores 0 and the shoot gate returns while it is not above 0, so a restored gun does not fire; a ship turret never wields,
/// equips or toggles, so nothing else would ever refresh it.
///
/// <para>Runs on the directed <see cref="GridRestoredEvent"/>, after every entity has started, because the modifier
/// subscribers read other components (<c>Wieldable.Wielded</c>, <c>ItemToggle.Activated</c>). It calls the owner's public
/// <see cref="SharedGunSystem.RefreshModifiers"/> with no user, so the holder-directed raise is skipped: a gun on a restored ship
/// is held by nobody, and <c>Holder</c> restores null. It adds no component, and running it twice writes the same nine values.
/// The directed raise skips an entity that is gone, so nothing here checks for one.</para>
/// </summary>
public sealed class GunRestoreSystem : EntitySystem
{
    [Dependency] private SharedGunSystem _gun = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GunComponent, GridRestoredEvent>(OnGridRestored);
    }

    private void OnGridRestored(Entity<GunComponent> gun, ref GridRestoredEvent args)
    {
        _gun.RefreshModifiers((gun, gun));
    }
}
