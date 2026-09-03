using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.BUI;

[NetSerializable, Serializable]
public sealed class ShipyardConsoleInterfaceState : BoundUserInterfaceState
{
    public int Balance;
    public readonly bool AccessGranted;
    public readonly string? ShipDeedTitle;
    public int ShipSellValue;
    public readonly bool IsTargetIdPresent;
    public readonly byte UiKey;

    public readonly (List<string> available, List<string> unavailable) ShipyardPrototypes;
    public readonly string ShipyardName;
    public readonly bool FreeListings;
    public readonly float SellRate;
    public readonly List<StoredShipInfo> StoredShips; // Triad: drydock tab

    /// <summary>
    /// Triad: whether the drydock tab is offered at all. The master switch is server-only, so the
    /// client cannot read it and has to be told; without this the console would show a tab whose
    /// every button comes back refused.
    /// </summary>
    public readonly bool DrydockEnabled;

    /// <summary>Triad: the operator's berths, occupants included.</summary>
    public readonly List<DrydockBerthInfo> Berths;

    /// <summary>Triad: berth purchase price per size class name, for the buy control.</summary>
    public readonly Dictionary<string, int> BerthPrices;

    /// <summary>Triad: the transfer waiting at this console, if any.</summary>
    public readonly DrydockTransferOfferInfo? TransferOffer;

    /// <summary>
    /// Triad: the account that owns the ship on the inserted card's deed, or null when the card
    /// carries no deed to a live ship. The client compares it with its own account and covers the
    /// drydock tab with the lockout when they differ. Presentation only: the server refuses every
    /// message the lockout hides, and the id is already networked on the ship's ownership component.
    /// </summary>
    public readonly Guid? DeedOwnerUserId;

    public ShipyardConsoleInterfaceState(
        int balance,
        bool accessGranted,
        string? shipDeedTitle,
        int shipSellValue,
        bool isTargetIdPresent,
        byte uiKey,
        (List<string> available, List<string> unavailable) shipyardPrototypes,
        string shipyardName,
        bool freeListings,
        float sellRate,
        List<StoredShipInfo> storedShips, // Triad: drydock tab
        bool drydockEnabled, // Triad: drydock tab
        List<DrydockBerthInfo> berths, // Triad: drydock tab
        Dictionary<string, int> berthPrices, // Triad: drydock tab
        DrydockTransferOfferInfo? transferOffer, // Triad: drydock tab
        Guid? deedOwnerUserId) // Triad: drydock tab
    {
        Berths = berths; // Triad: drydock tab
        BerthPrices = berthPrices; // Triad: drydock tab
        TransferOffer = transferOffer; // Triad: drydock tab
        DeedOwnerUserId = deedOwnerUserId; // Triad: drydock tab
        Balance = balance;
        AccessGranted = accessGranted;
        ShipDeedTitle = shipDeedTitle;
        ShipSellValue = shipSellValue;
        IsTargetIdPresent = isTargetIdPresent;
        UiKey = uiKey;
        ShipyardPrototypes = shipyardPrototypes;
        ShipyardName = shipyardName;
        FreeListings = freeListings;
        SellRate = sellRate;
        StoredShips = storedShips; // Triad: drydock tab
        DrydockEnabled = drydockEnabled; // Triad: drydock tab
    }
}
