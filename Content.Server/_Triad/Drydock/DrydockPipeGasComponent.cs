using Content.Shared.Atmos;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// A copying sidecar carrying each of an entity's pipe nodes' share of its net's gas across a store
/// and retrieve. A pipe net's air lives on the node-group object graph rather than on any entity,
/// so the serializer never sees it and a stored ship would come back with empty pipes.
///
/// <para>Distributed by pipe volume at store, merged back and removed on the first node-group
/// rebuild the reloaded grid sees. The component's own presence is the marker.</para>
///
/// <para>Keyed by node name, because a pump, a mixer or a crystallizer has two or three nodes in
/// as many different nets. The first draft held one mixture per entity, so the last net written
/// won and the restore merged it into every node: gas crossed from a pump's inlet to its outlet,
/// a mixer's two feeds leaked into each other, and a crystallizer's inlet dumped into its hot
/// regulator loop (test server, 2026-09-06: "pumps will backpressure their contents on ship
/// save", "the N2 leaked into the O2").</para>
/// </summary>
[RegisterComponent]
public sealed partial class DrydockPipeGasComponent : Component
{
    [DataField]
    public Dictionary<string, GasMixture> Shares = new();
}
