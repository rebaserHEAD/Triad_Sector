using Robust.Shared.Serialization;

namespace Content.Shared._NF.Market.BUI;

[NetSerializable, Serializable]
public sealed class MarketConsoleInterfaceState : BoundUserInterfaceState
{
    /// <summary>
    /// The player's balance
    /// </summary>
    public int Balance;

    /// <summary>
    /// The market modifier to apply on to the price.
    /// 0.1 makes prices 10% of their original value.
    /// </summary>
    public float MarketModifier;

    /// <summary>
    /// Data to display
    /// </summary>
    public List<MarketData> MarketDataList;

    /// <summary>
    /// The currently stored cart data
    /// </summary>
    public List<MarketData> CartDataList;

    /// <summary>
    /// The sum of the current cart
    /// </summary>
    public int CartBalance;

    /// <summary>
    /// are the buttons enabled
    /// </summary>
    public bool Enabled;

    /// <summary>
    /// The cost of one transaction.
    /// </summary>
    public int TransactionCost;

    /// <summary>
    /// The total amount of entities in the cart.
    /// </summary>
    public int CartEntities;

    // Triad: begin, multi-crate dispensing
    /// <summary>
    /// Entity slots one dispensed crate holds.
    /// </summary>
    public int CrateCapacity;

    /// <summary>
    /// The cart ceiling in entity slots (several crates' worth).
    /// </summary>
    public int MaxCartEntities;
    // Triad: end

    public MarketConsoleInterfaceState(int balance, float marketModifier, List<MarketData> marketDataList, List<MarketData> cartDataList, int cartBalance, bool enabled, int transactionCost, int cartEntities, int crateCapacity, int maxCartEntities) // Triad: add crateCapacity, maxCartEntities
    {
        Balance = balance;
        MarketModifier = marketModifier;
        MarketDataList = marketDataList;
        CartDataList = cartDataList;
        CartBalance = cartBalance;
        Enabled = enabled;
        TransactionCost = transactionCost;
        CartEntities = cartEntities;
        CrateCapacity = crateCapacity; // Triad
        MaxCartEntities = maxCartEntities; // Triad
    }
}
