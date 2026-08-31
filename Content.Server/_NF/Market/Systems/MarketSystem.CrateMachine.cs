using Content.Server._NF.CrateMachine;
using Content.Server._NF.Market.Components;
using Content.Server._NF.Market.Extensions;
using Content.Shared._NF.Market;
using Content.Shared._NF.Market.Components;
using Content.Shared._NF.Market.Events;
using Content.Shared._NF.Bank.Components;
using Robust.Shared.Audio;
using Robust.Shared.Player;
using Content.Shared._NF.CrateMachine.Components;

using Content.Server._Triad.Market; // Triad: market data
using Content.Server.Database; // Triad: market data

namespace Content.Server._NF.Market.Systems;

public sealed partial class MarketSystem
{
    [Dependency] private CrateMachineSystem _crateMachine = default!;

    private void InitializeCrateMachine()
    {
        SubscribeLocalEvent<MarketConsoleComponent, MarketPurchaseMessage>(OnMarketConsolePurchaseCrateMessage);
        SubscribeLocalEvent<CrateMachineComponent, CrateMachineOpenedEvent>(OnCrateMachineOpened);
    }

    private void OnMarketConsolePurchaseCrateMessage(EntityUid consoleUid,
        MarketConsoleComponent component,
        ref MarketPurchaseMessage args)
    {
        var marketMod = 1f;
        if (TryComp<MarketModifierComponent>(consoleUid, out var marketModComponent))
        {
            marketMod = marketModComponent.Mod;
        }

        if (!_crateMachine.FindNearestUnoccupied(consoleUid, component.MaxCrateMachineDistance, out var machineUid) || !_entityManager.TryGetComponent<CrateMachineComponent> (machineUid, out var comp))
        {
            _popup.PopupEntity(Loc.GetString("market-no-crate-machine-available"), consoleUid, Filter.PvsExcept(consoleUid), true);
            _audio.PlayPredicted(component.ErrorSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));

            return;
        }
        OnPurchaseCrateMessage(machineUid.Value, consoleUid, comp, component, marketMod, args);
    }

    private void OnPurchaseCrateMessage(EntityUid crateMachineUid,
        EntityUid consoleUid,
        CrateMachineComponent component,
        MarketConsoleComponent consoleComponent,
        float marketMod,
        MarketPurchaseMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (!TryComp<BankAccountComponent>(player, out var bankAccount))
            return;

        TrySpawnCrate(crateMachineUid, player, consoleUid, component, consoleComponent, marketMod, bankAccount);
    }

    private void TrySpawnCrate(EntityUid crateMachineUid,
        EntityUid player,
        EntityUid consoleUid,
        CrateMachineComponent component,
        MarketConsoleComponent consoleComponent,
        float marketMod,
        BankAccountComponent playerBank)
    {
        if (!TryComp<MarketItemSpawnerComponent>(crateMachineUid, out var itemSpawner))
            return;

        // Triad: purchase tax on top of list price, collected into the sector pot. Splits go on
        // the record before the withdrawal enqueues it; the deposits move only on success.
        var spawnCost = int.Abs(MarketDataExtensions.GetMarketValue(consoleComponent.CartDataList, marketMod));
        var buyTax = _bankSystem.GetSectorBuyTax(spawnCost);
        var totalCost = spawnCost + buyTax;
        if (playerBank.Balance < totalCost)
            return;

        var record = new MarketRecord
        {
            Kind = MarketTransactionKind.MarketCrate,
            Gross = -totalCost * 100L,
            Tax = buyTax * 100L,
            Net = -spawnCost * 100L,
        };
        _bankSystem.AddSectorTaxSplits(record, buyTax);

        // Withdraw spesos from player
        if (!_bankSystem.TryBankWithdraw(player, totalCost, record)) // Triad: market data; spawnCost >> totalCost
        {
            _popup.PopupEntity(Loc.GetString("market-insufficient-funds"), consoleUid, player);
            _audio.PlayPredicted(consoleComponent.ErrorSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));
            return;
        }
        _bankSystem.DepositSectorTax(buyTax); // Triad
        // End Triad
        _audio.PlayPredicted(consoleComponent.SuccessSound, consoleUid, null, AudioParams.Default.WithMaxDistance(5f));

        // Triad: chunk the purchase into as few crates as possible and queue them; the machine
        // dispenses one crate at a time, opening again for the next chunk once the previous crate
        // is taken. Greedy fill-stacks-first is slot-minimal because every item costs an integer
        // number of slots.
        var chunks = ChunkForCrates(consoleComponent.CartDataList, consoleComponent.CrateCapacity);
        consoleComponent.CartDataList = [];
        MarkMarketDirty(_station.GetOwningStation(consoleUid)); // Triad: persistent inventory - the sold cart leaves the snapshot
        if (chunks.Count == 0)
            return;
        itemSpawner.ItemsToSpawn = chunks[0];
        chunks.RemoveAt(0);
        itemSpawner.PendingChunks = chunks;
        _crateMachine.OpenFor(crateMachineUid, component);
    }

    private void SpawnCrateItems(List<MarketData> spawnList, EntityUid targetCrate)
    {
        var coordinates = Transform(targetCrate).Coordinates;
        foreach (var data in spawnList)
        {
            if (data.StackPrototype != null && _prototypeManager.TryIndex(data.StackPrototype, out var stackPrototype))
            {
                var entityList = _stackSystem.SpawnMultiple(stackPrototype.Spawn, data.Quantity, coordinates);
                foreach (var entity in entityList)
                {
                    _crateMachine.InsertIntoCrate(entity, targetCrate);
                }
            }
            else
            {
                // Spawn the requested quantity of non-stackable items
                for (int i = 0; i < data.Quantity; i++)
                {
                    var spawn = Spawn(data.Prototype, coordinates);
                    _crateMachine.InsertIntoCrate(spawn, targetCrate);
                }
            }
        }
    }

    private void OnCrateMachineOpened(EntityUid uid, CrateMachineComponent component, CrateMachineOpenedEvent args)
    {
        if (!TryComp<MarketItemSpawnerComponent>(uid, out var itemSpawner))
            return;

        var targetCrate = _crateMachine.SpawnCrate(uid, component);
        SpawnCrateItems(itemSpawner.ItemsToSpawn, targetCrate);
        itemSpawner.ItemsToSpawn = [];
    }

    // Triad: begin, multi-crate dispensing
    /// <summary>
    /// Splits a purchase into per-crate chunks of at most <paramref name="capacity"/> entity
    /// slots. Stacks pack at their max stack count per slot, everything else costs one slot;
    /// with integral slot costs a greedy split is minimal.
    /// </summary>
    private List<List<MarketData>> ChunkForCrates(List<MarketData> items, int capacity)
    {
        capacity = int.Max(1, capacity);
        var chunks = new List<List<MarketData>>();
        var current = new List<MarketData>();
        var slotsUsed = 0;

        void CloseChunk()
        {
            if (current.Count == 0)
                return;
            chunks.Add(current);
            current = [];
            slotsUsed = 0;
        }

        foreach (var data in items)
        {
            if (data.Quantity <= 0)
                continue;

            var perSlot = GetAmountPerEntitySpace(data);
            if (perSlot == null)
            {
                // Infinite stack: one slot carries any amount.
                if (slotsUsed >= capacity)
                    CloseChunk();
                current.Add(data);
                slotsUsed += 1;
                continue;
            }

            var remaining = data.Quantity;
            while (remaining > 0)
            {
                if (slotsUsed >= capacity)
                    CloseChunk();

                var take = int.Min(remaining, (capacity - slotsUsed) * perSlot.Value);
                current.Add(new MarketData(data.Prototype, data.StackPrototype, take, data.Price));
                slotsUsed += (take + perSlot.Value - 1) / perSlot.Value;
                remaining -= take;
            }
        }

        CloseChunk();
        return chunks;
    }

    /// <summary>
    /// Feeds queued chunks to idle machines: once the previous crate is taken and the door is
    /// shut, the next paid-for chunk opens the machine again.
    /// </summary>
    private void UpdateCrateDispensing()
    {
        var query = EntityQueryEnumerator<MarketItemSpawnerComponent, CrateMachineComponent>();
        while (query.MoveNext(out var uid, out var spawner, out var machine))
        {
            if (spawner.PendingChunks.Count == 0 || spawner.ItemsToSpawn.Count > 0)
                continue;
            if (_crateMachine.IsOccupied(uid, machine))
                continue;

            spawner.ItemsToSpawn = spawner.PendingChunks[0];
            spawner.PendingChunks.RemoveAt(0);
            _crateMachine.OpenFor(uid, machine);
        }
    }
    // Triad: end
}
