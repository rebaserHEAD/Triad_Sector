using System.Threading.Tasks;
using Content.Server._NF.Market.Components;
using Content.Server._NF.Market.Extensions;
using Content.Server._Triad.Market;
using Content.Server.Database;
using Content.Shared._NF.Market;
using Content.Shared._Triad.CCVar;
using Content.Shared.Stacks;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NF.Market.Systems;

// Triad: persistent per-POI market inventory. The shelf on a station with a PersistKey survives
// round restarts through the market_inventory table; everything else keeps round-local behavior.
public sealed partial class MarketSystem
{
    [Dependency] private readonly MarketInventoryStore _inventoryStore = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>
    /// Debounce between dirty-flag saves. Ten seconds keeps the durable copy at most one selling
    /// burst behind while a busy mall does not rewrite its shelf on every sale.
    /// </summary>
    private static readonly TimeSpan PersistSaveInterval = TimeSpan.FromSeconds(10);

    private readonly HashSet<EntityUid> _dirtyMarkets = new();
    private readonly List<(EntityUid Station, string Key, Task<List<MarketInventory>> Task)> _pendingLoads = new();
    private TimeSpan _nextPersistSave;

    private void InitializePersistence()
    {
        SubscribeLocalEvent<CargoMarketDataComponent, MapInitEvent>(OnMarketMapInit);
        SubscribeLocalEvent<CargoMarketDataComponent, ComponentShutdown>(OnMarketShutdown);
        SubscribeLocalEvent<MarketConsoleComponent, ComponentShutdown>(OnMarketConsoleShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Apply finished loads. The task ran on the database's thread; the result is applied here,
        // on the game thread, where prototypes can be validated and the component touched safely.
        for (var i = _pendingLoads.Count - 1; i >= 0; i--)
        {
            var (station, key, task) = _pendingLoads[i];
            if (!task.IsCompleted)
                continue;

            _pendingLoads.RemoveAt(i);

            if (!task.IsCompletedSuccessfully)
            {
                Log.Error($"Failed to load market inventory '{key}': {task.Exception?.GetBaseException().Message}");
                continue;
            }

            if (Deleted(station) || !TryComp<CargoMarketDataComponent>(station, out var market))
                continue;

            ApplyLoadedInventory((station, market), key, task.Result);
        }

        // Debounced dirty-flag save.
        if (_dirtyMarkets.Count == 0 || _timing.CurTime < _nextPersistSave)
            return;

        _nextPersistSave = _timing.CurTime + PersistSaveInterval;
        foreach (var station in _dirtyMarkets)
            SavePersistentMarket(station);
        _dirtyMarkets.Clear();
    }

    private void OnMarketMapInit(EntityUid uid, CargoMarketDataComponent component, MapInitEvent args)
    {
        if (component.PersistKey is not { } key || !_cfg.GetCVar(TriadCCVars.MarketPersistInventory))
            return;

        _pendingLoads.Add((uid, key, _inventoryStore.LoadInventory(key)));
    }

    /// <summary>
    /// The last write for a shelf, fired when the station goes away - at round restart that is the
    /// flush, mirroring MarketDataSystem's drain-at-restart rationale (the deploy pipeline restarts
    /// this server at round end). Fire and forget on purpose: the server keeps ticking through the
    /// post-round lobby, so the write has time to land.
    /// </summary>
    private void OnMarketShutdown(EntityUid uid, CargoMarketDataComponent component, ComponentShutdown args)
    {
        if (component.PersistKey is not { } key || !_cfg.GetCVar(TriadCCVars.MarketPersistInventory))
            return;

        _dirtyMarkets.Remove(uid);
        SaveRows(key, SnapshotInventory(uid, component));
    }

    /// <summary>
    /// A destroyed console must not eat the stock reserved in its cart: cart-add moves goods off
    /// the shared shelf, so shutdown moves whatever is left back. This also makes the round-end
    /// flush ordering-safe - however console and station deletion interleave, carted stock either
    /// returns to the shelf here or is folded in by <see cref="SnapshotInventory"/>.
    /// </summary>
    private void OnMarketConsoleShutdown(EntityUid uid, MarketConsoleComponent component, ComponentShutdown args)
    {
        if (component.CartDataList.Count == 0)
            return;

        if (_station.GetOwningStation(uid) is not { } station
            || !TryComp<CargoMarketDataComponent>(station, out var market))
            return;

        foreach (var data in component.CartDataList)
            market.MarketDataList.Upsert(data.Prototype, data.Quantity, data.Price, data.StackPrototype);
        component.CartDataList.Clear();
        MarkMarketDirty(station);
    }

    /// <summary>
    /// Flags a station's shelf as needing a save. Cheap to call after any mutation; does nothing
    /// for round-local markets or while persistence is off.
    /// </summary>
    private void MarkMarketDirty(EntityUid? station)
    {
        if (station is not { } uid)
            return;
        if (!TryComp<CargoMarketDataComponent>(uid, out var market) || market.PersistKey == null)
            return;
        if (!_cfg.GetCVar(TriadCCVars.MarketPersistInventory))
            return;

        _dirtyMarkets.Add(uid);
    }

    private void SavePersistentMarket(EntityUid station)
    {
        if (Deleted(station)
            || !TryComp<CargoMarketDataComponent>(station, out var market)
            || market.PersistKey is not { } key)
            return;

        SaveRows(key, SnapshotInventory(station, market));
    }

    private void SaveRows(string key, List<MarketInventory> rows)
    {
        _inventoryStore.SaveInventory(key, rows).ContinueWith(
            t => Log.Error($"Failed to save market inventory '{key}': {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Materializes a shelf into rows, on the game thread. The snapshot is the shared list PLUS
    /// every outstanding console cart on the station: cart-add is a reservation that moves stock
    /// off the shared list, and reserved-but-unpaid goods still belong to the shelf. Without the
    /// fold, a crash or restart with full carts would silently delete that stock.
    /// </summary>
    private List<MarketInventory> SnapshotInventory(EntityUid station, CargoMarketDataComponent market)
    {
        // (proto, stack) -> (quantity, latest unit price). Carted quantities merge onto shelf rows.
        var totals = new Dictionary<(string Proto, string? Stack), (long Quantity, double Price)>();

        void Fold(MarketData data)
        {
            if (data.Quantity <= 0)
                return;
            var mapKey = (data.Prototype.Id, data.StackPrototype?.Id);
            totals.TryGetValue(mapKey, out var existing);
            totals[mapKey] = (existing.Quantity + data.Quantity, data.Price);
        }

        foreach (var data in market.MarketDataList)
            Fold(data);

        var consoles = EntityQueryEnumerator<MarketConsoleComponent>();
        while (consoles.MoveNext(out var consoleUid, out var console))
        {
            if (console.CartDataList.Count == 0 || _station.GetOwningStation(consoleUid) != station)
                continue;
            foreach (var data in console.CartDataList)
                Fold(data);
        }

        var now = DateTime.UtcNow;
        var rows = new List<MarketInventory>(totals.Count + market.Pools.Count);
        foreach (var ((proto, stack), (quantity, price)) in totals)
        {
            rows.Add(new MarketInventory
            {
                PoiKey = market.PersistKey!,
                Kind = MarketInventoryKind.Item,
                ProtoId = proto,
                StackProto = stack,
                Quantity = quantity * 100,
                UnitPrice = (long)Math.Round(price * 100),
                UpdatedAt = now,
            });
        }

        // Shred pools ride in the same table under their key prefix. Unit price is zero on
        // purpose: pools are quantities awaiting a container, valued live at conversion time.
        foreach (var (key, centiUnits) in market.Pools)
        {
            if (centiUnits <= 0)
                continue;

            var sep = key.IndexOf(':');
            if (sep <= 0)
                continue;

            var kind = key[..sep] switch
            {
                "reagent" => MarketInventoryKind.Reagent,
                "gas" => MarketInventoryKind.Gas,
                "material" => MarketInventoryKind.Material,
                _ => (MarketInventoryKind?)null,
            };
            if (kind is not { } poolKind)
                continue;

            rows.Add(new MarketInventory
            {
                PoiKey = market.PersistKey!,
                Kind = poolKind,
                ProtoId = key[(sep + 1)..],
                Quantity = centiUnits,
                UnitPrice = 0,
                UpdatedAt = now,
            });
        }

        return rows;
    }

    /// <summary>
    /// Replaces a shelf with what the table holds. Prototype ids are validated against the running
    /// game and unknown ones dropped with a log line rather than crashing or resurrecting ghosts -
    /// the ship-save orphan lesson: a removed prototype must never take the whole shelf with it.
    /// </summary>
    private void ApplyLoadedInventory(Entity<CargoMarketDataComponent> station, string key, List<MarketInventory> rows)
    {
        var list = new List<MarketData>(rows.Count);
        var pools = new Dictionary<string, long>();
        var dropped = 0;

        foreach (var row in rows)
        {
            // Pool rows come back as loose units under their key prefix.
            if (row.Kind != MarketInventoryKind.Item)
            {
                var prefix = row.Kind switch
                {
                    MarketInventoryKind.Reagent => "reagent",
                    MarketInventoryKind.Gas => "gas",
                    MarketInventoryKind.Material => "material",
                    _ => null,
                };
                if (prefix != null && row.Quantity > 0)
                    pools[$"{prefix}:{row.ProtoId}"] = row.Quantity;
                continue;
            }

            if (!_prototypeManager.HasIndex<EntityPrototype>(row.ProtoId))
            {
                dropped++;
                Log.Warning($"Market inventory '{key}': dropping row for unknown prototype '{row.ProtoId}'.");
                continue;
            }

            ProtoId<StackPrototype>? stackProto = null;
            if (row.StackProto != null)
            {
                if (!_prototypeManager.HasIndex<StackPrototype>(row.StackProto))
                {
                    dropped++;
                    Log.Warning($"Market inventory '{key}': dropping row for '{row.ProtoId}' with unknown stack prototype '{row.StackProto}'.");
                    continue;
                }
                stackProto = row.StackProto;
            }

            var quantity = (int)(row.Quantity / 100);
            if (quantity <= 0)
                continue;

            list.Add(new MarketData(row.ProtoId, stackProto, quantity, row.UnitPrice / 100.0));
        }

        station.Comp.MarketDataList = list;
        station.Comp.Pools = pools;

        // Convert pools that already cover a container - the prototype roster may have changed
        // since the save, and a pool topped up across rounds converts on arrival.
        foreach (var poolKey in new List<string>(pools.Keys))
            ConvertPool(station.Comp, poolKey);

        Log.Info($"Market inventory '{key}': loaded {list.Count} listing(s) and {pools.Count} pool(s), dropped {dropped}.");
    }
}
