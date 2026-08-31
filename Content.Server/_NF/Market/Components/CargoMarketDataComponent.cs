using Content.Server._NF.Market.Systems;
using Content.Shared._NF.Market;
using Content.Shared.Whitelist;

namespace Content.Server._NF.Market.Components;

/// <summary>
/// Component that is put on the console's grid that will hold all things that are sold at cargo, for that grid.
/// </summary>
[RegisterComponent]
[Access(typeof(MarketSystem))]
public sealed partial class CargoMarketDataComponent : Component
{
    [DataField]
    public List<MarketData> MarketDataList = [];

    /// <summary>
    /// Sold items must match this whitelist to enter into this data set.
    /// </summary>
    [DataField]
    public EntityWhitelist? Whitelist;

    /// <summary>
    /// Sold items not must match this blacklist to enter into this data set.
    /// </summary>
    [DataField]
    public EntityWhitelist? Blacklist;

    /// <summary>
    /// Particular items that may override the blacklist.
    /// </summary>
    [DataField]
    public EntityWhitelist? WhitelistOverride;

    // Triad: begin, persistent inventory
    /// <summary>
    /// Which persistent shelf this market belongs to, keyed into the market_inventory table.
    /// Null (the default) keeps round-local behavior: the inventory dies with the round.
    /// Set through a station prototype (MarketFrontierOutpostPersistent sets TradeMall), and
    /// only honored while triad.market.persist_inventory is on.
    /// </summary>
    [DataField]
    public string? PersistKey;

    /// <summary>
    /// Loose-unit pools fed by shredding, keyed <c>material:Steel</c> / <c>reagent:X</c> /
    /// <c>gas:X</c>, values in x100 fixed-point (centiunits / centimoles). Whenever a pool covers
    /// a full standard container or stack, that much converts into a real listing on
    /// <see cref="MarketDataList"/>; the remainder stays here, so nothing is ever lost. Persisted
    /// alongside the listings under the same key.
    /// </summary>
    [ViewVariables]
    public Dictionary<string, long> Pools = new();
    // Triad: end
}
