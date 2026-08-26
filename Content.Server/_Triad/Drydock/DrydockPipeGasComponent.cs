using Content.Shared.Atmos;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// A copying sidecar carrying one pipe's share of its net's gas across a store and retrieve. A
/// pipe net's air lives on the node-group object graph rather than on any entity, so the serializer
/// never sees it and a stored ship would come back with empty pipes.
///
/// <para>Distributed by pipe volume at store, merged back and removed on the first node-group
/// rebuild the reloaded grid sees. The component's own presence is the marker.</para>
/// </summary>
[RegisterComponent]
public sealed partial class DrydockPipeGasComponent : Component
{
    [DataField]
    public GasMixture GasMixture = new();
}
