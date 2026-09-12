// Triad: drydock tab. A partial of the NF ShipyardSystem, kept in the Triad tree beside the other
// Triad console work rather than in _NF/, so an upstream merge touching the shipyard console only
// ever conflicts on the subscriptions in ShipyardSystem.Initialize and the two marked lines in the
// purchase handler. The pipeline this sits in front of lives in Content.Server._Triad.Drydock.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NF.Shipyard.Components;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Market;
using Content.Server.Database;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._Mono.Shipyard;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._NF.ShuttleRecords;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Database;
using Content.Shared.Forensics.Components;
using Content.Shared.Preferences;
using Content.Shared.StationRecords;
using Content.Server.StationEvents.Components;
using Content.Server.StationRecords;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    [Dependency] private DrydockSystem _drydock = default!;
    [Dependency] private DrydockStore _drydockStore = default!;
    [Dependency] private ShipSizeSystem _drydockSizes = default!;

    /// <summary>
    /// The round to stamp an audit row with, or null when there is no round yet.
    ///
    /// <para><see cref="GameTicker.RoundId"/> reads 0 before a round has been filed, and the round
    /// columns are real foreign keys, so passing that straight through makes the insert fail on a
    /// constraint rather than recording "no round". Nullable is what the schema means by it. The
    /// same guard is written at every other Triad call site that stamps a round.</para>
    /// </summary>
    private int? DrydockRoundId => _gameTicker.RoundId > 0 ? _gameTicker.RoundId : null;

    // ---------------------------------------------------------------- Pricing

    /// <summary>The berth price for a hull class, from the prototype ladder. Zero when the ladder has no entry, which disables the charge rather than refusing the purchase.</summary>
    public int DrydockBerthPrice(ShipSizeClass sizeClass)
    {
        return _prototypeManager.TryIndex<DrydockBerthClassPrototype>(sizeClass.ToString(), out var proto) ? proto.Price : 0;
    }

    /// <summary>The berth price for a live grid, read from its built tile count, never from any cache.</summary>
    public int DrydockBerthPriceFor(EntityUid grid)
    {
        return TryComp<MapGridComponent>(grid, out var map) ? DrydockBerthPrice(_drydockSizes.GetSizeClass((grid, map))) : 0;
    }

    private Dictionary<string, int> DrydockBerthPrices()
    {
        var prices = new Dictionary<string, int>();
        foreach (var sizeClass in Enum.GetValues<ShipSizeClass>())
            prices[sizeClass.ToString()] = DrydockBerthPrice(sizeClass);
        return prices;
    }

    private static ShipSizeClass? NextSizeClass(ShipSizeClass sizeClass)
    {
        return sizeClass == ShipSizeClass.SuperCapital ? null : sizeClass + 1;
    }

    // ---------------------------------------------------------------- Bundled berth on purchase

    /// <summary>
    /// Every ship bought at a shipyard comes with a berth of its hull class, charged on top of the
    /// vessel price. The purchase handler already required the whole amount before charging the
    /// vessel, so a failure to pay here is a race and not the ordinary case; a new owner is never
    /// left without a berth over it, they get one granted and the shortfall is logged.
    /// </summary>
    private void OnShuttlePurchased(ShipyardShuttlePurchaseEvent ev)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled))
            return;

        if (!TryComp<ShipOwnershipComponent>(ev.Shuttle, out var ownership)
            || !TryComp<MapGridComponent>(ev.Shuttle, out var map))
        {
            return;
        }

        // A vessel issued on a voucher, or one its faction has blacklisted from saving, can never
        // be stored, so it brings no berth with it. Faction crews are not drydock customers.
        if (TryComp<ShuttleDeedComponent>(ev.Shuttle, out var deed) && deed.PurchasedWithVoucher
            || HasComp<ShipSavingBlacklistComponent>(ev.Shuttle))
        {
            return;
        }

        var owner = ownership.OwnerUserId.UserId;
        var sizeClass = _drydockSizes.GetSizeClass((ev.Shuttle, map));
        var price = DrydockBerthPrice(sizeClass);

        var paid = 0;
        var kind = DrydockBerthKind.Granted;
        if (price > 0)
        {
            if (_bank.TryBankWithdraw(ev.Purchaser, price, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth }))
            {
                paid = price;
                kind = DrydockBerthKind.Purchased;
            }
            else
            {
                Log.Warning($"Drydock: {ToPrettyString(ev.Purchaser)} bought {ToPrettyString(ev.Shuttle)} but could not pay the {price} berth fee after the vessel; berth granted free.");
            }
        }

        _ = GrantPurchasedBerthAsync(owner, sizeClass, kind, paid, ev.Purchaser);
    }

    private async Task GrantPurchasedBerthAsync(Guid owner, ShipSizeClass sizeClass, DrydockBerthKind kind, int paid, EntityUid purchaser)
    {
        try
        {
            await _drydockStore.AddBerth(owner, sizeClass, kind, paid, owner, DrydockRoundId);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: berth for a purchased {sizeClass} could not be created for {owner}: {e.Message}");
            if (paid > 0 && !TerminatingOrDeleted(purchaser))
                _bank.TryBankDeposit(purchaser, paid, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth });
        }
    }

    // ---------------------------------------------------------------- Message handlers

    // Every handler is async void, which is what a BUI message subscription has to be, and an
    // exception escaping an async void has nowhere to go but the synchronization context. A
    // database fault is a logged refusal, never an unhandled throw.
    private async void OnStoreMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleStoreMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryDrydockStore(uid, component, player, (ShipyardConsoleUiKey)args.UiKey, args.BerthId);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: store from console {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
            if (!TerminatingOrDeleted(player))
                ConsolePopup(player, Loc.GetString("shipyard-console-store-failed"));
            // Before the refresh, not after: the refresh is what publishes the cached percentage,
            // and a throw from anywhere the pipeline's own finally does not cover would leave the
            // console reporting a store that is no longer running.
            ClearDrydockProgress(uid, component);
            await RefreshAfterRefusal(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
        }
    }

    private async void OnRetrieveMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleRetrieveMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryDrydockRetrieve(uid, component, player, args.ShipId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: retrieve of {args.ShipId} from console {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
            if (!TerminatingOrDeleted(player))
                ConsolePopup(player, Loc.GetString("shipyard-console-retrieve-failed"));
            ClearDrydockProgress(uid, component); // Same reason as the store handler's.
            await RefreshAfterRefusal(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
        }
    }

    private async void OnBuyBerthMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleBuyBerthMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryBuyBerth(uid, component, player, args.SizeClass, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: berth purchase at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
        }
    }

    private async void OnSellBerthMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleSellBerthMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TrySellBerth(uid, component, player, args.BerthId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: berth sale at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
        }
    }

    private async void OnUpgradeBerthMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleUpgradeBerthMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryUpgradeBerth(uid, component, player, args.BerthId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: berth upgrade at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
        }
    }

    private async void OnOfferTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleOfferTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryOfferTransfer(uid, component, player, args.ShipId, args.RecipientUserId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: transfer offer at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
            if (!TerminatingOrDeleted(player))
                ConsolePopup(player, Loc.GetString("shipyard-console-transfer-failed"));
        }
    }

    private async void OnCancelTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleCancelTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryCancelTransfer(uid, component, player, args.TransferId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: transfer cancel at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
        }
    }

    private async void OnDeclineTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleDeclineTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryDeclineTransfer(uid, component, player, args.TransferId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: transfer decline at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
        }
    }

    private async void OnSellStoredShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleSellStoredShipMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TrySellStoredShip(uid, component, player, args.ShipId, args.TypedName, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: sale of {args.ShipId} at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
            if (!TerminatingOrDeleted(player))
                ConsolePopup(player, Loc.GetString("shipyard-console-sell-failed"));
        }
    }

    private async void OnRenameStoredShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleRenameStoredShipMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryRenameStoredShip(uid, component, player, args.ShipId, args.NewName, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: rename of {args.ShipId} at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
        }
    }

    private async void OnMoveStoredShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleMoveStoredShipMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryMoveStoredShip(uid, component, player, args.ShipId, args.BerthId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: move of {args.ShipId} at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
        }
    }

    private async void OnAcceptTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleAcceptTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryAcceptTransfer(uid, component, player, args.TransferId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: transfer accept at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
            if (!TerminatingOrDeleted(player))
                ConsolePopup(player, Loc.GetString("shipyard-console-transfer-failed"));
        }
    }

    private async void OnRedeemImpoundMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleRedeemImpoundMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryRedeemImpound(uid, component, player, args.ShipId, args.BerthId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: reclaim of {args.ShipId} at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
            if (!TerminatingOrDeleted(player))
                ConsolePopup(player, Loc.GetString("shipyard-console-impound-failed"));
        }
    }

    private async void OnAbandonShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleAbandonShipMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryAbandonShip(uid, component, player, args.ShipId, args.TypedName, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: abandon of {args.ShipId} at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
            if (!TerminatingOrDeleted(player))
                ConsolePopup(player, Loc.GetString("shipyard-console-impound-failed"));
        }
    }

    // ---------------------------------------------------------------- State

    /// <summary>
    /// Fills the console's drydock caches for whoever is operating it, then re-publishes the
    /// interface state so the drydock tab shows the fresh lists. The caches exist because the
    /// state builder is synchronous and these come from the database.
    /// </summary>
    internal async Task RefreshDrydockState(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        // Triad: the caches are NOT cleared here, deliberately. Clearing at the top and refilling
        // after five awaited reads leaves several ticks where the cached lists are empty while the
        // tab shows them, so anything publishing in that window - an upstream RefreshState, a card
        // going in or out, a second refresh - sends a state with no berths and no ships. Lists are
        // built into locals and swapped in at the end, so the cache is only ever replaced by a
        // complete set and an early return leaves the last good one standing.
        //
        // No card, no account to list against: the drydock tab is per-operator, and an empty list
        // is the honest answer rather than everything the console has ever seen.
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId
            || !TryComp<ActorComponent>(player, out var actor))
        {
            component.CachedStoredShips = new();
            component.CachedBerths = new();
            component.CachedDeedShip = null;
            component.CachedOffers = new();
            component.CachedCaptains = new();
            component.CachedImpounded = new();
            RefreshDrydockUi(uid, component, player, uiKey);
            return;
        }

        var owner = actor.PlayerSession.UserId.UserId;
        var rows = await _drydockStore.GetShipsByOwner(owner);
        var slots = await _drydockStore.GetBerths(owner);
        var offersOut = await _drydockStore.GetPendingOffersFrom(owner);
        var offersIn = await _drydockStore.GetPendingOffersFor(owner);
        var appraisals = await _drydockStore.GetCurrentAppraisals(owner);

        // Everyone else online, for the transfer picker, with the classes of their free berths so
        // the picker can grey the captains with nowhere to put the ship. Read in one query, and only
        // when the operator has a stored ship to offer: every open tab refreshes on every expiry
        // sweep and every admin action, and a list nobody can pick from is a query per tab for nothing.
        var canOffer = rows.Any(r => r.State == DrydockShipState.Stored);
        var online = canOffer ? _player.Sessions.Where(s => s.UserId.UserId != owner).ToList() : new List<ICommonSession>();
        var freeClasses = await _drydockStore.GetFreeBerthClasses(online.Select(s => s.UserId.UserId));
        var names = await _drydockStore.GetPlayerNames(offersOut.Values.Select(t => t.ToUserId).Concat(offersIn.Select(o => o.Transfer.FromUserId)));

        // The console or the operator may have gone during the reads.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        // Every hull the account has, including the ones that are out: the tab warns when an
        // action would leave a ship with nowhere to dock.
        var storedShips = rows
            .Select(r => new StoredShipInfo(r.ShipGuid, r.ShipName, r.SizeClass, r.State.ToString(), r.BerthId))
            .ToList();

        var berthInfos = new List<DrydockBerthInfo>();
        var offerInfos = new List<DrydockTransferOfferInfo>();
        var captainInfos = new List<DrydockCaptainInfo>();

        var now = DateTime.UtcNow;
        var refund = _configManager.GetCVar(TriadCCVars.DrydockBerthRefund);
        foreach (var slot in slots)
        {
            int? upgradePrice = null;
            string? upgradeClass = null;
            if (DrydockStore.TryParseClass(slot.Berth.MaxSizeClass, out var current) && NextSizeClass(current) is { } next)
            {
                upgradePrice = Math.Max(0, DrydockBerthPrice(next) - DrydockBerthPrice(current));
                upgradeClass = next.ToString();
            }

            DrydockTransfer? escrow = null;
            int? sellPrice = null;
            int? sellBasis = null;
            if (slot.Occupant != null)
            {
                offersOut.TryGetValue(slot.Occupant.ShipGuid, out escrow);
                if (appraisals.TryGetValue(slot.Occupant.ShipGuid, out var appraisal) && appraisal is { } value)
                {
                    sellPrice = DrydockSalePrice((uid, component), value).Net;
                    sellBasis = value;
                }
            }

            berthInfos.Add(new DrydockBerthInfo(
                slot.Berth.BerthId,
                slot.Berth.MaxSizeClass,
                (int)(slot.Berth.PricePaid * refund),
                upgradePrice,
                upgradeClass,
                slot.Occupant?.ShipGuid,
                slot.Occupant?.ShipName,
                slot.Occupant?.SizeClass,
                slot.Occupant?.State.ToString(),
                sellPrice,
                escrow?.Id,
                escrow != null ? CaptainName(escrow.ToUserId, names) : null,
                escrow != null ? SecondsLeft(escrow.ExpiresAt, now) : null,
                sellBasis));
        }

        // The alerts: every offer addressed to this account, with where the ship would land if
        // accepted right now. The berth is chosen again at accept, so this is a preview.
        foreach (var (transfer, ship) in offersIn)
        {
            int? lands = slots
                .Where(s => s.Occupant == null && DrydockStore.Fits(ship.SizeClass, s.Berth.MaxSizeClass))
                .OrderBy(s => DrydockStore.TryParseClass(s.Berth.MaxSizeClass, out var max) ? (int)max : int.MaxValue)
                .ThenBy(s => s.Berth.BerthId)
                .Select(s => (int?)s.Berth.BerthId)
                .FirstOrDefault();

            offerInfos.Add(new DrydockTransferOfferInfo(
                transfer.Id,
                ship.ShipGuid,
                ship.ShipName,
                ship.SizeClass,
                CaptainName(transfer.FromUserId, names),
                transfer.FromUserId,
                lands,
                SecondsLeft(transfer.ExpiresAt, now)));
        }

        foreach (var session in online)
        {
            var id = session.UserId.UserId;
            captainInfos.Add(new DrydockCaptainInfo(id, SessionDisplayName(session), freeClasses.GetValueOrDefault(id) ?? new List<string>()));
        }

        // The impound lot: every hull of the account's the lot holds, with what it costs to get
        // back and which berths it could go into, read off the same slots the rows draw with the
        // store's own preference. The fee is the row's, frozen at impound; the appraisal beside it
        // is what that fee was cut from, so the card can say what share of the hull it is.
        var impoundedInfos = new List<DrydockImpoundedShipInfo>();
        foreach (var row in rows)
        {
            if (row.State != DrydockShipState.Impounded)
                continue;

            var fitting = FittingFreeBerths(slots, row.SizeClass);
            appraisals.TryGetValue(row.ShipGuid, out var basis);

            impoundedInfos.Add(new DrydockImpoundedShipInfo(
                row.ShipGuid,
                row.ShipName,
                row.SizeClass,
                row.ImpoundFee,
                basis,
                row.ImpoundRedeemable,
                row.ImpoundReason,
                PreferredBerth(fitting, row.LastBerthId),
                fitting));
        }

        // Everything is read; swap the whole set in at once. Nothing above this line has touched
        // what the console is currently showing.
        component.CachedStoredShips = storedShips;
        component.CachedBerths = berthInfos;
        component.CachedOffers = offerInfos;
        component.CachedCaptains = captainInfos;
        component.CachedImpounded = impoundedInfos;
        component.CachedDeedShip = BuildDeedShip(uid, targetId, rows, slots);
        RefreshDrydockUi(uid, component, player, uiKey);
    }

    /// <summary>
    /// Whether the ship is docked to one of the station's grids. A store is a hand-over at a berth,
    /// not a remote command: the hull has to be alongside the station whose console is filing it.
    /// Any dock on the ship whose partner sits on a grid the station owns counts.
    /// </summary>
    private bool IsDockedToStation(EntityUid shuttle, EntityUid station)
    {
        foreach (var dock in _docking.GetDocks(shuttle))
        {
            if (dock.Comp.DockedWith is not { } partner || !Exists(partner))
                continue;

            if (Transform(partner).GridUid is { } grid && _station.GetOwningStation(grid) == station)
                return true;
        }

        return false;
    }

    private static int SecondsLeft(DateTime expiresAt, DateTime now)
    {
        return (int)Math.Max(0, Math.Ceiling((expiresAt - now).TotalSeconds));
    }

    /// <summary>The character's name while they are online, else the account's last seen name, else a placeholder.</summary>
    private string CaptainName(Guid userId, Dictionary<Guid, string> lastSeen)
    {
        if (_player.TryGetSessionById(new NetUserId(userId), out var session))
            return SessionDisplayName(session);

        return lastSeen.TryGetValue(userId, out var name) ? name : Loc.GetString("shipyard-console-transfer-someone");
    }

    private string SessionDisplayName(ICommonSession session)
    {
        if (session.AttachedEntity is { } ent && !TerminatingOrDeleted(ent))
        {
            var name = Name(ent).Trim();
            if (name.Length > 0)
                return name;
        }

        return session.Name;
    }

    /// <summary>
    /// The card at the top of the tab: the ship on the inserted deed, how long it has been out,
    /// whether it is docked here, and which of the operator's free berths it fits. Read from the
    /// live grid, never from the cached class text, for the same reason the store itself does.
    /// </summary>
    private DrydockDeedShipInfo? BuildDeedShip(EntityUid console, EntityUid targetId, List<DrydockShip> rows, List<DrydockBerthSlot> slots)
    {
        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed)
            || deed.ShuttleUid is not { Valid: true } shuttle
            || !TryComp<MapGridComponent>(shuttle, out var map))
        {
            return null;
        }

        var sizeClass = _drydockSizes.GetSizeClass((shuttle, map));
        var hullClass = sizeClass.ToString();

        DrydockShip? row = null;
        if (TryComp<DrydockIdentityComponent>(shuttle, out var identity) && identity.ShipId != Guid.Empty)
            row = rows.FirstOrDefault(r => r.ShipGuid == identity.ShipId);

        int? minutesOut = row is { State: DrydockShipState.CheckedOut }
            ? (int)Math.Max(0, (DateTime.UtcNow - row.StateChangedAt).TotalMinutes)
            : null;

        // The same preference the store applies: the ship's own last berth if it is free and
        // fits, else the smallest free berth that fits. The dropdown lists the rest.
        var fitting = FittingFreeBerths(slots, hullClass);
        var preferred = PreferredBerth(fitting, row?.LastBerthId);

        var docked = _station.GetOwningStation(console) is { Valid: true } station && IsDockedToStation(shuttle, station);

        return new DrydockDeedShipInfo(GetFullName(deed), hullClass, minutesOut, preferred, fitting, docked);
    }

    /// <summary>The operator's free berths the hull fits, smallest class first, in the order the store's own pick walks them.</summary>
    private static List<int> FittingFreeBerths(List<DrydockBerthSlot> slots, string? hullClass)
    {
        return slots
            .Where(s => s.Occupant == null && DrydockStore.Fits(hullClass, s.Berth.MaxSizeClass))
            .OrderBy(s => DrydockStore.TryParseClass(s.Berth.MaxSizeClass, out var max) ? (int)max : int.MaxValue)
            .ThenBy(s => s.Berth.BerthId)
            .Select(s => s.Berth.BerthId)
            .ToList();
    }

    /// <summary>
    /// The berth a plain store or a reclaim lands in: the ship's own last berth when it is among
    /// the fitting ones, else the first of them, which is the smallest. Null when nothing fits.
    /// </summary>
    private static int? PreferredBerth(List<int> fitting, int? lastBerthId)
    {
        if (fitting.Count == 0)
            return null;

        return lastBerthId is { } last && fitting.Contains(last) ? last : fitting[0];
    }

    /// <summary>
    /// Fills the tab without waiting: the upstream console handlers are synchronous and the drydock
    /// lists come from the database. Called from one marked line in each.
    ///
    /// <para>Every handler that publishes a console state has to call this, not only the ones that
    /// obviously touch the drydock. <c>RefreshState</c> builds the drydock half out of the caches
    /// this fills, so publishing without kicking a read sends whatever the tab last knew - buying a
    /// ship leaves the deed card blank, because the purchase mints the deed and nothing re-reads
    /// it.</para>
    /// </summary>
    internal void KickDrydockRefresh(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled))
            return;

        _ = RefreshDrydockStateSafe(uid, component, player, uiKey);
    }

    private async Task RefreshDrydockStateSafe(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        try
        {
            await RefreshDrydockState(uid, component, player, uiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: refreshing the tab on {ToPrettyString(uid)} for {ToPrettyString(player)} threw: {e}");
        }
    }

    /// <summary>
    /// Re-fills the drydock tab on every console this account has open, wherever it is. Called
    /// when the other side of an offer acts, so the alert or the escrow row changes under them
    /// without a reopen.
    /// </summary>
    internal void KickDrydockRefreshForAccount(Guid userId)
    {
        KickDrydockRefreshWhere(actorUserId => actorUserId == userId);
    }

    /// <summary>Re-fills every open drydock tab. The expiry sweep calls this, since it does not know who was watching.</summary>
    internal void KickDrydockRefreshAll()
    {
        KickDrydockRefreshWhere(_ => true);
    }

    private void KickDrydockRefreshWhere(Func<Guid, bool> accountMatches)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled))
            return;

        var query = EntityQueryEnumerator<ShipyardConsoleComponent>();
        while (query.MoveNext(out var uid, out var console))
        {
            foreach (var key in Enum.GetValues<ShipyardConsoleUiKey>())
            {
                foreach (var viewer in _ui.GetActors(uid, key))
                {
                    if (TryComp<ActorComponent>(viewer, out var actor) && accountMatches(actor.PlayerSession.UserId.UserId))
                        KickDrydockRefresh(uid, console, viewer, key);
                }
            }
        }
    }

    /// <summary>
    /// The drydock half of the console state, read from the caches. Called by the upstream state
    /// builder so it carries one line of ours rather than a block.
    /// </summary>
    internal (List<StoredShipInfo> Ships, List<DrydockBerthInfo> Berths, Dictionary<string, int> Prices, List<DrydockTransferOfferInfo> Offers, List<DrydockCaptainInfo> Captains, Guid? DeedOwner, DrydockDeedShipInfo? DeedShip, int OfferMinutes, List<DrydockImportShipInfo> Importables, List<DrydockImpoundedShipInfo> Impounded) BuildDrydockState(EntityUid uid)
    {
        // The same floor the offer itself applies, so the prompt never promises less than an offer gets.
        var offerMinutes = (int)Math.Ceiling(Math.Max(60, _configManager.GetCVar(TriadCCVars.DrydockTransferOfferSeconds)) / 60.0);

        if (!TryComp<ShipyardConsoleComponent>(uid, out var console))
            return (new(), new(), DrydockBerthPrices(), new(), new(), null, null, offerMinutes, new(), new());

        return (console.CachedStoredShips, console.CachedBerths, DrydockBerthPrices(), console.CachedOffers, console.CachedCaptains, DeedOwnerAccount(console), console.CachedDeedShip, offerMinutes, console.CachedImportables, console.CachedImpounded);
    }

    /// <summary>
    /// The account that owns the ship on the inserted card's deed, or null when the card carries no
    /// deed to a live ship. The client compares it with its own account to draw the lockout; every
    /// message the lockout hides is refused server-side regardless, so this is presentation, and
    /// the id it exposes is already networked on the ship's ownership component.
    /// </summary>
    private Guid? DeedOwnerAccount(ShipyardConsoleComponent console)
    {
        if (console.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId
            || !TryComp<ShuttleDeedComponent>(targetId, out var deed)
            || deed.ShuttleUid is not { Valid: true } shuttle
            || !TryComp<ShipOwnershipComponent>(shuttle, out var ownership))
        {
            return null;
        }

        return ownership.OwnerUserId.UserId;
    }

    /// <summary>
    /// The account behind the click, which is the only identity any drydock verb is checked
    /// against. Deliberately not the character's mind: a mind is what goes missing when a dead
    /// player is reprinted into a body without its components, and that has stranded ships before.
    /// A session's account survives every body.
    /// </summary>
    private bool TryGetOperatorAccount(EntityUid player, out Guid userId)
    {
        userId = default;
        if (!TryComp<ActorComponent>(player, out var actor))
            return false;

        userId = actor.PlayerSession.UserId.UserId;
        return true;
    }

    /// <summary>
    /// Whether the operator is barred from the drydock the way they are barred from ship saving.
    /// Faction crews (TDF, TFA, the station roles) are issued their vessels on voucher and the
    /// drydock is the civilian garage, so the same signals decide both: the blacklist component the
    /// job stamps on the character, and the console's own save blacklist, which reads that
    /// component too. The direct check is here so a console whose blacklist a mapper forgot still
    /// refuses.
    /// </summary>
    private bool DrydockBarsOperator(EntityUid player, ShipyardConsoleComponent component)
    {
        return HasComp<ShipSavingBlacklistComponent>(player) || !IsShipSaveWhitelistValid(player, component);
    }

    /// <summary>
    /// Refuses a message whose sender does not own what it names, and writes the refusal to the
    /// timeline. The console never offers such a click, so a row here means a modified client or a
    /// forged message, which is exactly what an admin wants to see beside a stolen-card report.
    /// </summary>
    private void RefuseAccess(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid actor, Guid? shipGuid, string? shipName, Guid? ownerUserId, int? berthId, string verb)
    {
        Log.Info($"Drydock: {verb} by {ToPrettyString(player)} ({actor}) refused, not the owner of {shipName ?? shipGuid?.ToString() ?? $"berth {berthId}"}.");

        _ = WriteRefusalAsync(new DrydockAudit
        {
            ShipGuid = shipGuid,
            ShipName = shipName,
            BerthId = berthId,
            Action = DrydockAuditAction.AccessRefused,
            ActorUserId = actor,
            SubjectUserId = ownerUserId,
            RoundId = DrydockRoundId,
            Reason = verb,
        });

        ConsolePopup(player, Loc.GetString("shipyard-console-not-owner"));
        PlayDenySound(player, uid, component);
    }

    private async Task WriteRefusalAsync(DrydockAudit entry)
    {
        try
        {
            await _drydockStore.WriteAudit(entry);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: refused-access audit row could not be written: {e.Message}");
        }
    }

    /// <summary>
    /// Republishes the console's whole interface state, recomputed the way opening the console
    /// computes it. Lives here rather than in the upstream console file so the drydock tab needs no
    /// refactor of <c>RefreshState</c>'s caller-supplied arguments.
    /// </summary>
    private void RefreshDrydockUi(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        if (!TryComp<BankAccountComponent>(player, out var bank))
            return;

        var targetId = component.TargetIdSlot.ContainerSlot?.ContainedEntity;
        TryComp<ShuttleDeedComponent>(targetId, out var deed);

        var sellValue = 0;
        if (deed?.ShuttleUid is { } deedShuttle && Exists(deedShuttle))
        {
            sellValue = (int)_pricing.AppraiseGrid(deedShuttle, LacksPreserveOnSaleComp);
            sellValue = CalculateShipResaleValue((uid, component), sellValue);
        }

        RefreshState(
            uid,
            bank.Balance,
            true,
            deed != null ? GetFullName(deed) : null,
            sellValue,
            targetId,
            uiKey,
            HasComp<ShipyardVoucherComponent>(targetId));
    }

    // ---------------------------------------------------------------- Store and retrieve

    /// <summary>
    /// The console half of a store: resolve the ship from the inserted card's deed, check the
    /// operator may put it away, hand off to the pipeline, then clean up the card.
    ///
    /// <para>Ownership here is the ship's stamped account, never the deed on the card. A deed is a
    /// holder claim and cards get lent, so gating on the card would let a borrowed one file
    /// somebody else's ship into a garage they do not own.</para>
    ///
    /// <para>Returns null when this console refused before the pipeline was entered, so a caller
    /// can tell "we did not try" from "we tried and it said no". The in-progress refusal is the one
    /// exception: it is named rather than swallowed, because the pipeline that would have named it
    /// is the one holding the hull.</para>
    /// </summary>
    internal async Task<(DrydockStoreResult Result, Guid? ShipId)?> TryDrydockStore(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey, int? berthId = null)
    {
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return null;
        }

        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed) || deed.ShuttleUid is not { Valid: true } shuttleUid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return null;
        }

        if (DrydockBarsOperator(player, component))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-drydock-faction"));
            PlayDenySound(player, uid, component);
            return null;
        }

        if (!TryGetOperatorAccount(player, out var operatorAccount))
            return null;

        // No account owns it, so nobody can put it away. Not a forged message and not somebody
        // else's ship, so it is a refusal and not a timeline row.
        if (!TryComp<ShipOwnershipComponent>(shuttleUid, out var ownership))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-store-unregistered"));
            PlayDenySound(player, uid, component);
            return null;
        }

        if (ownership.OwnerUserId.UserId != operatorAccount)
        {
            // A ship that has been stored before carries its id; a new hull has none yet, and the
            // refusal is filed against the actor alone.
            Guid? knownId = TryComp<DrydockIdentityComponent>(shuttleUid, out var identity) && identity.ShipId != Guid.Empty
                ? identity.ShipId
                : null;

            RefuseAccess(uid, component, player, operatorAccount, knownId, Name(shuttleUid), ownership.OwnerUserId.UserId, null, "store");
            return null;
        }

        // The hull itself: a faction vessel carries the blacklist on its grid, and a deed bought on
        // a voucher says so. Both are what the ship-save path refuses, for the same reason.
        if (HasComp<ShipSavingBlacklistComponent>(shuttleUid))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-store-faction-ship"));
            PlayDenySound(player, uid, component);
            return null;
        }

        if (deed.PurchasedWithVoucher)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-store-voucher-ship"));
            PlayDenySound(player, uid, component);
            return null;
        }

        // A store is a hand-over at a berth. The ship has to be docked to the station this console
        // belongs to, not parked somewhere in the sector while its captain files it remotely.
        if (_station.GetOwningStation(uid) is not { Valid: true } station)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-station"));
            PlayDenySound(player, uid, component);
            return null;
        }

        // Ahead of the docked gate, because a store already in flight has undocked the hull and moved
        // it to a private map: every gate below would then refuse it for the wrong reason and tell
        // the captain their ship is not docked for the length of the store. The pipeline's own
        // re-entrancy sentinel is the marker, read here because the pipeline is only reached past
        // these gates.
        if (HasComp<DrydockInProgressComponent>(shuttleUid))
        {
            ConsolePopup(player, Loc.GetString(StoreRefusalLoc(DrydockStoreResult.InProgress)));
            PlayDenySound(player, uid, component);
            return (DrydockStoreResult.InProgress, null);
        }

        if (!IsDockedToStation(shuttleUid, station))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-store-not-docked"));
            PlayDenySound(player, uid, component);
            return null;
        }

        // This is the only layer that holds the console, the operator and the interface key at
        // once, so the pipeline is handed a delegate rather than being told about any of them. The
        // station goes with it because the store now moves the hull onto a private map before it
        // does any work, and the unwind needs somewhere to put it back that it cannot re-derive
        // from a grid sitting on that map.
        DrydockProgressCallback onProgress = (percent, _) =>
            PushDrydockProgress(uid, component, player, uiKey, DrydockProgressKind.Store, percent);

        (DrydockStoreResult Result, Guid? ShipId) result;
        try
        {
            result = await _drydock.TryStoreShip(shuttleUid, ownership.OwnerUserId.UserId, DrydockRoundId, berthId, station, onProgress);
        }
        finally
        {
            // Every path out of here runs a refresh, and a refresh publishes the cached percentage
            // to whoever opens this console next. A cancellation throws past every one of those
            // return sites, so the clear is a finally: a stale figure would otherwise sit in the
            // cache for the rest of the round and draw a busy button at an idle console.
            ClearDrydockProgress(uid, component);
        }

        // The write yielded. The store itself has already succeeded or refused; everything below is
        // the console epilogue.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return result;

        if (result.Result != DrydockStoreResult.Success)
        {
            ConsolePopup(player, Loc.GetString(StoreRefusalLoc(result.Result)));
            PlayDenySound(player, uid, component);
            await RefreshAfterRefusal(uid, component, player, uiKey);
            return result;
        }

        // The grid is gone, so the card deed now points at nothing. Strip it as the sell path does;
        // the ship's durable identity is the database row, not this card.
        if (!TerminatingOrDeleted(targetId))
            RemComp<ShuttleDeedComponent>(targetId);

        ConsolePopup(player, Loc.GetString("shipyard-console-store-success"));
        PlayConfirmSound(player, uid, component);

        await RefreshDrydockState(uid, component, player, uiKey);
        return result;
    }

    /// <summary>
    /// The console half of a retrieve: check the card can take a deed, hand off to the pipeline,
    /// then mint the deed that makes the returned ship flyable.
    ///
    /// <para>Authorization is the pipeline's, not this method's. It re-reads the row's owner and
    /// moves the row out of <see cref="DrydockShipState.Stored"/> in one conditional update, which
    /// is what makes a forged ship id and two simultaneous retrieves both safe.</para>
    /// </summary>
    internal async Task<EntityUid?> TryDrydockRetrieve(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, ShipyardConsoleUiKey uiKey)
    {
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return null;
        }

        // One ship per card is card capacity, not duplicate prevention: the row state is what stops
        // a ship existing twice. Refusing here keeps a card from carrying two claims at once.
        if (HasComp<ShuttleDeedComponent>(targetId))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-already-deeded"));
            PlayDenySound(player, uid, component);
            return null;
        }

        if (DrydockBarsOperator(player, component))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-drydock-faction"));
            PlayDenySound(player, uid, component);
            return null;
        }

        // A voucher is a claim on a new hull from the faction's list, not a card a stored ship can
        // be called in on. The ship-load path refuses it for the same reason.
        if (HasComp<ShipyardVoucherComponent>(targetId))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-retrieve-voucher-card"));
            PlayDenySound(player, uid, component);
            return null;
        }

        if (!TryGetOperatorAccount(player, out var operatorAccount))
            return null;

        if (_station.GetOwningStation(uid) is not { Valid: true } station)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-station"));
            PlayDenySound(player, uid, component);
            return null;
        }

        // The pipeline re-reads the owner and refuses on its own; this earlier read is what turns
        // a forged retrieve into a timeline row rather than a silent null.
        var header = await _drydockStore.GetShipHeader(shipId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return null;

        if (header != null && header.OwnerUserId != operatorAccount)
        {
            RefuseAccess(uid, component, player, operatorAccount, shipId, header.ShipName, header.OwnerUserId, header.BerthId, "retrieve");
            return null;
        }

        DrydockProgressCallback onProgress = (percent, _) =>
            PushDrydockProgress(uid, component, player, uiKey, DrydockProgressKind.Retrieve, percent);

        DrydockRetrieve retrieve;
        try
        {
            retrieve = await _drydock.TryRetrieveShip(shipId, operatorAccount, station, DrydockRoundId, onProgress);
        }
        finally
        {
            // Same reason as the store's: the cached figure outlives a cancellation otherwise, and
            // every refresh below would publish it to the next person at this console.
            ClearDrydockProgress(uid, component);
        }

        if (!retrieve.Succeeded)
        {
            if (!TerminatingOrDeleted(player))
            {
                ConsolePopup(player, Loc.GetString(RetrieveRefusalLoc(retrieve.Result)));
                PlayDenySound(player, uid, component);
            }

            await RefreshAfterRefusal(uid, component, player, uiKey);
            return null;
        }

        var grid = retrieve.Grid!.Value;

        // The read yielded and the card may be gone. The ship is already docked and its row is
        // checked out, so skipping the mint is recoverable through an admin: the console cannot
        // store a hull no card carries a deed to, but an impound takes it and a release puts it
        // back in a berth for nothing. Throwing out of an async void handler is not recoverable.
        if (TerminatingOrDeleted(targetId) || TerminatingOrDeleted(player))
            return grid;

        MintDeeds(targetId, grid, player);
        AddNewShuttleDeedAccessLevels(targetId, component);
        AddCompanyInformation(targetId, grid);
        AddRetrievedShuttleRecord(component, grid, player);

        // The rest of what a purchase and a ship load do for their captain, in their order (the
        // list is the ship-load path's, walked with its author): a station record on
        // the ship's own station, ship access on every door and locker, the grid-split lifecycle
        // marker, the permit items re-stamped to whoever is retrieving, the direction message,
        // and the shipyard channel hearing about it. Ownership is the drydock's own step, since
        // the row, not the card, says who owns a retrieved ship. Console locks are the drydock's
        // too, because they hold the grid uid, which only the retrieve knows.
        if (_station.GetOwningStation(grid) is { Valid: true } shipStation)
            EnsureCaptainStationRecord(shipStation, targetId, player);

        // AI cores respawn: a stored core comes back empty and does not re-offer its ghost role.
        // Called here rather than only from import, which is meant to be switched off once the
        // legacy pool drains and would have taken this with it.
        _shipLoadRespawn.RespawnMarkedEntities(grid);

        AddShipAccessToEntities(grid);
        EnsureComp<LinkedLifecycleGridParentComponent>(grid);
        _contrabandPermit.InitializePermitItemsOnGrid(grid, player);
        _shipyardDirection.SendShipDirectionMessage(player, grid);

        var gridName = Name(grid);
        SendPurchaseMessage(uid, player, gridName, component.ShipyardChannel, secret: false);
        if (component.SecretShipyardChannel is { } secretChannel)
            SendPurchaseMessage(uid, player, gridName, secretChannel, secret: true);

        ConsolePopup(player, Loc.GetString("shipyard-console-retrieve-success"));
        PlayConfirmSound(player, uid, component);

        await RefreshDrydockState(uid, component, player, uiKey);
        return grid;
    }

    // ---------------------------------------------------------------- Berths

    /// <summary>Buys a berth for the operator's own account. Money first, then the row; a row that fails after the money moved refunds it.</summary>
    internal async Task<bool> TryBuyBerth(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, string sizeClassText, ShipyardConsoleUiKey uiKey)
    {
        if (!TryComp<ActorComponent>(player, out var actor))
            return false;

        if (!DrydockStore.TryParseClass(sizeClassText, out var sizeClass) || DrydockBerthPrice(sizeClass) is var price && price <= 0)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-berth-failed"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (!_bank.TryBankWithdraw(player, price, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth }))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-berth-unaffordable", ("cost", price)));
            PlayDenySound(player, uid, component);
            return false;
        }

        var owner = actor.PlayerSession.UserId.UserId;
        try
        {
            await _drydockStore.AddBerth(owner, sizeClass, DrydockBerthKind.Purchased, price, owner, DrydockRoundId);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: berth purchase for {owner} failed after payment: {e.Message}");
            if (!TerminatingOrDeleted(player))
            {
                _bank.TryBankDeposit(player, price, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth });
                ConsolePopup(player, Loc.GetString("shipyard-console-berth-failed"));
            }

            return false;
        }

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return true;

        ConsolePopup(player, Loc.GetString("shipyard-console-berth-bought", ("class", sizeClass.ToString())));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    /// <summary>Sells one of the operator's empty berths for the configured fraction of what was paid.</summary>
    internal async Task<bool> TrySellBerth(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, int berthId, ShipyardConsoleUiKey uiKey)
    {
        if (!TryComp<ActorComponent>(player, out var actor))
            return false;

        var owner = actor.PlayerSession.UserId.UserId;
        var (outcome, berth) = await _drydockStore.TryRemoveBerth(berthId, owner, DrydockAuditAction.BerthSale, owner, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success || berth == null)
        {
            ConsolePopup(player, Loc.GetString(outcome == DrydockBerthResult.BerthOccupied
                ? "shipyard-console-berth-occupied"
                : "shipyard-console-berth-failed"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var refund = (int)(berth.PricePaid * _configManager.GetCVar(TriadCCVars.DrydockBerthRefund));
        if (refund > 0)
            _bank.TryBankDeposit(player, refund, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth });

        ConsolePopup(player, Loc.GetString("shipyard-console-berth-sold", ("refund", refund)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    /// <summary>Raises one of the operator's berths one class, charging the price difference.</summary>
    internal async Task<bool> TryUpgradeBerth(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, int berthId, ShipyardConsoleUiKey uiKey)
    {
        if (!TryComp<ActorComponent>(player, out var actor))
            return false;

        var owner = actor.PlayerSession.UserId.UserId;
        var slots = await _drydockStore.GetBerths(owner);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        var slot = slots.FirstOrDefault(s => s.Berth.BerthId == berthId);
        if (slot == null
            || !DrydockStore.TryParseClass(slot.Berth.MaxSizeClass, out var current)
            || NextSizeClass(current) is not { } next)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-berth-failed"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var delta = Math.Max(0, DrydockBerthPrice(next) - DrydockBerthPrice(current));
        if (delta > 0 && !_bank.TryBankWithdraw(player, delta, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth }))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-berth-unaffordable", ("cost", delta)));
            PlayDenySound(player, uid, component);
            return false;
        }

        var outcome = await _drydockStore.TryUpgradeBerth(berthId, owner, next, delta, owner, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success)
        {
            if (delta > 0)
                _bank.TryBankDeposit(player, delta, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth });

            ConsolePopup(player, Loc.GetString("shipyard-console-berth-failed"));
            PlayDenySound(player, uid, component);
            return false;
        }

        ConsolePopup(player, Loc.GetString("shipyard-console-berth-upgraded", ("class", next.ToString())));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    // ---------------------------------------------------------------- Transfer

    /// <summary>
    /// Opens an offer of one of the operator's stored ships to another account. The recipient has
    /// to be online right now, which is the one social gate: an offer is a conversation, not a
    /// parcel left on a doorstep. From here the offer is a persisted row with a deadline, the
    /// ship waits in escrow in its own berth, and the recipient answers from any console.
    /// </summary>
    internal async Task<bool> TryOfferTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, Guid recipient, ShipyardConsoleUiKey uiKey)
    {
        if (!TryGetOperatorAccount(player, out var owner))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-not-verified"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (recipient == owner)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-own"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var current = await _drydockStore.GetShipHeader(shipId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        // The account behind the click must own the row. The card in the slot says nothing here,
        // and this is checked before anything about the recipient so a forged offer of someone
        // else's ship lands on the timeline whoever it was addressed to.
        if (current != null && current.OwnerUserId != owner)
        {
            RefuseAccess(uid, component, player, owner, shipId, current.ShipName, current.OwnerUserId, current.BerthId, "transfer");
            return false;
        }

        if (current == null || current.State != DrydockShipState.Stored)
        {
            ConsolePopup(player, Loc.GetString(current is { State: DrydockShipState.InEscrow }
                ? "shipyard-console-transfer-busy"
                : "shipyard-console-transfer-not-yours"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (!_player.TryGetSessionById(new NetUserId(recipient), out var recipientSession))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-offline"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var seconds = Math.Max(60, _configManager.GetCVar(TriadCCVars.DrydockTransferOfferSeconds));
        var (outcome, transfer) = await _drydockStore.TryOfferTransfer(shipId, owner, recipient, TimeSpan.FromSeconds(seconds), DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success || transfer == null)
        {
            ConsolePopup(player, Loc.GetString(outcome switch
            {
                DrydockBerthResult.NoBerth or DrydockBerthResult.BerthTooSmall => "shipyard-console-transfer-recipient-full",
                DrydockBerthResult.Conflict => "shipyard-console-transfer-busy",
                _ => "shipyard-console-transfer-not-yours",
            }));
            PlayDenySound(player, uid, component);
            return false;
        }

        ConsolePopup(player, Loc.GetString("shipyard-console-transfer-offered",
            ("name", SessionDisplayName(recipientSession)), ("minutes", (int)Math.Ceiling(seconds / 60.0))));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        KickDrydockRefreshForAccount(recipient);
        return true;
    }

    /// <summary>The owner withdraws a standing offer. The ship leaves escrow; the recipient's alert goes.</summary>
    internal async Task<bool> TryCancelTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, long transferId, ShipyardConsoleUiKey uiKey)
    {
        return await TryEndTransfer(uid, component, player, transferId, DrydockTransferResolution.Cancelled, uiKey);
    }

    /// <summary>The recipient turns an offer down. The ship leaves escrow; the owner's row goes back to Stored.</summary>
    internal async Task<bool> TryDeclineTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, long transferId, ShipyardConsoleUiKey uiKey)
    {
        return await TryEndTransfer(uid, component, player, transferId, DrydockTransferResolution.Declined, uiKey);
    }

    private async Task<bool> TryEndTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, long transferId, DrydockTransferResolution resolution, ShipyardConsoleUiKey uiKey)
    {
        if (!TryGetOperatorAccount(player, out var operatorAccount))
            return false;

        var pending = await _drydockStore.GetPendingTransfer(transferId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        if (pending is not var (transfer, ship))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-none"));
            PlayDenySound(player, uid, component);
            return false;
        }

        // Cancel is the owner's verb and decline the recipient's; the console never offers the
        // other one, so the wrong party here is a forged message and goes on the timeline.
        var (rightParty, verb) = resolution == DrydockTransferResolution.Cancelled
            ? (transfer.FromUserId, "cancel offer")
            : (transfer.ToUserId, "decline offer");
        if (rightParty != operatorAccount)
        {
            RefuseAccess(uid, component, player, operatorAccount, ship.ShipGuid, ship.ShipName, ship.OwnerUserId, ship.BerthId, verb);
            return false;
        }

        var resolved = await _drydockStore.TryResolveTransfer(transferId, resolution, operatorAccount, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return resolved != null;

        if (resolved == null)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-none"));
            PlayDenySound(player, uid, component);
            return false;
        }

        ConsolePopup(player, Loc.GetString(resolution == DrydockTransferResolution.Cancelled
            ? "shipyard-console-transfer-cancelled"
            : "shipyard-console-transfer-declined"));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        KickDrydockRefreshForAccount(resolution == DrydockTransferResolution.Cancelled ? resolved.ToUserId : resolved.FromUserId);
        return true;
    }

    /// <summary>
    /// The recipient takes the ship. The store re-checks the deadline and picks the berth now,
    /// so an alert that outlived its offer, or a garage that filled up meanwhile, is a refusal
    /// with a reason rather than a ship in two places.
    /// </summary>
    internal async Task<bool> TryAcceptTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, long transferId, ShipyardConsoleUiKey uiKey)
    {
        if (!TryGetOperatorAccount(player, out var recipient))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-not-verified"));
            PlayDenySound(player, uid, component);
            return false;
        }

        // A card has to be in the slot. The tab lists against the account behind the click, not the
        // card, so this is not who the card belongs to; it is that a retrieve mints the deed onto
        // a card, and a recipient with none in cannot follow the accept with the retrieve.
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true })
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var pending = await _drydockStore.GetPendingTransfer(transferId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        if (pending is not var (transfer, ship))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-none"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (transfer.ToUserId != recipient)
        {
            RefuseAccess(uid, component, player, recipient, ship.ShipGuid, ship.ShipName, ship.OwnerUserId, ship.BerthId, "accept offer");
            return false;
        }

        var (outcome, _, acceptedName) = await _drydockStore.TryAcceptTransfer(transferId, recipient, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success || acceptedName == null)
        {
            ConsolePopup(player, Loc.GetString(outcome switch
            {
                DrydockBerthResult.NoBerth or DrydockBerthResult.BerthTooSmall => "shipyard-console-store-no-berth",
                DrydockBerthResult.WrongState or DrydockBerthResult.NotFound => "shipyard-console-transfer-gone",
                _ => "shipyard-console-transfer-failed",
            }));
            PlayDenySound(player, uid, component);
            return false;
        }

        ConsolePopup(player, Loc.GetString("shipyard-console-transfer-complete", ("ship", acceptedName)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        KickDrydockRefreshForAccount(transfer.FromUserId);
        return true;
    }

    // ---------------------------------------------------------------- Sell, rename, move

    /// <summary>The shipyard's appraisal of a live hull, as the sale path prices it. Captured at store as the scrap quote.</summary>
    public int AppraiseHull(EntityUid grid)
    {
        return (int)_pricing.AppraiseGrid(grid, LacksPreserveOnSaleComp);
    }

    /// <summary>
    /// What scrapping a stored ship pays and what each tax account takes, from the appraisal
    /// captured at store. The same arithmetic as the live sale's, so the figure on the menu is
    /// the figure that lands.
    /// </summary>
    public (int Gross, int Net, List<(SectorBankAccount Account, int Tax)> Taxes) DrydockSalePrice(Entity<ShipyardConsoleComponent> console, int appraisal)
    {
        var gross = console.Comp.IgnoreBaseSaleRate ? appraisal : (int)(appraisal * _baseSaleRate);
        gross = Math.Max(0, gross);

        var taxes = new List<(SectorBankAccount, int)>();
        var net = gross;
        foreach (var (account, coeff) in console.Comp.TaxAccounts)
        {
            var tax = CalculateSalesTax(gross, coeff);
            taxes.Add((account, tax));
            net -= tax;
        }

        return (gross, Math.Max(0, net), taxes);
    }

    /// <summary>
    /// Writes the row's name onto a retrieved hull and its grid-side deed, split the way the
    /// shipyard splits a typed name into name and suffix. Called by the retrieve pipeline before
    /// the station is recreated, since the station takes the grid's name.
    /// </summary>
    public void StampStoredName(EntityUid grid, string fullName)
    {
        fullName = fullName.Trim();
        if (fullName.Length == 0)
            return;

        if (TryComp<ShuttleDeedComponent>(grid, out var deed))
        {
            var (name, suffix) = SplitShuttleName(fullName);
            deed.ShuttleName = name;
            deed.ShuttleNameSuffix = suffix;
            Dirty(grid, deed);
        }

        _metaData.SetEntityName(grid, fullName);
    }

    /// <summary>
    /// The shipyard's own rule for telling a suffix from a name: a short last word with a dash
    /// in it is the suffix. Duplicated from the private parse in the console file rather than
    /// widened there, so the upstream file stays untouched.
    /// </summary>
    private static (string Name, string? Suffix) SplitShuttleName(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hasSuffix = parts.Length > 1 && parts[^1].Length < ShuttleDeedComponent.MaxSuffixLength && parts[^1].Contains('-');
        return hasSuffix
            ? (string.Join(' ', parts[..^1]), parts[^1])
            : (fullName, null);
    }

    /// <summary>
    /// The one shape a stored ship's new name may take. The client mirrors this for the counter
    /// and the greyed button; this is the check that counts.
    /// </summary>
    public static bool IsValidStoredShipName(string name)
    {
        if (name.Length == 0 || name.Length > ShuttleDeedComponent.MaxNameLength)
            return false;

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != ' ' && c != '-')
                return false;
        }

        return name.Trim().Length == name.Length;
    }

    /// <summary>
    /// The owner scraps a stored ship. The typed name is compared with the row's name here,
    /// exactly, which is the safety the modal exists for: the client's locked button is a
    /// convenience and this comparison is the rule. Money moves after the row is Sold, the
    /// same order as the live sale.
    /// </summary>
    internal async Task<(bool Sold, int Price, bool Paid)> TrySellStoredShip(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, string typedName, ShipyardConsoleUiKey uiKey)
    {
        if (!TryGetOperatorAccount(player, out var owner))
            return (false, 0, false);

        if (!HasComp<BankAccountComponent>(player))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-bank"));
            PlayDenySound(player, uid, component);
            return (false, 0, false);
        }

        var header = await _drydockStore.GetShipHeader(shipId);
        var appraisals = await _drydockStore.GetCurrentAppraisals(owner);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return (false, 0, false);

        if (header != null && header.OwnerUserId != owner)
        {
            RefuseAccess(uid, component, player, owner, shipId, header.ShipName, header.OwnerUserId, header.BerthId, "sell");
            return (false, 0, false);
        }

        if (header == null || header.State != DrydockShipState.Stored)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-sell-not-available"));
            PlayDenySound(player, uid, component);
            return (false, 0, false);
        }

        if (!string.Equals(typedName.Trim(), header.ShipName.Trim(), StringComparison.Ordinal))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-sell-name-mismatch"));
            PlayDenySound(player, uid, component);
            return (false, 0, false);
        }

        if (!appraisals.TryGetValue(shipId, out var appraisal) || appraisal is not { } value)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-sell-no-appraisal"));
            PlayDenySound(player, uid, component);
            return (false, 0, false);
        }

        var price = DrydockSalePrice((uid, component), value);
        var (outcome, soldName) = await _drydockStore.TrySellShip(shipId, owner, price.Net, value, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return (outcome == DrydockBerthResult.Success, price.Net, false);

        if (outcome != DrydockBerthResult.Success || soldName == null)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-sell-not-available"));
            PlayDenySound(player, uid, component);
            return (false, 0, false);
        }

        foreach (var (account, tax) in price.Taxes)
            _bank.TrySectorDeposit(account, tax, LedgerEntryType.ShipyardTax);

        var paid = price.Net <= 0 || _bank.TryBankDeposit(player, price.Net, new MarketRecord { Kind = MarketTransactionKind.ShipyardSale });
        if (!paid)
            Log.Error($"Drydock: {shipId} ({soldName}) was sold by {owner} for {price.Net} but the deposit to {ToPrettyString(player)} failed; the timeline row carries the amount.");

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} scrapped stored ship {soldName} ({shipId}) for {price.Net} credits via {ToPrettyString(uid)}");

        ConsolePopup(player, Loc.GetString("shipyard-console-sell-complete", ("ship", soldName), ("price", price.Net)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return (true, price.Net, paid);
    }

    /// <summary>
    /// The shipyard's own live sale, catching the drydock row up. Called by the upstream sell
    /// handler through one marked line after the credits have moved, with the identity read off the
    /// grid before the sale deleted it. Only a hull the drydock has filed carries an identity, so a
    /// fresh purchase sold the same round never reaches here. Without this the row goes on reading
    /// as checked out: the panel lists a scrapped ship as stranded, a plain restore hands it back on
    /// top of the credits, and a restore from sale finds no sale on file.
    /// </summary>
    internal void RecordDrydockLiveSale(Guid shipId, EntityUid player, int price, int appraisal)
    {
        if (!TryGetOperatorAccount(player, out var seller))
            return;

        _ = RecordDrydockLiveSaleAsync(shipId, seller, price, appraisal);
    }

    private async Task RecordDrydockLiveSaleAsync(Guid shipId, Guid seller, int price, int appraisal)
    {
        try
        {
            var (outcome, _) = await _drydockStore.TrySellLiveShip(shipId, seller, price, appraisal, DrydockRoundId);
            if (outcome != DrydockBerthResult.Success)
                Log.Warning($"Drydock: {shipId} was sold live at the shipyard but its row would not move to sold ({outcome}); the shipyard log carries the sale.");
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: recording the live sale of {shipId} threw: {e}");
        }
    }

    /// <summary>
    /// The owner renames a stored ship. The suffix the shipyard gave the hull survives: only the
    /// name part changes, and the hull and deed take the new full name at the next retrieve.
    /// </summary>
    internal async Task<bool> TryRenameStoredShip(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, string newName, ShipyardConsoleUiKey uiKey)
    {
        if (!TryGetOperatorAccount(player, out var owner))
            return false;

        newName = newName.Trim();
        if (!IsValidStoredShipName(newName))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-rename-invalid", ("max", ShuttleDeedComponent.MaxNameLength)));
            PlayDenySound(player, uid, component);
            return false;
        }

        var header = await _drydockStore.GetShipHeader(shipId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        if (header != null && header.OwnerUserId != owner)
        {
            RefuseAccess(uid, component, player, owner, shipId, header.ShipName, header.OwnerUserId, header.BerthId, "rename");
            return false;
        }

        if (header == null || header.State != DrydockShipState.Stored)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-rename-not-available"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var (_, suffix) = SplitShuttleName(header.ShipName);
        var fullName = suffix == null ? newName : $"{newName} {suffix}";

        var outcome = await _drydockStore.TryRenameShip(shipId, owner, fullName, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-rename-not-available"));
            PlayDenySound(player, uid, component);
            return false;
        }

        ConsolePopup(player, Loc.GetString("shipyard-console-rename-complete", ("ship", fullName)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    /// <summary>
    /// The owner moves a stored ship to another of their own empty berths that fits. The store's
    /// admin move does the work; the composite key on the ship row already refuses another
    /// owner's berth, and the ownership check here is what turns a forged move into a timeline row.
    /// </summary>
    internal async Task<bool> TryMoveStoredShip(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, int berthId, ShipyardConsoleUiKey uiKey)
    {
        if (!TryGetOperatorAccount(player, out var owner))
            return false;

        var header = await _drydockStore.GetShipHeader(shipId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        if (header != null && header.OwnerUserId != owner)
        {
            RefuseAccess(uid, component, player, owner, shipId, header.ShipName, header.OwnerUserId, header.BerthId, "move");
            return false;
        }

        if (header == null || header.State != DrydockShipState.Stored)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-move-not-available"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var outcome = await _drydockStore.TryMoveShip(shipId, berthId, owner, DrydockRoundId, "moved at the console");

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success)
        {
            ConsolePopup(player, Loc.GetString(outcome switch
            {
                DrydockBerthResult.BerthTooSmall => "shipyard-console-store-berth-too-small",
                DrydockBerthResult.BerthOccupied => "shipyard-console-berth-occupied",
                _ => "shipyard-console-move-not-available",
            }));
            PlayDenySound(player, uid, component);
            return false;
        }

        ConsolePopup(player, Loc.GetString("shipyard-console-move-complete", ("ship", header.ShipName), ("berth", berthId)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    // ---------------------------------------------------------------- The impound lot

    /// <summary>
    /// The owner pays the fee and takes an impounded ship back into one of their berths. Money
    /// first, then the row, and the money comes back on every refusal: the order a berth purchase
    /// uses. The fee charged is the one on the row when it was read, and the store refuses the move
    /// when the row's fee is any different by the time it lands, so a re-impound on new terms
    /// between the read and the press cannot be paid at the old price.
    /// </summary>
    internal async Task<bool> TryRedeemImpound(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, int berthId, ShipyardConsoleUiKey uiKey)
    {
        if (!TryGetOperatorAccount(player, out var owner))
            return false;

        if (!HasComp<BankAccountComponent>(player))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-bank"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var header = await _drydockStore.GetShipHeader(shipId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        if (header != null && header.OwnerUserId != owner)
        {
            RefuseAccess(uid, component, player, owner, shipId, header.ShipName, header.OwnerUserId, header.BerthId, "reclaim");
            return false;
        }

        if (header == null || header.State != DrydockShipState.Impounded)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-impound-not-available"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (!header.ImpoundRedeemable)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-impound-locked"));
            PlayDenySound(player, uid, component);
            return false;
        }

        // The fee goes nowhere for now. It is owed to the Triad Frontier Administration, which has
        // no account to receive it until the economy update; the transaction kind is the seam that
        // work attaches to, so the withdrawals it has to credit are findable rather than searched for.
        var fee = header.ImpoundFee;
        if (fee > 0 && !_bank.TryBankWithdraw(player, fee, new MarketRecord { Kind = MarketTransactionKind.DrydockImpound }))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-impound-unaffordable", ("fee", fee)));
            PlayDenySound(player, uid, component);
            return false;
        }

        var outcome = await _drydockStore.TryRedeemImpound(shipId, owner, berthId, fee, DrydockRoundId);

        if (outcome != DrydockBerthResult.Success)
        {
            // Nothing moved, so the money goes back to whoever is still standing there.
            if (fee > 0 && !TerminatingOrDeleted(player)
                && !_bank.TryBankDeposit(player, fee, new MarketRecord { Kind = MarketTransactionKind.DrydockImpound }))
            {
                Log.Error($"Drydock: reclaim of {shipId} by {owner} was refused ({outcome}) and the {fee} taken could not be returned to {ToPrettyString(player)}.");
            }

            if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
                return false;

            ConsolePopup(player, Loc.GetString(outcome switch
            {
                DrydockBerthResult.BerthTooSmall => "shipyard-console-store-berth-too-small",
                DrydockBerthResult.BerthOccupied => "shipyard-console-berth-occupied",
                // The row is not what the card said: re-impounded on other terms, released, or
                // locked since the read. The refreshed card says which.
                DrydockBerthResult.Conflict or DrydockBerthResult.WrongState => "shipyard-console-impound-terms-changed",
                _ => "shipyard-console-impound-failed",
            }));
            PlayDenySound(player, uid, component);
            await RefreshAfterRefusal(uid, component, player, uiKey);
            return false;
        }

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return true;

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} reclaimed impounded ship {header.ShipName} ({shipId}) into berth {berthId} for {fee} credits via {ToPrettyString(uid)}");

        ConsolePopup(player, Loc.GetString(fee > 0 ? "shipyard-console-impound-reclaimed-paid" : "shipyard-console-impound-reclaimed",
            ("ship", header.ShipName), ("berth", berthId), ("fee", fee)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    /// <summary>
    /// The owner gives up an impounded ship rather than pay for it. The typed name is the safety,
    /// compared here exactly as a sale compares it; no money moves in either direction, which is
    /// the whole difference from a sale; and a locked impound refuses, so an owner cannot end an
    /// adjudication from their side.
    /// </summary>
    internal async Task<bool> TryAbandonShip(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, string typedName, ShipyardConsoleUiKey uiKey)
    {
        if (!TryGetOperatorAccount(player, out var owner))
            return false;

        var header = await _drydockStore.GetShipHeader(shipId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        if (header != null && header.OwnerUserId != owner)
        {
            RefuseAccess(uid, component, player, owner, shipId, header.ShipName, header.OwnerUserId, header.BerthId, "abandon");
            return false;
        }

        if (header == null || header.State != DrydockShipState.Impounded)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-impound-not-available"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (!header.ImpoundRedeemable)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-impound-locked"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (!string.Equals(typedName.Trim(), header.ShipName.Trim(), StringComparison.Ordinal))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-abandon-name-mismatch"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var (outcome, abandonedName) = await _drydockStore.TryAbandonShip(shipId, owner, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success || abandonedName == null)
        {
            ConsolePopup(player, Loc.GetString(outcome == DrydockBerthResult.WrongState
                ? "shipyard-console-impound-terms-changed"
                : "shipyard-console-impound-failed"));
            PlayDenySound(player, uid, component);
            await RefreshAfterRefusal(uid, component, player, uiKey);
            return false;
        }

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} abandoned impounded ship {abandonedName} ({shipId}) via {ToPrettyString(uid)}");

        ConsolePopup(player, Loc.GetString("shipyard-console-abandon-complete", ("ship", abandonedName)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    // ---------------------------------------------------------------- Helpers

    /// <summary>
    /// The player-facing reason a store was refused. Every non-success result maps to something,
    /// so a refusal is never a button that silently does nothing.
    /// </summary>
    private static string StoreRefusalLoc(DrydockStoreResult result)
    {
        return result switch
        {
            DrydockStoreResult.OrganicsAboard => "shipyard-console-store-organics",
            DrydockStoreResult.HazardAboard => "shipyard-console-store-hazard",
            DrydockStoreResult.Disabled => "shipyard-console-store-disabled",
            DrydockStoreResult.NoBerth => "shipyard-console-store-no-berth",
            DrydockStoreResult.BerthTooSmall => "shipyard-console-store-berth-too-small",
            DrydockStoreResult.InProgress => "shipyard-console-store-in-progress",
            DrydockStoreResult.BerthOccupied => "shipyard-console-berth-occupied",
            // Not a fault of the player's and not a fault of the ship's: a round restart, a
            // shutdown, or the slice watchdog stopped a store that was already under way. The ship
            // is back where it was, so the honest message is "try again", not "it failed".
            DrydockStoreResult.Cancelled => "shipyard-console-store-cancelled",
            _ => "shipyard-console-store-failed",
        };
    }

    private static string RetrieveRefusalLoc(DrydockRetrieveResult result)
    {
        return result switch
        {
            DrydockRetrieveResult.Disabled => "shipyard-console-retrieve-disabled",
            DrydockRetrieveResult.NoStation => "shipyard-console-retrieve-no-station",
            DrydockRetrieveResult.NoStagingMap => "shipyard-console-retrieve-no-staging",
            DrydockRetrieveResult.NotFound => "shipyard-console-retrieve-not-found",
            DrydockRetrieveResult.NotOwned => "shipyard-console-not-owner",
            DrydockRetrieveResult.AlreadyOut => "shipyard-console-retrieve-already-out",
            DrydockRetrieveResult.Impounded => "shipyard-console-retrieve-impounded",
            DrydockRetrieveResult.InEscrow => "shipyard-console-retrieve-in-escrow",
            DrydockRetrieveResult.Sold => "shipyard-console-retrieve-sold",
            DrydockRetrieveResult.Destroyed => "shipyard-console-retrieve-destroyed",
            DrydockRetrieveResult.Abandoned => "shipyard-console-retrieve-abandoned",
            DrydockRetrieveResult.NotStored => "shipyard-console-retrieve-not-stored",
            DrydockRetrieveResult.NoReadableRevision => "shipyard-console-retrieve-no-revision",
            DrydockRetrieveResult.StationLost => "shipyard-console-retrieve-station-lost",
            // The store's counterpart: the pipeline was stopped mid-flight, the claim was released
            // and nothing is out, so the ship is still stored and still retrievable.
            DrydockRetrieveResult.Cancelled => "shipyard-console-retrieve-cancelled",
            _ => "shipyard-console-retrieve-failed",
        };
    }

    /// <summary>
    /// Hands the operator who pressed the button the pipeline's own percentage, and remembers it on
    /// the console so a tab opened halfway through draws the indicator instead of a live button.
    ///
    /// <para>Aimed at the actor rather than published as state: the whole point of slicing the
    /// pipeline is to stop it spending main-thread time per tick, and republishing the interface
    /// state would re-run the shuttle listing and a grid appraisal every time the figure moved. The
    /// actor overload is the <see cref="EntityUid"/> one; the session overload is client-only and
    /// returns without sending anything when called from here.</para>
    ///
    /// <para>Called synchronously from inside the pipeline's own tick slice, so it does the least
    /// it can: one cache write and one message.</para>
    /// </summary>
    private void PushDrydockProgress(EntityUid uid, ShipyardConsoleComponent component, EntityUid player,
        ShipyardConsoleUiKey uiKey, DrydockProgressKind kind, int percent)
    {
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        if (kind == DrydockProgressKind.Retrieve)
            component.CachedRetrieveProgress = percent;
        else
            component.CachedStoreProgress = percent;

        _ui.ServerSendUiMessage(uid, uiKey, new ShipyardConsoleDrydockProgressMessage(kind, percent), player);
    }

    /// <summary>
    /// Forgets whatever this console was reporting. Called from the finally around each pipeline
    /// call and again from the handler catches, because a cached percentage is published by every
    /// later state this console builds and would otherwise outlive the operation by the round.
    /// </summary>
    private void ClearDrydockProgress(EntityUid uid, ShipyardConsoleComponent component)
    {
        if (TerminatingOrDeleted(uid))
            return;

        component.CachedStoreProgress = null;
        component.CachedRetrieveProgress = null;
    }

    /// <summary>
    /// A refusal changes nothing on the server, but the client only takes its store or retrieve
    /// indicator down when a state arrives, so a refusal has to send one or the button sits on
    /// "Retrieving" until its timeout. Errors here are logged and swallowed: the refusal itself
    /// has already been delivered.
    /// </summary>
    private async Task RefreshAfterRefusal(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        try
        {
            await RefreshDrydockState(uid, component, player, uiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: state refresh after a refusal at {ToPrettyString(uid)} threw: {e.Message}");
        }
    }

    /// <summary>
    /// Mints the deeds a retrieved ship needs, mirroring the deed-assign block of the purchase
    /// path, which deeds the card and the grid both. The card the ship was stored with is generally
    /// gone by now - it was stripped at store, and rounds end - so retrieve always mints rather than
    /// looking for the old one.
    ///
    /// <para>The grid-side deed normally rides the document. A legacy import brings none, because
    /// the ship-save exporter strips <c>ShuttleDeed</c> from everything it writes, and a hull with
    /// no grid deed files no shuttle record and takes no name stamp. So the grid half is minted when
    /// the document did not bring one, and left alone when it did: an existing deed carries the
    /// owner the hull was bought under, which is not this retrieve's to rewrite.</para>
    /// </summary>
    internal void MintDeeds(EntityUid targetId, EntityUid shuttleUid, EntityUid player)
    {
        TryComp<ShuttleDeedComponent>(shuttleUid, out var gridDeed);
        var name = gridDeed != null ? GetFullName(gridDeed) : Name(shuttleUid);
        var owner = Name(player).Trim();

        var deed = EnsureComp<ShuttleDeedComponent>(targetId);
        AssignShuttleDeedProperties(deed, shuttleUid, name, owner, purchasedWithVoucher: false);
        deed.DeedHolder = targetId;

        if (gridDeed == null)
        {
            // The name is the row's, already stamped onto the grid by the retrieve. Not
            // loadedFromSave, deliberately: that flag bars a sale at any shipyard console, and a
            // retrieved hull is meant to sell like a purchased one whichever way it got here.
            gridDeed = EnsureComp<ShuttleDeedComponent>(shuttleUid);
            AssignShuttleDeedProperties(gridDeed, shuttleUid, name, owner, purchasedWithVoucher: false);
        }

        // The grid-side deed tracks which card currently holds it; retrieve's rebind left that
        // blank because the card it pointed at did not survive the store.
        gridDeed.DeedHolder = targetId;
        Dirty(shuttleUid, gridDeed);
    }

    /// <summary>
    /// Files the sector shuttle record the purchase and ship-load paths file, so a retrieved ship
    /// shows on the shuttle records console. Records are round-scoped and keyed by the live grid, so
    /// every retrieve files a fresh one. Same gate as those paths: a console that cannot transfer
    /// deeds keeps no records.
    /// </summary>
    private void AddRetrievedShuttleRecord(ShipyardConsoleComponent component, EntityUid grid, EntityUid player)
    {
        if (!component.CanTransferDeed || !TryComp<ShuttleDeedComponent>(grid, out var gridDeed))
            return;

        uint price = 0;
        if (TryComp<VesselComponent>(grid, out var vesselComp)
            && _prototypeManager.TryIndex<VesselPrototype>(vesselComp.VesselId, out var vessel))
        {
            price = (uint)vessel.Price;
        }

        _shuttleRecordsSystem.AddRecord(
            new ShuttleRecord(
                name: gridDeed.ShuttleName ?? "",
                suffix: gridDeed.ShuttleNameSuffix ?? "",
                ownerName: Name(player).Trim(),
                entityUid: GetNetEntity(grid),
                purchasedWithVoucher: false,
                loadedFromSave: false,
                purchasePrice: price));
    }

    /// <summary>
    /// Puts the captain on the ship's own station records: their existing record copied over from
    /// any station that has one, else a fresh general record from their profile. Lifted verbatim
    /// from the ship-load path, which purchase mirrors, so that retrieve does what both do.
    /// </summary>
    internal void EnsureCaptainStationRecord(EntityUid shuttleStation, EntityUid targetId, EntityUid player)
    {
        if (!TryComp<StationRecordKeyStorageComponent>(targetId, out var keyStorage) || keyStorage.Key == null)
            return;

        var recSuccess = false;
        var stationList = EntityQueryEnumerator<StationRecordsComponent>();
        while (stationList.MoveNext(out _, out _))
        {
            if (!_records.TryGetRecord<GeneralStationRecord>(keyStorage.Key.Value, out var record))
                continue;

            _records.AddRecordEntry(shuttleStation, record);
            recSuccess = true;
            break;
        }

        if (!recSuccess
            && _mind.TryGetMind(player, out _, out var mindComp)
            && mindComp.UserId != null
            && _prefManager.GetPreferences(mindComp.UserId.Value).SelectedCharacter is HumanoidCharacterProfile playerProfile
            && TryComp<StationRecordsComponent>(shuttleStation, out var stationRec))
        {
            TryComp<FingerprintComponent>(player, out var fingerprintComponent);
            TryComp<DnaComponent>(player, out var dnaComponent);

            _records.CreateGeneralRecord(
                shuttleStation,
                targetId,
                playerProfile.Name,
                playerProfile.Age,
                playerProfile.Species,
                playerProfile.Gender,
                "Captain",
                fingerprintComponent?.Fingerprint ?? string.Empty,
                dnaComponent?.DNA ?? string.Empty,
                playerProfile,
                stationRec);
        }

        // Not every station keeps records: the POI outposts a shipyard console can sit on have
        // none, and Synchronize logs an error when the component is missing.
        if (HasComp<StationRecordsComponent>(shuttleStation))
            _records.Synchronize(shuttleStation);
    }

    /// <summary>
    /// Blanks the grid-side deed's holder for the length of a store and hands back what it was.
    /// The holder is the card in the console, which is not in the ship's document, so serialized
    /// it is an invalid reference that the deserializer logs as an error on every scratch load and
    /// every retrieve. Retrieve mints a card deed and sets the holder afresh, so nothing is lost by
    /// writing it blank; the abort path puts the live value back.
    /// </summary>
    internal EntityUid? DetachGridDeedHolder(EntityUid grid)
    {
        if (!TryComp<ShuttleDeedComponent>(grid, out var deed))
            return null;

        var holder = deed.DeedHolder;
        deed.DeedHolder = null;
        Dirty(grid, deed);
        return holder;
    }

    /// <summary>The abort half of <see cref="DetachGridDeedHolder"/>.</summary>
    internal void ReattachGridDeedHolder(EntityUid grid, EntityUid? holder)
    {
        if (holder == null || !TryComp<ShuttleDeedComponent>(grid, out var deed))
            return;

        deed.DeedHolder = holder;
        Dirty(grid, deed);
    }
}
