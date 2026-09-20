using Content.Server._Triad.Drydock.Loader;
using Content.Server.Construction;
using Content.Server.Construction.Components;

namespace Content.Server._Triad.Construction;

/// <summary>
/// Computes a restored machine's part-derived ratings. Twenty-four systems derive a rating from a machine's parts when
/// <c>RefreshPartsEvent</c> is raised, and it is raised only by map init (<c>ConstructionSystem.OnMachineMapInit</c>, after it
/// stocks the machine) and by the part exchanger, so a restore leaves each derived member at its initializer: a biomass
/// reclaimer yields nothing, a gyroscope draws nothing, a seed extractor gives no seeds. The subscribers were read one by one
/// (<c>resources/2026-09-19-h11/refreshparts-subscribers.md</c>): each computes from its base and the parts, so a second run
/// changes nothing.
///
/// <para>Runs on the directed <see cref="GridRestoredEvent"/>, after every entity has started: after the item-slot passes (a
/// dispenser adds storage slots from its part count and is idempotent against slots the seam restored), after the manifest's
/// before-init members (the three power members' base values, which they scale by), and before the first power solve. It
/// calls only the owner's public <see cref="ConstructionSystem.RefreshParts"/>, not the stocking of a board and parts that
/// map init does first, which would spawn. It adds no component, and the directed raise skips an entity that is gone.</para>
///
/// <para>What it does to a disabled thruster's power load is <c>ThrusterRestoreSystem</c>'s to undo, and it is ordered after
/// this system.</para>
/// </summary>
public sealed class MachinePartsRestoreSystem : EntitySystem
{
    [Dependency] private ConstructionSystem _construction = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MachineComponent, GridRestoredEvent>(OnGridRestored);
    }

    private void OnGridRestored(Entity<MachineComponent> machine, ref GridRestoredEvent args)
    {
        _construction.RefreshParts(machine, machine.Comp);
    }
}
