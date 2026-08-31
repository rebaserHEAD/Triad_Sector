using Robust.Shared.GameStates;

namespace Content.Shared._NF.Market.Components;

[RegisterComponent]
[NetworkedComponent]
public sealed partial class MarketItemSpawnerComponent : Component
{

    [NonSerialized]
    public List<MarketData> ItemsToSpawn = [];

    // Triad: begin, multi-crate dispensing
    /// <summary>
    /// Paid-for chunks still waiting for a crate. When the machine goes unoccupied and this is
    /// non-empty, the next chunk moves to <see cref="ItemsToSpawn"/> and the machine opens again.
    /// </summary>
    [NonSerialized]
    public List<List<MarketData>> PendingChunks = [];
    // Triad: end
}
