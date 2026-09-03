using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock.Admin;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Eui;
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
public sealed class DrydockAdminEui : BaseEui
{
    [Dependency] private readonly DrydockStore _store = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    private const int DefaultPageSize = 50;

    private int _page;
    private int _pageSize = DefaultPageSize;
    private string? _ownerFilter;
    private string? _shipNameFilter;
    private DrydockShipState? _stateFilter;
    private bool _strandedOnly;

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
                _ownerFilter = string.IsNullOrWhiteSpace(req.Owner) ? null : req.Owner.Trim();
                _shipNameFilter = string.IsNullOrWhiteSpace(req.ShipNameContains) ? null : req.ShipNameContains.Trim();
                _stateFilter = Enum.TryParse<DrydockShipState>(req.State, out var state) ? state : null;
                _strandedOnly = req.StrandedOnly;
                _ = RefreshAsync();
                break;

            case DrydockAdminSelectShipMessage select:
                _selected = select.ShipGuid;
                _ = RefreshAsync();
                break;

            case DrydockAdminHoldMessage hold:
                _ = Act(async () =>
                {
                    var moved = hold.Hold
                        ? await _store.TrySetState(hold.ShipGuid, null, DrydockShipState.Held, DrydockAuditAction.Hold, AdminId, RoundForAudit(), hold.Reason)
                        : await _store.TrySetState(hold.ShipGuid, DrydockShipState.Held, DrydockShipState.Stored, DrydockAuditAction.Release, AdminId, RoundForAudit(), hold.Reason);
                    return moved
                        ? (hold.Hold ? "Ship held." : "Hold released; the ship is stored again.")
                        : (hold.Hold ? "Already held, or unknown ship." : "Not held, so nothing to release.");
                });
                break;

            case DrydockAdminInvestigateMessage inv:
                _ = Act(async () =>
                {
                    var changed = await _store.SetInvestigating(inv.ShipGuid, inv.Investigating, AdminId, RoundForAudit(), inv.Reason);
                    return changed
                        ? (inv.Investigating ? "Investigation opened; retrieve is refused." : "Investigation closed.")
                        : "No change.";
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
                    return outcome switch
                    {
                        DrydockBerthResult.Success => "Ship restored to the drydock.",
                        DrydockBerthResult.WrongState => "Refused: the ship is already stored, or a live grid still carries it this round.",
                        DrydockBerthResult.BerthTooSmall => "Refused: that berth cannot hold this hull. Grant a larger one.",
                        DrydockBerthResult.BerthOccupied => "Refused: that berth is occupied.",
                        DrydockBerthResult.NotFound => "Refused: unknown ship, or the berth is not the owner's.",
                        _ => $"Refused: {outcome}.",
                    };
                });
                break;

            case DrydockAdminMoveMessage move:
                _ = Act(async () =>
                {
                    var outcome = await _store.TryMoveShip(move.ShipGuid, move.BerthId, AdminId, RoundForAudit(), move.Reason);
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
                    return $"Granted {sizeClass} berth #{id}.";
                });
                break;

            case DrydockAdminDeleteBerthMessage delBerth:
                _ = Act(async () =>
                {
                    var (outcome, _) = await _store.TryRemoveBerth(delBerth.BerthId, null, DrydockAuditAction.BerthDelete, AdminId, RoundForAudit());
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

        Guid? ownerId = null;
        string? ownerName = null;
        if (_ownerFilter != null)
        {
            if (Guid.TryParse(_ownerFilter, out var parsed))
                ownerId = parsed;
            else
                ownerName = _ownerFilter;
        }

        var filter = new DrydockShipFilter(ownerId, ownerName, _shipNameFilter, _stateFilter, _strandedOnly, round);
        var (rows, total) = await _store.QueryShips(filter, _page, _pageSize);

        var detail = _selected is { } selected ? await _store.GetShipDetail(selected) : null;
        if (_selected != null && detail == null)
            _selected = null;

        var berths = detail != null ? await _store.GetBerths(detail.Ship.OwnerUserId) : new List<DrydockBerthSlot>();

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
            state.Ships.Add(ToDto(row, names, live));

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
                r.DerivedFromRevision)).ToList();

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
                a.Reason)).ToList();

            state.Selected = new DrydockAdminShipDetailDto(ToDto(detail.Ship, names, live), detail.Ship.AdminNotes, revisions, timeline);

            foreach (var slot in berths)
            {
                state.OwnerBerths.Add(new DrydockAdminBerthDto(
                    slot.Berth.BerthId,
                    slot.Berth.MaxSizeClass,
                    slot.Berth.Kind.ToString(),
                    slot.Berth.PricePaid,
                    slot.Occupant?.ShipGuid,
                    slot.Occupant?.ShipName));
            }
        }

        _state = state;
        StateDirty();
    }

    private static DrydockAdminShipDto ToDto(DrydockShip ship, Dictionary<Guid, string> names, HashSet<Guid> live)
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
            ship.CheckedOutRoundId,
            ship.StateChangedAt,
            ship.CurrentRevision,
            live.Contains(ship.ShipGuid));
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
