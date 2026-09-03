// Triad: drydock tab. New files in the NF namespace because they are part of the shipyard console's
// existing interface state rather than a surface of their own.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.BUI;

/// <summary>One of the operator's berths as the drydock tab draws it: a slot, and what is in it.</summary>
[Serializable, NetSerializable]
public sealed class DrydockBerthInfo
{
    public int BerthId;

    /// <summary>The stored class text, shown as-is for the same reason the ship's is.</summary>
    public string MaxSizeClass = string.Empty;

    /// <summary>What selling this berth returns right now. Zero for a grant.</summary>
    public int SellValue;

    /// <summary>What raising this berth one class costs, or null when it is already the largest.</summary>
    public int? UpgradePrice;

    public string? UpgradeClass;

    public Guid? OccupantShipId;

    public string? OccupantName;

    /// <summary>The occupant's stored class text, so the row can say "Kestrel · Cutter".</summary>
    public string? OccupantSizeClass;

    /// <summary>The occupant's row state as text; only a Stored occupant offers Retrieve.</summary>
    public string? OccupantState;

    public DrydockBerthInfo(int berthId, string maxSizeClass, int sellValue, int? upgradePrice, string? upgradeClass,
        Guid? occupantShipId, string? occupantName, string? occupantSizeClass, string? occupantState)
    {
        BerthId = berthId;
        MaxSizeClass = maxSizeClass;
        SellValue = sellValue;
        UpgradePrice = upgradePrice;
        UpgradeClass = upgradeClass;
        OccupantShipId = occupantShipId;
        OccupantName = occupantName;
        OccupantSizeClass = occupantSizeClass;
        OccupantState = occupantState;
    }
}

/// <summary>
/// The ship on the inserted card's deed, as the card at the top of the tab draws it: the one ship
/// the operator has out, and where it can go. Null when the card carries no deed.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockDeedShipInfo
{
    public string Name = string.Empty;

    public string? SizeClass;

    /// <summary>Minutes since the ship was retrieved, or null for a hull that has never been stored.</summary>
    public int? MinutesOut;

    /// <summary>
    /// The berth a plain Store lands in: the ship's own last berth if it is free and fits, else
    /// the smallest free berth that fits. Null when nothing fits, which is what disables Store.
    /// </summary>
    public int? DefaultBerthId;

    /// <summary>Every free berth the hull fits, for the dropdown beside Store.</summary>
    public List<int> FittingBerthIds = new();

    public DrydockDeedShipInfo(string name, string? sizeClass, int? minutesOut, int? defaultBerthId, List<int> fittingBerthIds)
    {
        Name = name;
        SizeClass = sizeClass;
        MinutesOut = minutesOut;
        DefaultBerthId = defaultBerthId;
        FittingBerthIds = fittingBerthIds;
    }
}

/// <summary>
/// A transfer waiting at this console for someone to accept. Carries names, never ids of people,
/// because the client only needs to say who is offering what; the server holds the real offer.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockTransferOfferInfo
{
    public Guid ShipId;
    public string ShipName = string.Empty;
    public string? SizeClass;
    public string OfferedBy = string.Empty;

    /// <summary>
    /// The offering account, so the client can show Cancel to the offerer and Accept to everyone
    /// else. One console state is shared by every viewer, so this cannot be a per-viewer flag.
    /// Ownership ids are already networked on every deeded ship, so this exposes nothing new.
    /// </summary>
    public Guid OfferedByUserId;

    public int SecondsLeft;

    public DrydockTransferOfferInfo(Guid shipId, string shipName, string? sizeClass, string offeredBy, Guid offeredByUserId, int secondsLeft)
    {
        ShipId = shipId;
        ShipName = shipName;
        SizeClass = sizeClass;
        OfferedBy = offeredBy;
        OfferedByUserId = offeredByUserId;
        SecondsLeft = secondsLeft;
    }
}
