using Content.Server._NF.Contraband.Components;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Stack;
using Content.Server.Station.Systems;
using Content.Shared._NF.Contraband;
using Content.Shared._NF.Contraband.BUI;
using Content.Shared._NF.Contraband.Components;
using Content.Shared._NF.Contraband.Events;
using Content.Shared.Contraband;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Content.Shared.Coordinates;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Prototypes;
using Content.Server._NF.Bank; // Triad: BM smuggling mirror
using Content.Server._Triad.Market; // Triad: market data
using Content.Server.Database; // Triad: market data
using Content.Shared._NF.Bank.BUI; // Triad: BM smuggling mirror
using Content.Shared._NF.Bank.Components; // Triad: BM smuggling mirror
using Robust.Shared.Player; // Triad: market data

namespace Content.Server._NF.Contraband.Systems;

/// <summary>
/// Contraband system. Contraband Pallet UI Console is mostly a copy of the system in cargo. Checkraze Note: copy of my code from cargosystems.shuttles.cs
/// </summary>
public sealed partial class ContrabandTurnInSystem : SharedContrabandTurnInSystem
{
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private UserInterfaceSystem _uiSystem = default!;
    [Dependency] private BankSystem _bank = default!; // Triad: BM smuggling mirror
    [Dependency] private PricingSystem _pricing = default!; // Triad: BM smuggling mirror
    [Dependency] private IMarketDataManager _market = default!; // Triad: market data
    [Dependency] private ISharedPlayerManager _playerManager = default!; // Triad: market data

    private EntityQuery<MobStateComponent> _mobQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<CargoSellBlacklistComponent> _blacklistQuery;

    public override void Initialize()
    {
        base.Initialize();

        _xformQuery = GetEntityQuery<TransformComponent>();
        _blacklistQuery = GetEntityQuery<CargoSellBlacklistComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();

        SubscribeLocalEvent<ContrabandPalletConsoleComponent, ContrabandPalletSellMessage>(OnPalletSale);
        SubscribeLocalEvent<ContrabandPalletConsoleComponent, ContrabandPalletAppraiseMessage>(OnPalletAppraise);
        SubscribeLocalEvent<ContrabandPalletConsoleComponent, BoundUIOpenedEvent>(OnPalletUIOpen);
    }

    private void UpdatePalletConsoleInterface(EntityUid uid, ContrabandPalletConsoleComponent comp)
    {
        var bui = _uiSystem.HasUi(uid, ContrabandPalletConsoleUiKey.Contraband);
        if (Transform(uid).GridUid is not EntityUid gridUid)
        {
            _uiSystem.SetUiState(uid, ContrabandPalletConsoleUiKey.Contraband,
                new ContrabandPalletConsoleInterfaceState(0, 0, false));
            return;
        }

        GetPalletGoods(gridUid, comp, out var toSell, out var amount);

        _uiSystem.SetUiState(uid, ContrabandPalletConsoleUiKey.Contraband,
            new ContrabandPalletConsoleInterfaceState((int) amount, toSell.Count, true));
    }

    private void OnPalletUIOpen(EntityUid uid, ContrabandPalletConsoleComponent component, BoundUIOpenedEvent args)
    {
        var player = args.Actor;

        if (player == null)
            return;

        UpdatePalletConsoleInterface(uid, component);
    }

    /// <summary>
    /// Ok so this is just the same thing as opening the UI, its a refresh button.
    /// I know this would probably feel better if it were like predicted and dynamic as pallet contents change
    /// However.
    /// I dont want it to explode if cargo uses a conveyor to move 8000 pineapple slices or whatever, they are
    /// known for their entity spam i wouldnt put it past them
    /// </summary>

    private void OnPalletAppraise(EntityUid uid, ContrabandPalletConsoleComponent component, ContrabandPalletAppraiseMessage args)
    {
        var player = args.Actor;

        if (player == null)
            return;

        UpdatePalletConsoleInterface(uid, component);
    }

    private List<(EntityUid Entity, ContrabandPalletComponent Component)> GetContrabandPallets(EntityUid gridUid)
    {
        var pads = new List<(EntityUid, ContrabandPalletComponent)>();
        var query = AllEntityQuery<ContrabandPalletComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var compXform))
        {
            if (compXform.ParentUid != gridUid ||
                !compXform.Anchored)
            {
                continue;
            }

            pads.Add((uid, comp));
        }

        return pads;
    }

    private void SellPallets(EntityUid gridUid, ContrabandPalletConsoleComponent component, EntityUid? station, out int amount, out double spesoValue, MarketRecord? capture = null) // Triad: add spesoValue + capture
    {
        station ??= _station.GetOwningStation(gridUid);
        GetPalletGoods(gridUid, component, out var toSell, out amount);

        Log.Debug($"{component.Faction} sold {toSell.Count} contraband items for {amount}");

        if (station != null)
        {
            var ev = new EntitySoldEvent(toSell, gridUid);
            RaiseLocalEvent(ref ev);
        }

        // Triad: appraise the fenced goods before they are deleted - the speso value feeds the
        // hidden BlackMarket mirror, and the lines make the fence half of the smuggling loop
        // queryable.
        spesoValue = 0;
        foreach (var ent in toSell)
        {
            var appraised = _pricing.GetPrice(ent);
            spesoValue += appraised;
            if (capture != null && MetaData(ent).EntityPrototype is { } proto)
            {
                capture.AddLine(proto.ID, MarketDirection.Sale, 1,
                    (long)Math.Round(appraised * 100), (long)Math.Round(appraised * 100),
                    MarketPriceSource.Unknown);
            }
        }
        // End Triad

        foreach (var ent in toSell)
        {
            Del(ent);
        }
    }

    private void GetPalletGoods(EntityUid gridUid, ContrabandPalletConsoleComponent console, out HashSet<EntityUid> toSell, out int amount)
    {
        amount = 0;
        toSell = new HashSet<EntityUid>();

        foreach (var (palletUid, _) in GetContrabandPallets(gridUid))
        {
            foreach (var ent in _lookup.GetEntitiesIntersecting(palletUid,
                         LookupFlags.Dynamic | LookupFlags.Sundries | LookupFlags.Approximate))
            {
                // Dont sell:
                // - anything already being sold
                // - anything anchored (e.g. light fixtures)
                // - anything blacklisted (e.g. players).
                if (toSell.Contains(ent) ||
                    _xformQuery.TryGetComponent(ent, out var xform) &&
                    (xform.Anchored || !CanSell(ent, xform)))
                {
                    continue;
                }

                if (_blacklistQuery.HasComponent(ent))
                    continue;

                if (TryComp<ContrabandComponent>(ent, out var comp))
                {
                    if (!comp.TurnInValues.ContainsKey(console.RewardType))
                        continue;

                    var value = comp.TurnInValues[console.RewardType];
                    // Mono Begin - Accounting for stacks of contraband
                    if (TryComp<StackComponent>(ent, out var stackcomp))
                        value *= stackcomp.Count;
                    // Mono End
                    if (value <= 0)
                        continue;
                    amount += value;
                    toSell.Add(ent); // Mono - Moved down to not sell valueless contraband
                }
            }
        }
    }

    private bool CanSell(EntityUid uid, TransformComponent xform)
    {
        if (_mobQuery.HasComponent(uid))
        {
            if (_mobQuery.GetComponent(uid).CurrentState == MobState.Dead) // Allow selling alive prisoners
            {
                return false;
            }
            return true;
        }

        // Recursively check for mobs at any point.
        var children = xform.ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (!CanSell(child, _xformQuery.GetComponent(child)))
                return false;
        }
        // Look for blacklisted items and stop the selling of the container.
        if (_blacklistQuery.HasComponent(uid))
        {
            return false;
        }
        return true;
    }

    private void OnPalletSale(EntityUid uid, ContrabandPalletConsoleComponent component, ContrabandPalletSellMessage args)
    {
        var player = args.Actor;

        if (player == null)
            return;

        if (Transform(uid).GridUid is not EntityUid gridUid)
        {
            _uiSystem.SetUiState(uid, ContrabandPalletConsoleUiKey.Contraband,
                new ContrabandPalletConsoleInterfaceState(0, 0, false));
            return;
        }

        // Triad: begin - the fence event. Captured as ContrabandTurnIn in the payout currency,
        // and the goods' speso appraisal deposits to the hidden BlackMarket account as
        // SmugglingIncome (its own standalone ledger row, since the currencies differ).
        var capture = _market.Enabled
            ? new MarketRecord
            {
                Kind = MarketTransactionKind.ContrabandTurnIn,
                Currency = component.RewardType,
                Rail = MarketRail.Cash,
                ConsoleProto = MetaData(uid).EntityPrototype?.ID,
                LocationName = _station.GetOwningStation(uid) is { } locStation ? MetaData(locStation).EntityName : null,
            }
            : null;

        SellPallets(gridUid, component, null, out var price, out var spesoValue, capture);

        if ((int)spesoValue > 0)
            _bank.TrySectorDeposit(SectorBankAccount.BlackMarket, (int)spesoValue, LedgerEntryType.SmugglingIncome);

        if (capture != null)
        {
            capture.Gross = price * 100L;
            capture.Net = price * 100L;
            capture.AddSplit("Player", nameof(MarketTransactionKind.ContrabandTurnIn), price * 100L);
            capture.Calc = $"{{\"spesoAppraisalMinor\":{(long)Math.Round(spesoValue * 100)}}}";
            if (_playerManager.TryGetSessionByEntity(args.Actor, out var session))
                capture.ActorUserId = session.UserId;
            _market.Record(capture);
        }
        // Triad: end

        var stackPrototype = _protoMan.Index<StackPrototype>(component.RewardType);
        _stack.Spawn(price, stackPrototype, uid.ToCoordinates());
        UpdatePalletConsoleInterface(uid, component);
    }
}
