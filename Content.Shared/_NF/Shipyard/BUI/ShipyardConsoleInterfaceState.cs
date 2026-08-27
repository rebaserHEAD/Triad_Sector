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
        bool drydockEnabled) // Triad: drydock tab
    {
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
