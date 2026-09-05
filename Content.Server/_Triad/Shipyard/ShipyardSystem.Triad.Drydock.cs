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
using Content.Shared._Mono.Shipyard;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Database;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    [Dependency] private readonly DrydockSystem _drydock = default!;
    [Dependency] private readonly DrydockStore _drydockStore = default!;
    [Dependency] private readonly ShipSizeSystem _drydockSizes = default!;

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

        var owner = ownership.OwnerUserId.UserId;
        var sizeClass = _drydockSizes.GetSizeClass((ev.Shuttle, map));
        var price = DrydockBerthPrice(sizeClass);
        var voucher = TryComp<ShuttleDeedComponent>(ev.Shuttle, out var deed) && deed.PurchasedWithVoucher;

        var paid = 0;
        var kind = DrydockBerthKind.Granted;
        if (!voucher && price > 0)
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

    // ---------------------------------------------------------------- State

    /// <summary>
    /// Fills the console's drydock caches for whoever is operating it, then re-publishes the
    /// interface state so the drydock tab shows the fresh lists. The caches exist because the
    /// state builder is synchronous and these come from the database.
    /// </summary>
    internal async Task RefreshDrydockState(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        component.CachedStoredShips = new();
        component.CachedBerths = new();
        component.CachedDeedShip = null;
        component.CachedOffers = new();
        component.CachedCaptains = new();

        // No card, no account to list against: the drydock tab is per-operator, and an empty list
        // is the honest answer rather than everything the console has ever seen.
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId
            || !TryComp<ActorComponent>(player, out var actor))
        {
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
        // the picker can grey the captains with nowhere to put the ship. Read in one query.
        var online = _player.Sessions.Where(s => s.UserId.UserId != owner).ToList();
        var freeClasses = await _drydockStore.GetFreeBerthClasses(online.Select(s => s.UserId.UserId));
        var names = await _drydockStore.GetPlayerNames(offersOut.Values.Select(t => t.ToUserId).Concat(offersIn.Select(o => o.Transfer.FromUserId)));

        // The console or the operator may have gone during the reads.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        // Every hull the account has, including the ones that are out: the tab warns when an
        // action would leave a ship with nowhere to dock. A ship under investigation is hidden,
        // and retrieve refuses it regardless.
        component.CachedStoredShips = rows
            .Where(r => !r.Investigating)
            .Select(r => new StoredShipInfo(r.ShipGuid, r.ShipName, r.SizeClass, r.State.ToString(), r.BerthId))
            .ToList();

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

            component.CachedBerths.Add(new DrydockBerthInfo(
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

            component.CachedOffers.Add(new DrydockTransferOfferInfo(
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
            component.CachedCaptains.Add(new DrydockCaptainInfo(id, SessionDisplayName(session), freeClasses.GetValueOrDefault(id) ?? new List<string>()));
        }

        component.CachedDeedShip = BuildDeedShip(targetId, rows, slots);
        RefreshDrydockUi(uid, component, player, uiKey);
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
    /// and which of the operator's free berths it fits. Read from the live grid, never from the
    /// cached class text, for the same reason the store itself does.
    /// </summary>
    private DrydockDeedShipInfo? BuildDeedShip(EntityUid targetId, List<DrydockShip> rows, List<DrydockBerthSlot> slots)
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
        var fitting = slots
            .Where(s => s.Occupant == null && DrydockStore.Fits(hullClass, s.Berth.MaxSizeClass))
            .OrderBy(s => DrydockStore.TryParseClass(s.Berth.MaxSizeClass, out var max) ? (int)max : int.MaxValue)
            .ThenBy(s => s.Berth.BerthId)
            .Select(s => s.Berth.BerthId)
            .ToList();

        int? preferred = row?.LastBerthId is { } last && fitting.Contains(last) ? last : fitting.FirstOrDefault();
        if (fitting.Count == 0)
            preferred = null;

        return new DrydockDeedShipInfo(GetFullName(deed), hullClass, minutesOut, preferred, fitting);
    }

    /// <summary>
    /// Fills the tab without waiting: the upstream open and card-slot handlers are synchronous and
    /// the drydock lists come from the database. Called from one marked line in each.
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
    internal (List<StoredShipInfo> Ships, List<DrydockBerthInfo> Berths, Dictionary<string, int> Prices, List<DrydockTransferOfferInfo> Offers, List<DrydockCaptainInfo> Captains, Guid? DeedOwner, DrydockDeedShipInfo? DeedShip, int OfferMinutes) BuildDrydockState(EntityUid uid)
    {
        // The same floor the offer itself applies, so the prompt never promises less than an offer gets.
        var offerMinutes = (int)Math.Ceiling(Math.Max(60, _configManager.GetCVar(TriadCCVars.DrydockTransferOfferSeconds)) / 60.0);

        if (!TryComp<ShipyardConsoleComponent>(uid, out var console))
            return (new(), new(), DrydockBerthPrices(), new(), new(), null, null, offerMinutes);

        return (console.CachedStoredShips, console.CachedBerths, DrydockBerthPrices(), console.CachedOffers, console.CachedCaptains, DeedOwnerAccount(console), console.CachedDeedShip, offerMinutes);
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
    /// can tell "we did not try" from "we tried and it said no".</para>
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

        if (!TryGetOperatorAccount(player, out var operatorAccount))
            return null;

        if (!TryComp<ShipOwnershipComponent>(shuttleUid, out var ownership)
            || ownership.OwnerUserId.UserId != operatorAccount)
        {
            // A ship that has been stored before carries its id; a new hull has none yet, and the
            // refusal is filed against the actor alone.
            Guid? knownId = TryComp<DrydockIdentityComponent>(shuttleUid, out var identity) && identity.ShipId != Guid.Empty
                ? identity.ShipId
                : null;

            RefuseAccess(uid, component, player, operatorAccount, knownId, Name(shuttleUid), ownership?.OwnerUserId.UserId, null, "store");
            return null;
        }

        var result = await _drydock.TryStoreShip(shuttleUid, ownership.OwnerUserId.UserId, DrydockRoundId, berthId);

        // The write yielded. The store itself has already succeeded or refused; everything below is
        // the console epilogue.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return result;

        if (result.Result != DrydockStoreResult.Success)
        {
            ConsolePopup(player, Loc.GetString(StoreRefusalLoc(result.Result)));
            PlayDenySound(player, uid, component);
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

        var grid = await _drydock.TryRetrieveShip(shipId, operatorAccount, station, DrydockRoundId);

        if (grid is null)
        {
            if (!TerminatingOrDeleted(player))
            {
                ConsolePopup(player, Loc.GetString("shipyard-console-retrieve-failed"));
                PlayDenySound(player, uid, component);
            }

            return null;
        }

        // The read yielded and the card may be gone. The ship is already docked and its row is
        // checked out, so skipping the mint is recoverable - the owner stores it and retrieves
        // again - where throwing out of an async void handler is not.
        if (TerminatingOrDeleted(targetId) || TerminatingOrDeleted(player))
            return grid;

        MintCardDeed(targetId, grid.Value, player);
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

        if (current == null || current.State != DrydockShipState.Stored || current.Investigating)
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

        // The recipient's own card has to be in the slot: it is how the tab lists against their
        // account, and it is what they will mint the deed onto when they retrieve.
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

        var (outcome, _, accepted) = await _drydockStore.TryAcceptTransfer(transferId, recipient, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success || accepted == null)
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

        ConsolePopup(player, Loc.GetString("shipyard-console-transfer-complete", ("ship", accepted.ShipName)));
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

        if (header == null || header.State != DrydockShipState.Stored || header.Investigating)
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
        var (outcome, sold) = await _drydockStore.TrySellShip(shipId, owner, price.Net, value, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return (outcome == DrydockBerthResult.Success, price.Net, false);

        if (outcome != DrydockBerthResult.Success || sold == null)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-sell-not-available"));
            PlayDenySound(player, uid, component);
            return (false, 0, false);
        }

        foreach (var (account, tax) in price.Taxes)
            _bank.TrySectorDeposit(account, tax, LedgerEntryType.ShipyardTax);

        var paid = price.Net <= 0 || _bank.TryBankDeposit(player, price.Net, new MarketRecord { Kind = MarketTransactionKind.ShipyardSale });
        if (!paid)
            Log.Error($"Drydock: {sold.ShipGuid} ({sold.ShipName}) was sold by {owner} for {price.Net} but the deposit to {ToPrettyString(player)} failed; the timeline row carries the amount.");

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} scrapped stored ship {sold.ShipName} ({sold.ShipGuid}) for {price.Net} credits via {ToPrettyString(uid)}");

        ConsolePopup(player, Loc.GetString("shipyard-console-sell-complete", ("ship", sold.ShipName), ("price", price.Net)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return (true, price.Net, paid);
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

        if (header == null || header.State != DrydockShipState.Stored || header.Investigating)
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

        if (header == null || header.State != DrydockShipState.Stored || header.Investigating)
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
            _ => "shipyard-console-store-failed",
        };
    }

    /// <summary>
    /// Mints a fresh card-side deed for a retrieved ship, mirroring the deed-assign block of the
    /// purchase path. The card the ship was stored with is generally gone by now - it was stripped
    /// at store, and rounds end - so retrieve always mints rather than looking for the old one.
    /// </summary>
    internal void MintCardDeed(EntityUid targetId, EntityUid shuttleUid, EntityUid player)
    {
        TryComp<ShuttleDeedComponent>(shuttleUid, out var gridDeed);
        var name = gridDeed != null ? GetFullName(gridDeed) : Name(shuttleUid);
        var owner = Name(player).Trim();

        var deed = EnsureComp<ShuttleDeedComponent>(targetId);
        AssignShuttleDeedProperties(deed, shuttleUid, name, owner, purchasedWithVoucher: false);
        deed.DeedHolder = targetId;

        // The grid-side deed tracks which card currently holds it; retrieve's rebind left that
        // blank because the card it pointed at did not survive the store.
        if (gridDeed != null)
            gridDeed.DeedHolder = targetId;
    }
}
