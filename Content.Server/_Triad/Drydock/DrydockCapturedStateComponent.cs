namespace Content.Server._Triad.Drydock;

/// <summary>
/// The clearing sidecar: rides an entity through the grid serialize and reload carrying the
/// component and field state the engine serializer cannot write. Populated at store, after which
/// the original fields are cleared so the serializer does not choke on them, and consumed and
/// removed at retrieve.
///
/// <para>It lives on the entity rather than beside it, which is the whole trick: the serializer
/// preserves a component alongside its own entity, so nothing has to correlate ids across a round
/// trip. On load each entity already carries its own captured state.</para>
///
/// <para>Keyed by <c>ComponentTypeName|FieldName</c>, valued as base64 of YAML because YAML nested
/// inside YAML is an escaping problem nobody needs. The key form is the drift surface: a C# rename
/// on either side orphans a key, which is why restore counts and names its misses rather than
/// skipping them quietly.</para>
/// </summary>
[RegisterComponent]
public sealed partial class DrydockCapturedStateComponent : Component
{
    [DataField]
    public Dictionary<string, string> Fields = new();
}
