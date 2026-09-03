// Triad: drydock tab. The berth and transfer messages, beside the store and retrieve ones.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>Buy a berth of this size class for the operator's account.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleBuyBerthMessage : BoundUserInterfaceMessage
{
    public readonly string SizeClass;

    public ShipyardConsoleBuyBerthMessage(string sizeClass)
    {
        SizeClass = sizeClass;
    }
}

/// <summary>Sell one of the operator's empty berths. The server checks it is theirs and empty.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleSellBerthMessage : BoundUserInterfaceMessage
{
    public readonly int BerthId;

    public ShipyardConsoleSellBerthMessage(int berthId)
    {
        BerthId = berthId;
    }
}

/// <summary>Raise one of the operator's berths to the next class up, paying the difference.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleUpgradeBerthMessage : BoundUserInterfaceMessage
{
    public readonly int BerthId;

    public ShipyardConsoleUpgradeBerthMessage(int berthId)
    {
        BerthId = berthId;
    }
}

/// <summary>
/// Offer one of the operator's stored ships to whoever accepts next at this console. The server
/// verifies the offering session owns the row, not the card in the slot: cards get lent and lost.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleOfferTransferMessage : BoundUserInterfaceMessage
{
    public readonly Guid ShipId;

    public ShipyardConsoleOfferTransferMessage(Guid shipId)
    {
        ShipId = shipId;
    }
}

[Serializable, NetSerializable]
public sealed class ShipyardConsoleCancelTransferMessage : BoundUserInterfaceMessage
{
}

/// <summary>
/// Accept the pending offer at this console into the accepting account's own drydock. The
/// accepting account must not be the offering one and must have a free berth the hull fits. The
/// account is the session's, never the character's mind: a reprinted body still owns its ships.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleAcceptTransferMessage : BoundUserInterfaceMessage
{
}
