using Content.Shared.CartridgeLoader;
using Robust.Shared.GameStates;

namespace Content.Shared._Triad.CartridgeLoader;

/// <summary>
/// The latest UI state a PDA program published, kept on the program entity so every program replicates its own
/// state. The loader's BUI state slot holds one value for the whole PDA and cannot carry more than one writer.
/// </summary>
/// <remarks>
/// Sent only to players who have the loader's UI open, see the get-state attempt handler in
/// CartridgeLoaderSystem. Only the server writes it. A fragment must not change a state it is fed: the client keeps
/// the object and feeds it to every fragment it rebuilds.
/// </remarks>
[RegisterComponent, NetworkedComponent, UnsavedComponent]
[AutoGenerateComponentState(true)]
[Access(typeof(CartridgeLoaderSystem), Other = AccessPermissions.Read)]
public sealed partial class CartridgeUiStateComponent : Component
{
    /// <summary>
    /// Null until the program publishes, and again once the state is cleared.
    /// </summary>
    [AutoNetworkedField]
    public BoundUserInterfaceState? State;

    public override bool SessionSpecific => true;
}
