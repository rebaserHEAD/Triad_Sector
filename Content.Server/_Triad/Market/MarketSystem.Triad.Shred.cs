using Content.Server._NF.Market.Components;
using Content.Server._NF.Market.Extensions;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Cargo.Systems;
using Content.Shared.Atmos.Piping.Unary.Components;
using Content.Shared.Atmos.Prototypes;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Item;
using Content.Shared.Materials;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.Market.Systems;

// Triad: the shred engine. Anything sold at a market pallet that is neither whitelisted (lists
// as-is) nor on the hard-reject roster (cash-only) decomposes here: its price manifest becomes
// loose-unit pools and real listings, so the mall conserves everything sellable instead of letting
// the unlisted middle evaporate for cash. The seller was already paid the full appraisal by the
// sale itself; this only decides what enters inventory.
public sealed partial class MarketSystem
{
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;

    /// <summary>Reagent id -> the largest single-reagent portable container proto and its fill.</summary>
    private Dictionary<string, (string Proto, long FillCentiUnits)>? _containerByReagent;

    /// <summary>Gas id -> the largest single-gas canister proto and its fill in centimoles.</summary>
    private Dictionary<string, (string Proto, long FillCentiMoles)>? _canisterByGas;

    /// <summary>
    /// Decomposes one sold entity into the market: manifest rows become pools and listings.
    /// Contents are NOT walked here - the sold-entity walk already visits contained entities and
    /// runs each through its own disposition.
    /// </summary>
    private void ShredIntoMarket(CargoMarketDataComponent market, EntityUid sold, EntityUid grid)
    {
        var rows = new List<PriceContribution>();
        _pricingSystem.GetOwnPriceCollectingManifest(sold, grid, rows);

        foreach (var row in rows)
        {
            var sep = row.Key.IndexOf(':');
            if (sep <= 0)
                continue;
            var kind = row.Key[..sep];
            var id = row.Key[(sep + 1)..];

            switch (kind)
            {
                case "material":
                case "reagent":
                case "gas":
                    if (row.QuantityHundredths > 0)
                        CreditPool(market, row.Key, row.QuantityHundredths);
                    break;

                case "entity":
                    // Unspawned mag fill and the like: stateless spawnable prototypes, listed
                    // directly as loose items.
                    if (row.QuantityHundredths > 0 && _prototypeManager.HasIndex<EntityPrototype>(id))
                        market.MarketDataList.Upsert(id, (int)(row.QuantityHundredths / 100), row.UnitValueMinor / 100.0);
                    break;

                // authored:/unattributed: residue has no material mapping; the cash the seller was
                // paid is its only representation. The startup audit reports which prototypes fall
                // through this way, so the gap is a report, not a surprise.
            }
        }
    }

    /// <summary>
    /// Credits loose units to a pool, then converts whatever now covers full containers or stacks
    /// into real listings. The remainder stays pooled; nothing is lost.
    /// </summary>
    private void CreditPool(CargoMarketDataComponent market, string key, long centiUnits)
    {
        market.Pools.TryGetValue(key, out var pool);
        pool += centiUnits;
        market.Pools[key] = pool;
        ConvertPool(market, key);
    }

    /// <summary>
    /// Converts a pool's covered quanta into listings. Also called for pools loaded from the
    /// database, so a pool topped up over several rounds still converts.
    /// </summary>
    private void ConvertPool(CargoMarketDataComponent market, string key)
    {
        var sep = key.IndexOf(':');
        if (sep <= 0 || !market.Pools.TryGetValue(key, out var pool) || pool <= 0)
            return;
        var id = key[(sep + 1)..];

        switch (key[..sep])
        {
            case "material":
                ConvertMaterialPool(market, key, id, pool);
                break;
            case "reagent":
                ConvertReagentPool(market, key, id, pool);
                break;
            case "gas":
                ConvertGasPool(market, key, id, pool);
                break;
        }
    }

    private void ConvertMaterialPool(CargoMarketDataComponent market, string key, string materialId, long pool)
    {
        if (!_prototypeManager.TryIndex<MaterialPrototype>(materialId, out var material)
            || material.StackEntity is not { } stackEntity
            || !_prototypeManager.TryIndex<EntityPrototype>(stackEntity, out var entProto)
            || !entProto.TryComp<PhysicalCompositionComponent>(out var composition, Factory)
            || !composition.MaterialComposition.TryGetValue(materialId, out var perStack)
            || perStack <= 0)
            return;

        var perStackCenti = perStack * 100L;
        var count = pool / perStackCenti;
        if (count <= 0)
            return;

        string? stackProto = null;
        if (entProto.TryComp<StackComponent>(out var stack, Factory))
            stackProto = stack.StackTypeId;

        market.MarketDataList.Upsert(stackEntity, (int)count, material.Price * perStack, stackProto);
        market.Pools[key] = pool - count * perStackCenti;
    }

    private void ConvertReagentPool(CargoMarketDataComponent market, string key, string reagentId, long pool)
    {
        var containers = _containerByReagent ??= BuildReagentContainerMap();
        if (!containers.TryGetValue(reagentId, out var container))
            return; // Pools with no container proto stay pooled; future protos unlock them.

        if (!_prototypeManager.TryIndex<ReagentPrototype>(reagentId, out var reagent))
            return;

        var count = pool / container.FillCentiUnits;
        if (count <= 0)
            return;

        // Full container value: its reagent fill at live per-unit price, plus the shell (the
        // container proto's estimate minus the contents the estimate already includes).
        var protoEstimate = _pricingSystem.GetEstimatedPrice(_prototypeManager.Index<EntityPrototype>(container.Proto));
        var fillUnits = container.FillCentiUnits / 100.0;
        var contentValue = fillUnits * reagent.PricePerUnit;
        var shell = Math.Max(0, protoEstimate - contentValue);

        market.MarketDataList.Upsert(container.Proto, (int)count, contentValue + shell);
        market.Pools[key] = pool - count * container.FillCentiUnits;
    }

    private void ConvertGasPool(CargoMarketDataComponent market, string key, string gasId, long pool)
    {
        var canisters = _canisterByGas ??= BuildGasCanisterMap();
        if (!canisters.TryGetValue(gasId, out var canister))
            return;

        var count = pool / canister.FillCentiMoles;
        if (count <= 0)
            return;

        var gasProto = _prototypeManager.Index<GasPrototype>(gasId);
        var shell = _pricingSystem.GetEstimatedPrice(_prototypeManager.Index<EntityPrototype>(canister.Proto));
        var contentValue = canister.FillCentiMoles / 100.0 * gasProto.PricePerMole;

        market.MarketDataList.Upsert(canister.Proto, (int)count, contentValue + shell);
        market.Pools[key] = pool - count * canister.FillCentiMoles;
    }

    /// <summary>
    /// Reagent -> container mapping, discovered by prototype shape rather than naming convention:
    /// a portable (Item) entity prototype whose authored solutions hold exactly one reagent, at
    /// least 100u of it. The largest fill per reagent wins, which picks the 1000u chemical barrels
    /// where they exist and falls back to jugs where they do not.
    /// </summary>
    private Dictionary<string, (string Proto, long FillCentiUnits)> BuildReagentContainerMap()
    {
        var map = new Dictionary<string, (string Proto, long FillCentiUnits)>();

        foreach (var proto in _prototypeManager.EnumeratePrototypes<EntityPrototype>())
        {
            if (proto.Abstract || !proto.Components.ContainsKey(Factory.GetComponentName<ItemComponent>()))
                continue;
            if (!proto.TryComp<SolutionContainerManagerComponent>(out var solutions, Factory)
                || solutions.Solutions is not { } authored)
                continue;

            string? reagentId = null;
            long totalCenti = 0;
            var singleReagent = true;
            foreach (var (_, solution) in authored)
            {
                foreach (var (reagent, quantity) in solution.Contents)
                {
                    if (reagentId == null || reagentId == reagent.Prototype)
                    {
                        reagentId = reagent.Prototype;
                        totalCenti += (long)Math.Round((float)quantity * 100);
                    }
                    else
                    {
                        singleReagent = false;
                    }
                }
            }

            if (!singleReagent || reagentId == null || totalCenti < 100 * 100)
                continue;

            if (!map.TryGetValue(reagentId, out var existing) || existing.FillCentiUnits < totalCenti)
                map[reagentId] = (proto.ID, totalCenti);
        }

        Log.Info($"Market shredder: mapped {map.Count} reagent container prototype(s).");
        return map;
    }

    /// <summary>
    /// Gas -> canister mapping: an entity prototype carrying GasCanister whose authored mixture is
    /// a single gas. Largest fill per gas wins.
    /// </summary>
    private Dictionary<string, (string Proto, long FillCentiMoles)> BuildGasCanisterMap()
    {
        var map = new Dictionary<string, (string Proto, long FillCentiMoles)>();

        foreach (var proto in _prototypeManager.EnumeratePrototypes<EntityPrototype>())
        {
            if (proto.Abstract || !proto.TryComp<GasCanisterComponent>(out var canister, Factory))
                continue;

            var mixture = canister.Air;
            var gasIndex = -1;
            var singleGas = true;
            for (var i = 0; i < mixture.Moles.Length; i++)
            {
                if (mixture.Moles[i] <= 0)
                    continue;
                if (gasIndex == -1)
                    gasIndex = i;
                else
                    singleGas = false;
            }

            if (!singleGas || gasIndex == -1)
                continue;

            var gasId = _atmosphere.GetGas(gasIndex).ID;
            var fillCenti = (long)Math.Round(mixture.Moles[gasIndex] * 100);
            if (fillCenti <= 0)
                continue;

            if (!map.TryGetValue(gasId, out var existing) || existing.FillCentiMoles < fillCenti)
                map[gasId] = (proto.ID, fillCenti);
        }

        Log.Info($"Market shredder: mapped {map.Count} gas canister prototype(s).");
        return map;
    }
}
