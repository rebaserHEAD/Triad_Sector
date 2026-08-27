// Triad: drydock tab.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>
/// Store the ship whose deed is on the inserted ID card.
///
/// <para>Deliberately carries nothing. The ship is resolved server-side from the card in the
/// console's slot, so there is no ship handle for a client to substitute; a player who wants to
/// store a different ship has to physically put its deed in the slot.</para>
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleStoreMessage : BoundUserInterfaceMessage
{
}
