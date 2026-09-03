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
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.Map.Components;
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
            await TryDrydockStore(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
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
            await TryOfferTransfer(uid, component, player, args.ShipId, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: transfer offer at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
        }
    }

    private async void OnCancelTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleCancelTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryCancelTransfer(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: transfer cancel at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
        }
    }

    private async void OnAcceptTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleAcceptTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        try
        {
            await TryAcceptTransfer(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: transfer accept at {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
            if (!TerminatingOrDeleted(player))
                ConsolePopup(player, Loc.GetString("shipyard-console-transfer-failed"));
        }
    }

    /// <summary>An offer dies with the console session that made it, so a walk-away never leaves a live offer for a stranger.</summary>
    private void OnConsoleUIClosed(EntityUid uid, ShipyardConsoleComponent component, BoundUIClosedEvent args)
    {
        if (component.PendingTransfer is not { } offer)
            return;

        if (TryComp<ActorComponent>(args.Actor, out var actor) && actor.PlayerSession.UserId.UserId == offer.OwnerUserId)
            component.PendingTransfer = null;
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

        // No card, no account to list against: the drydock tab is per-operator, and an empty list
        // is the honest answer rather than everything the console has ever seen.
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true }
            || !TryComp<ActorComponent>(player, out var actor))
        {
            RefreshDrydockUi(uid, component, player, uiKey);
            return;
        }

        var owner = actor.PlayerSession.UserId.UserId;
        var rows = await _drydockStore.GetShipsByOwner(owner);
        var slots = await _drydockStore.GetBerths(owner);

        // The console or the operator may have gone during the reads.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        // Every hull the account has, including the ones that are out: the player sees why a
        // berth is empty, and the tab can warn when an action would leave a ship with nowhere to
        // dock. A ship under investigation is hidden, and retrieve refuses it regardless.
        component.CachedStoredShips = rows
            .Where(r => !r.Investigating)
            .Select(r => new StoredShipInfo(r.ShipGuid, r.ShipName, r.SizeClass, r.State.ToString(), r.BerthId))
            .ToList();

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

            component.CachedBerths.Add(new DrydockBerthInfo(
                slot.Berth.BerthId,
                slot.Berth.MaxSizeClass,
                (int)(slot.Berth.PricePaid * refund),
                upgradePrice,
                upgradeClass,
                slot.Occupant?.ShipGuid,
                slot.Occupant?.ShipName));
        }

        RefreshDrydockUi(uid, component, player, uiKey);
    }

    /// <summary>
    /// The drydock half of the console state, read from the caches and the pending offer. Called
    /// by the upstream state builder so it carries one line of ours rather than a block.
    /// </summary>
    internal (List<StoredShipInfo> Ships, List<DrydockBerthInfo> Berths, Dictionary<string, int> Prices, DrydockTransferOfferInfo? Offer, Guid? DeedOwner) BuildDrydockState(EntityUid uid)
    {
        if (!TryComp<ShipyardConsoleComponent>(uid, out var console))
            return (new(), new(), DrydockBerthPrices(), null, null);

        DrydockTransferOfferInfo? offer = null;
        if (console.PendingTransfer is { } pending)
        {
            var left = (int)Math.Ceiling((pending.ExpiresAt - _timing.CurTime).TotalSeconds);
            if (left <= 0)
                console.PendingTransfer = null;
            else
                offer = new DrydockTransferOfferInfo(pending.ShipId, pending.ShipName, pending.SizeClass, pending.OwnerName, pending.OwnerUserId, left);
        }

        return (console.CachedStoredShips, console.CachedBerths, DrydockBerthPrices(), offer, DeedOwnerAccount(console));
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
    internal async Task<(DrydockStoreResult Result, Guid? ShipId)?> TryDrydockStore(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
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

        var result = await _drydock.TryStoreShip(shuttleUid, ownership.OwnerUserId.UserId, DrydockRoundId);

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

    internal async Task<bool> TryOfferTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, ShipyardConsoleUiKey uiKey)
    {
        if (!TryGetOperatorAccount(player, out var owner))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-not-verified"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (component.PendingTransfer != null && component.PendingTransfer.ExpiresAt > _timing.CurTime)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-busy"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var current = await _drydockStore.GetShipHeader(shipId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        // The account behind the click must own the row. The card in the slot says nothing here.
        if (current != null && current.OwnerUserId != owner)
        {
            RefuseAccess(uid, component, player, owner, shipId, current.ShipName, current.OwnerUserId, current.BerthId, "transfer");
            return false;
        }

        if (current == null || current.State != DrydockShipState.Stored || current.Investigating)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-not-yours"));
            PlayDenySound(player, uid, component);
            return false;
        }

        var seconds = Math.Max(5, _configManager.GetCVar(TriadCCVars.DrydockTransferOfferSeconds));
        component.PendingTransfer = new DrydockTransferOffer
        {
            ShipId = shipId,
            ShipName = current.ShipName,
            SizeClass = current.SizeClass,
            OwnerUserId = owner,
            OwnerName = Name(player).Trim(),
            ExpiresAt = _timing.CurTime + TimeSpan.FromSeconds(seconds),
        };

        ConsolePopup(player, Loc.GetString("shipyard-console-transfer-offered", ("seconds", seconds)));
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    internal Task<bool> TryCancelTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        if (component.PendingTransfer is not { } offer)
            return Task.FromResult(false);

        // Only the offerer withdraws a live offer; anyone may clear a lapsed one.
        var lapsed = offer.ExpiresAt <= _timing.CurTime;
        if (!lapsed && (!TryComp<ActorComponent>(player, out var actor) || actor.PlayerSession.UserId.UserId != offer.OwnerUserId))
            return Task.FromResult(false);

        component.PendingTransfer = null;
        return RefreshDrydockState(uid, component, player, uiKey).ContinueWith(_ => true, TaskScheduler.Default);
    }

    internal async Task<bool> TryAcceptTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        if (component.PendingTransfer is not { } offer || offer.ExpiresAt <= _timing.CurTime)
        {
            component.PendingTransfer = null;
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-none"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (!TryGetOperatorAccount(player, out var recipient))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-not-verified"));
            PlayDenySound(player, uid, component);
            return false;
        }

        if (recipient == offer.OwnerUserId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-transfer-own"));
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

        var (outcome, _) = await _drydockStore.TryTransferShip(offer.ShipId, offer.OwnerUserId, recipient, DrydockRoundId,
            $"transferred at the console to {Name(player).Trim()}");

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success)
        {
            ConsolePopup(player, Loc.GetString(outcome switch
            {
                DrydockBerthResult.NoBerth => "shipyard-console-store-no-berth",
                DrydockBerthResult.BerthTooSmall => "shipyard-console-store-berth-too-small",
                DrydockBerthResult.WrongState or DrydockBerthResult.NotFound => "shipyard-console-transfer-gone",
                _ => "shipyard-console-transfer-failed",
            }));
            PlayDenySound(player, uid, component);
            return false;
        }

        component.PendingTransfer = null;
        ConsolePopup(player, Loc.GetString("shipyard-console-transfer-complete", ("ship", offer.ShipName)));
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
