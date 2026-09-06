using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._NF.Bank;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Market;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Shared._NF.Bank.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock.Admin;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Eui;
using Content.Shared.Preferences;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Network;

namespace Content.Server._Triad.Drydock.Admin;

/// <summary>
/// The drydock admin panel. Same shape as the tamper audit panel: the EUI owns the filter and the
/// selection, every action goes through the store with the acting admin and the current round on
/// the audit row, and the whole state is rebuilt and pushed after each one so the panel never shows
/// a decision that did not happen.
///
/// <para>Whether a ship is lost is the admin's call and nothing here decides it. The one guard is
/// the system's: a hull whose grid is still in the world cannot be restored, because that would
/// be a duplicate rather than a recovery.</para>
/// </summary>
public sealed partial class DrydockAdminEui : BaseEui
{
    [Dependency] private DrydockStore _store = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IEntityManager _entMan = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IServerPreferencesManager _prefs = default!;
    [Dependency] private IServerDbManager _db = default!;

    private const int DefaultPageSize = 50;

    private int _page;
    private int _pageSize = DefaultPageSize;
    private string? _search;
    private DrydockShipState? _stateFilter;
    private bool _strandedOnly;
    private bool _investigatingOnly;

    private Guid? _selected;
    private string? _notice;

    private DrydockAdminEuiState _state = new();

    public DrydockAdminEui()
    {
        IoCManager.InjectDependencies(this);
    }

    public override void Opened()
    {
        base.Opened();
        _ = RefreshAsync();
    }

    public override EuiStateBase GetNewState()
    {
        return _state;
    }

    private int CurrentRoundId()
    {
        return _entMan.System<GameTicker>().RoundId;
    }

    private int? RoundForAudit()
    {
        var round = CurrentRoundId();
        return round > 0 ? round : null;
    }

    private Guid AdminId => Player.UserId.UserId;

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        switch (msg)
        {
            case DrydockAdminRequestPageMessage req:
                _page = Math.Max(0, req.Page);
                _pageSize = Math.Clamp(req.PageSize, 1, 500);
                _search = string.IsNullOrWhiteSpace(req.Search) ? null : req.Search.Trim();
                _stateFilter = null;
                _strandedOnly = false;
                _investigatingOnly = false;
                switch (req.Chip)
                {
                    case "Stranded":
                        _strandedOnly = true;
                        break;
                    case "Investigating":
                        _investigatingOnly = true;
                        break;
                    default:
                        _stateFilter = Enum.TryParse<DrydockShipState>(req.Chip, out var state) ? state : null;
                        break;
                }
                _ = RefreshAsync();
                break;

            case DrydockAdminSelectShipMessage select:
                _selected = select.ShipGuid;
                _ = RefreshAsync();
                break;

            case DrydockAdminHoldMessage hold:
                _ = Act(async () =>
                {
                    if (hold.Hold)
                    {
                        var held = await _store.TrySetState(hold.ShipGuid, null, DrydockShipState.Held, DrydockAuditAction.Hold, AdminId, RoundForAudit(), hold.Reason);
                        return held ? "Ship held." : "Already held, or unknown ship.";
                    }

                    // Back to wherever the hold found it. A ship held while out is still out.
                    return await _store.TryReleaseHold(hold.ShipGuid, AdminId, RoundForAudit(), hold.Reason) switch
                    {
                        DrydockShipState.CheckedOut => "Hold released; the ship is still out.",
                        DrydockShipState.Stored => "Hold released; the ship is stored again.",
                        _ => "Not held, so nothing to release.",
                    };
                });
                break;

            case DrydockAdminInvestigateMessage inv:
                _ = Act(async () =>
                {
                    var changed = await _store.SetInvestigating(inv.ShipGuid, inv.Investigating, AdminId, RoundForAudit(), inv.Reason);
                    if (changed)
                        KickConsoles();
                    return changed
                        ? (inv.Investigating ? "Investigation opened; retrieve is refused and any standing offer is withdrawn." : "Investigation closed.")
                        : "No change.";
                });
                break;

            case DrydockAdminCancelOfferMessage cancel:
                _ = Act(async () =>
                {
                    var resolved = await _store.TryResolveTransfer(cancel.TransferId, DrydockTransferResolution.Cancelled, AdminId, RoundForAudit(),
                        adminOverride: true, reason: string.IsNullOrWhiteSpace(cancel.Reason) ? "cancelled by admin" : cancel.Reason);
                    if (resolved != null)
                        KickConsoles();
                    return resolved != null ? "Offer withdrawn; the ship is stored again." : "No standing offer by that id.";
                });
                break;

            case DrydockAdminNotesMessage notes:
                _ = Act(async () =>
                {
                    await _store.SetAdminNotes(notes.ShipGuid, string.IsNullOrWhiteSpace(notes.Notes) ? null : notes.Notes);
                    return "Notes saved.";
                });
                break;

            case DrydockAdminRestoreMessage restore:
                _ = Act(async () =>
                {
                    var reason = string.IsNullOrWhiteSpace(restore.Reason) ? "restored by admin" : restore.Reason;
                    var outcome = await _entMan.System<DrydockSystem>().TryAdminRestore(restore.ShipGuid, restore.BerthId, AdminId, RoundForAudit(), reason);
                    if (outcome == DrydockBerthResult.Success)
                        KickConsoles();
                    return RestoreOutcomeText(outcome);
                });
                break;

            case DrydockAdminRestoreFromSaleMessage undo:
                _ = Act(() => RestoreFromSale(undo));
                break;

            case DrydockAdminMoveMessage move:
                _ = Act(async () =>
                {
                    var outcome = await _store.TryMoveShip(move.ShipGuid, move.BerthId, AdminId, RoundForAudit(), move.Reason);
                    if (outcome == DrydockBerthResult.Success)
                        KickConsoles();
                    return outcome switch
                    {
                        DrydockBerthResult.Success => move.BerthId is null ? "Berth vacated." : "Ship moved.",
                        DrydockBerthResult.WrongState => "Refused: only a stored ship can be moved into a berth.",
                        DrydockBerthResult.BerthTooSmall => "Refused: that berth cannot hold this hull.",
                        DrydockBerthResult.BerthOccupied => "Refused: that berth is occupied.",
                        _ => $"Refused: {outcome}.",
                    };
                });
                break;

            case DrydockAdminGrantBerthMessage grant:
                _ = Act(async () =>
                {
                    if (!DrydockStore.TryParseClass(grant.MaxSizeClass, out var sizeClass))
                        return "Refused: not a size class.";

                    var id = await _store.AddBerth(grant.OwnerUserId, sizeClass, DrydockBerthKind.Granted, 0, AdminId, RoundForAudit());
                    KickConsoles();
                    return $"Granted {sizeClass} berth #{id}.";
                });
                break;

            case DrydockAdminDeleteBerthMessage delBerth:
                _ = Act(async () =>
                {
                    var (outcome, _) = await _store.TryRemoveBerth(delBerth.BerthId, null, DrydockAuditAction.BerthDelete, AdminId, RoundForAudit());
                    if (outcome == DrydockBerthResult.Success)
                        KickConsoles();
                    return outcome switch
                    {
                        DrydockBerthResult.Success => $"Berth #{delBerth.BerthId} deleted.",
                        DrydockBerthResult.BerthOccupied => "Refused: the berth holds a ship. Move it first.",
                        _ => $"Refused: {outcome}.",
                    };
                });
                break;

            case DrydockAdminPromoteRevisionMessage promote:
                _ = Act(async () =>
                {
                    var keep = _cfg.GetCVar(TriadCCVars.DrydockKeepBlobs);
                    var (outcome, revision) = await _store.TryPromoteRevision(promote.ShipGuid, promote.Revision, AdminId, RoundForAudit(), promote.Reason, keep);
                    return outcome == DrydockBerthResult.Success
                        ? $"Revision {promote.Revision} promoted as revision {revision}."
                        : "Refused: that revision has no document left to promote.";
                });
                break;

            case DrydockAdminDeleteShipMessage delShip:
                _ = Act(async () =>
                {
                    if (_entMan.System<DrydockSystem>().IsShipLive(delShip.ShipGuid))
                        return "Refused: a live grid still carries this hull this round.";

                    var outcome = await _store.TryDeleteShip(delShip.ShipGuid, AdminId, RoundForAudit(), delShip.Reason);
                    if (outcome == DrydockBerthResult.Success && _selected == delShip.ShipGuid)
                        _selected = null;

                    return outcome == DrydockBerthResult.Success
                        ? "Ship record deleted. Its timeline is kept."
                        : $"Refused: {outcome}.";
                });
                break;
        }
    }

    private static string RestoreOutcomeText(DrydockBerthResult outcome)
    {
        return outcome switch
        {
            DrydockBerthResult.Success => "Ship restored to the drydock.",
            DrydockBerthResult.WrongState => "Refused: the ship is already stored, or a live grid still carries it this round.",
            DrydockBerthResult.BerthTooSmall => "Refused: that berth cannot hold this hull. Grant a larger one.",
            DrydockBerthResult.BerthOccupied => "Refused: that berth is occupied.",
            DrydockBerthResult.NotFound => "Refused: unknown ship, or the berth is not the owner's.",
            _ => $"Refused: {outcome}.",
        };
    }

    /// <summary>
    /// Undoes a sale. Money first, because a refusal there must leave the ship sold; then the
    /// restore, which the live-grid guard can still refuse, in which case the money goes back.
    /// Either way the timeline says what happened to the credits.
    /// </summary>
    private async Task<string> RestoreFromSale(DrydockAdminRestoreFromSaleMessage undo)
    {
        var header = await _store.GetShipHeader(undo.ShipGuid);
        if (header is not { State: DrydockShipState.Sold })
            return "Refused: that ship is not sold.";

        var sale = await _store.GetLastSale(undo.ShipGuid);
        var took = 0;
        if (undo.TakeMoneyBack)
        {
            if (sale == null)
                return "Refused: no sale price is on file for this ship. Untick taking the money back and give a reason.";

            if (!await TryTakeBack(header.OwnerUserId, sale.Value.Price))
                return $"Refused: the owner's balance cannot cover {sale.Value.Price}. Untick taking the money back and give a reason.";

            took = sale.Value.Price;
        }
        else if (string.IsNullOrWhiteSpace(undo.Reason))
        {
            return "Refused: leaving the money with the owner needs a reason.";
        }

        var reason = string.IsNullOrWhiteSpace(undo.Reason) ? "sale reversed by admin" : undo.Reason;
        var outcome = await _entMan.System<DrydockSystem>().TryAdminRestore(undo.ShipGuid, undo.BerthId, AdminId, RoundForAudit(), reason);
        if (outcome != DrydockBerthResult.Success)
        {
            if (took > 0 && !await TryGiveBack(header.OwnerUserId, took))
                return $"{RestoreOutcomeText(outcome)} {took} was taken from the owner and could NOT be returned automatically; fix their balance by hand.";

            return RestoreOutcomeText(outcome);
        }

        await _store.WriteAudit(new DrydockAudit
        {
            ShipGuid = undo.ShipGuid,
            ShipName = header.ShipName,
            BerthId = undo.BerthId,
            Action = DrydockAuditAction.SaleReversed,
            ActorUserId = AdminId,
            SubjectUserId = header.OwnerUserId,
            Revision = header.CurrentRevision,
            RoundId = RoundForAudit(),
            Reason = took > 0 ? $"took back {took}: {reason}" : $"money left with the owner: {reason}",
            CreatedAt = DateTime.UtcNow,
        });

        KickConsoles();
        return took > 0 ? $"Sale reversed; {took} taken back from the owner." : "Sale reversed; the owner keeps the money.";
    }

    // ---------------------------------------------------------------- Money

    /// <summary>
    /// The owner's balance as the bank sees it: the live component while they are in a body,
    /// their selected character's saved balance otherwise. Null when neither can be read.
    /// </summary>
    private async Task<int?> OwnerBalance(Guid owner)
    {
        if (TryOwnerEntity(owner, out var ent) && _entMan.TryGetComponent<BankAccountComponent>(ent, out var bank))
            return bank.Balance;

        var prefs = await LoadPrefs(owner);
        return (prefs?.SelectedCharacter as HumanoidCharacterProfile)?.BankBalance;
    }

    private async Task<bool> TryTakeBack(Guid owner, int amount)
    {
        var bank = _entMan.System<BankSystem>();
        if (TryOwnerEntity(owner, out var ent) && _entMan.HasComponent<BankAccountComponent>(ent))
            return bank.TryBankWithdraw(ent, amount, new MarketRecord { Kind = MarketTransactionKind.AdminAdjust });

        var prefs = await LoadPrefs(owner);
        if (prefs?.SelectedCharacter is not HumanoidCharacterProfile profile)
            return false;

        return await bank.TryBankWithdrawOffline(new NetUserId(owner), prefs, profile, amount);
    }

    private async Task<bool> TryGiveBack(Guid owner, int amount)
    {
        var bank = _entMan.System<BankSystem>();
        if (TryOwnerEntity(owner, out var ent) && _entMan.HasComponent<BankAccountComponent>(ent))
            return bank.TryBankDeposit(ent, amount, new MarketRecord { Kind = MarketTransactionKind.AdminAdjust });

        var prefs = await LoadPrefs(owner);
        if (prefs?.SelectedCharacter is not HumanoidCharacterProfile profile)
            return false;

        return await bank.TryBankDepositOffline(new NetUserId(owner), prefs, profile, amount);
    }

    private bool TryOwnerEntity(Guid owner, out EntityUid entity)
    {
        entity = default;
        if (!_players.TryGetSessionById(new NetUserId(owner), out var session) || session.AttachedEntity is not { } ent || !_entMan.EntityExists(ent))
            return false;

        entity = ent;
        return true;
    }

    /// <summary>Cached preferences when the account has been seen this boot, else the saved ones.</summary>
    private async Task<PlayerPreferences?> LoadPrefs(Guid owner)
    {
        var id = new NetUserId(owner);
        if (_prefs.TryGetCachedPreferences(id, out var cached))
            return cached;

        return await _db.GetPlayerPreferencesAsync(id, CancellationToken.None);
    }

    /// <summary>Every open drydock tab re-reads after an admin action, so the player sees it without reopening.</summary>
    private void KickConsoles()
    {
        _entMan.System<ShipyardSystem>().KickDrydockRefreshAll();
    }

    // ---------------------------------------------------------------- State

    /// <summary>Runs one admin action, records its outcome for the footer, and rebuilds the panel.</summary>
    private async Task Act(Func<Task<string>> action)
    {
        try
        {
            _notice = await action();
        }
        catch (Exception e)
        {
            _notice = $"Failed: {e.Message}";
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var round = CurrentRoundId();

        var filter = new DrydockShipFilter(null, null, null, _stateFilter, _strandedOnly, round, _search, _investigatingOnly);
        var (rows, total) = await _store.QueryShips(filter, _page, _pageSize);
        var offers = await _store.GetPendingOffersForShips(rows.Select(r => r.ShipGuid));

        var detail = _selected is { } selected ? await _store.GetShipDetail(selected) : null;
        if (_selected != null && detail == null)
            _selected = null;

        var berths = detail != null ? await _store.GetBerths(detail.Ship.OwnerUserId) : new List<DrydockBerthSlot>();

        DrydockTransfer? escrow = null;
        List<DrydockBerthSlot> recipientBerths = new();
        (int Price, DateTime At)? lastSale = null;
        int? ownerBalance = null;
        if (detail != null)
        {
            if (detail.Ship.State == DrydockShipState.InEscrow)
            {
                escrow = await _store.GetPendingOfferForShip(detail.Ship.ShipGuid);
                if (escrow != null)
                    recipientBerths = await _store.GetBerths(escrow.ToUserId);
            }

            if (detail.Ship.State == DrydockShipState.Sold)
            {
                lastSale = await _store.GetLastSale(detail.Ship.ShipGuid);
                ownerBalance = await OwnerBalance(detail.Ship.OwnerUserId);
            }
        }

        // Names, resolved once for everything on screen. Online sessions win; the player table
        // answers for everyone else.
        var ids = new HashSet<Guid>();
        foreach (var row in rows)
            ids.Add(row.OwnerUserId);
        if (detail != null)
        {
            ids.Add(detail.Ship.OwnerUserId);
            foreach (var r in detail.Revisions)
                if (r.ActorUserId is { } a) ids.Add(a);
            foreach (var a in detail.Timeline)
            {
                if (a.ActorUserId is { } actor) ids.Add(actor);
                if (a.SubjectUserId is { } subject) ids.Add(subject);
            }
        }
        if (escrow != null)
        {
            ids.Add(escrow.FromUserId);
            ids.Add(escrow.ToUserId);
        }

        var names = await ResolveNames(ids);

        // The live check reads the entity world and runs on the game thread, which is where every
        // continuation above lands.
        var live = _entMan.System<DrydockSystem>().LiveShipIds();

        var state = new DrydockAdminEuiState
        {
            Page = _page,
            PageSize = _pageSize,
            TotalShips = total,
            CurrentRoundId = round,
            Notice = _notice,
        };

        foreach (var row in rows)
            state.Ships.Add(ToDto(row, names, live, offers.GetValueOrDefault(row.ShipGuid)));

        if (detail != null)
        {
            var revisions = detail.Revisions.Select(r => new DrydockAdminRevisionDto(
                r.Revision,
                r.Kind.ToString(),
                r.CreatedAt,
                r.CreatedRoundId,
                r.ActorUserId,
                r.ActorUserId is { } a ? names.GetValueOrDefault(a) : null,
                r.SizeBytes,
                detail.RevisionsWithBlob.Contains(r.Revision),
                r.DerivedFromRevision,
                r.AppraisedValue)).ToList();

            var timeline = detail.Timeline.Select(a => new DrydockAdminAuditDto(
                a.Id,
                a.CreatedAt,
                a.Action.ToString(),
                a.ActorUserId,
                a.ActorUserId is { } actor ? names.GetValueOrDefault(actor) : null,
                a.SubjectUserId,
                a.SubjectUserId is { } subject ? names.GetValueOrDefault(subject) : null,
                a.Revision,
                a.BerthId,
                a.RoundId,
                a.Reason,
                a.ShipName)).ToList();

            DrydockAdminEscrowDto? escrowDto = null;
            if (escrow != null)
            {
                // The same preference the accept applies: smallest free berth of the recipient's
                // that fits. A preview, since the berth is picked again when they accept.
                int? lands = recipientBerths
                    .Where(s => s.Occupant == null && DrydockStore.Fits(detail.Ship.SizeClass, s.Berth.MaxSizeClass))
                    .OrderBy(s => DrydockStore.TryParseClass(s.Berth.MaxSizeClass, out var max) ? (int)max : int.MaxValue)
                    .ThenBy(s => s.Berth.BerthId)
                    .Select(s => (int?)s.Berth.BerthId)
                    .FirstOrDefault();

                escrowDto = new DrydockAdminEscrowDto(
                    escrow.Id,
                    escrow.FromUserId,
                    names.GetValueOrDefault(escrow.FromUserId),
                    escrow.ToUserId,
                    names.GetValueOrDefault(escrow.ToUserId),
                    escrow.CreatedAt,
                    escrow.ExpiresAt,
                    lands);
            }

            var saleDto = lastSale is { } s ? new DrydockAdminSaleDto(s.Price, s.At, ownerBalance) : null;

            state.Selected = new DrydockAdminShipDetailDto(
                ToDto(detail.Ship, names, live, escrow),
                detail.Ship.AdminNotes,
                revisions,
                timeline,
                escrowDto,
                saleDto);

            foreach (var slot in berths)
            {
                state.OwnerBerths.Add(new DrydockAdminBerthDto(
                    slot.Berth.BerthId,
                    slot.Berth.MaxSizeClass,
                    slot.Berth.Kind.ToString(),
                    slot.Berth.PricePaid,
                    slot.Occupant?.ShipGuid,
                    slot.Occupant?.ShipName,
                    slot.Occupant?.SizeClass,
                    slot.Occupant?.State.ToString()));
            }
        }

        _state = state;
        StateDirty();
    }

    private static DrydockAdminShipDto ToDto(DrydockShip ship, Dictionary<Guid, string> names, HashSet<Guid> live, DrydockTransfer? escrow)
    {
        return new DrydockAdminShipDto(
            ship.ShipGuid,
            ship.ShipName,
            ship.OwnerUserId,
            names.GetValueOrDefault(ship.OwnerUserId),
            ship.State.ToString(),
            ship.Investigating,
            ship.SizeClass,
            ship.VesselProto,
            ship.BerthId,
            ship.LastBerthId,
            ship.CheckedOutRoundId,
            ship.StateChangedAt,
            ship.CurrentRevision,
            live.Contains(ship.ShipGuid),
            escrow?.ExpiresAt);
    }

    private async Task<Dictionary<Guid, string>> ResolveNames(HashSet<Guid> ids)
    {
        var names = new Dictionary<Guid, string>();
        var missing = new List<Guid>();

        foreach (var id in ids)
        {
            if (_players.TryGetSessionById(new NetUserId(id), out var session))
                names[id] = session.Name;
            else
                missing.Add(id);
        }

        if (missing.Count > 0)
        {
            foreach (var (id, name) in await _store.GetPlayerNames(missing))
                names[id] = name;
        }

        return names;
    }
}
