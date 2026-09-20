using Content.Server._Triad.Drydock.Loader;
using Content.Shared.SmartFridge;

namespace Content.Server._Triad.SmartFridge;

/// <summary>
/// Rebuilds a restored smart fridge's stock index. <see cref="SmartFridgeComponent.ContainedEntries"/> is a networked index
/// over the fridge's container and no longer a data field (a <c>SmartFridgeEntry</c> cannot be a YAML mapping key), and only
/// map init rebuilt it (<c>SharedSmartFridgeSystem.OnMapInit</c>, which does nothing else), which a restore does not raise. So
/// a stocked fridge would report itself empty and its stock be unreachable through the UI.
///
/// <para>Runs on the directed <see cref="GridRestoredEvent"/>, after every entity has started, because a key is the contained
/// entity's identity name, which includes the modifiers applied at startup: a key built earlier would not match one a live
/// insert builds. It calls the owner's public <see cref="SharedSmartFridgeSystem.RebuildEntries"/>, which clears and refills
/// the index from the container and tops up the menu (<c>Entries</c>, a data field, kept as stored) with a name only when a
/// stocked item lacks one, which a live insert always added. It adds no component, and running it twice leaves the same index.
/// The directed raise skips an entity that is gone, so nothing here checks for one.</para>
/// </summary>
public sealed class SmartFridgeRestoreSystem : EntitySystem
{
    [Dependency] private SharedSmartFridgeSystem _fridge = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SmartFridgeComponent, GridRestoredEvent>(OnGridRestored);
    }

    private void OnGridRestored(Entity<SmartFridgeComponent> fridge, ref GridRestoredEvent args)
    {
        _fridge.RebuildEntries(fridge);
    }
}
