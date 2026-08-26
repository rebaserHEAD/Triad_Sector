using Content.Server.Atmos.EntitySystems;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.NodeContainer;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Merges pipe-gas sidecars back into their nets on the first node-group rebuild, then removes
/// them.
///
/// <para>The sidecar's presence is the whole apply condition, which avoids assuming anything about
/// when the first rebuild fires relative to load. Removing it immediately is what stops a player
/// cutting a pipe later from re-firing the merge and duplicating the gas.</para>
/// </summary>
public sealed class DrydockPipeGasRestoreSystem : EntitySystem
{
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<DrydockPipeGasComponent, NodeGroupsRebuilt>(OnNodeGroupsRebuilt);
    }

    private void OnNodeGroupsRebuilt(Entity<DrydockPipeGasComponent> ent, ref NodeGroupsRebuilt args)
    {
        if (!TryComp<NodeContainerComponent>(ent, out var nodeContainer))
        {
            RemComp<DrydockPipeGasComponent>(ent);
            return;
        }

        foreach (var node in nodeContainer.Nodes.Values)
        {
            if (node is not PipeNode { NodeGroup: not null } pipe)
                continue;

            // Merge rather than overwrite, so a net whose members each carry a share sums back to
            // what it held instead of the last one clobbering the rest.
            _atmosphere.Merge(pipe.Air, ent.Comp.GasMixture);
        }

        RemComp<DrydockPipeGasComponent>(ent);
    }
}
