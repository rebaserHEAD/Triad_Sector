// Triad: drydock tab. A partial of the NF ShipyardSystem, kept in the Triad tree beside the other
// Triad console work rather than in _NF/, so an upstream merge touching the shipyard console only
// ever conflicts on the two subscriptions in ShipyardSystem.Initialize. The pipeline this sits in
// front of lives in Content.Server._Triad.Drydock.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NF.Shipyard.Components;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Robust.Shared.Player;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    [Dependency] private readonly DrydockSystem _drydock = default!;
    [Dependency] private readonly DrydockStore _drydockStore = default!;

    /// <summary>
    /// The round to stamp an audit row with, or null when there is no round yet.
    ///
    /// <para><see cref="GameTicker.RoundId"/> reads 0 before a round has been filed, and the round
    /// columns are real foreign keys, so passing that straight through makes the insert fail on a
    /// constraint rather than recording "no round". Nullable is what the schema means by it. The
    /// same guard is written at every other Triad call site that stamps a round.</para>
    /// </summary>
    private int? DrydockRoundId => _gameTicker.RoundId > 0 ? _gameTicker.RoundId : null;

    // Both handlers are async void, which is what a BUI message subscription has to be, and an
    // exception escaping an async void has nowhere to go but the synchronization context. A
    // database fault mid-store or mid-retrieve is a logged refusal, never an unhandled throw.
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

    /// <summary>
    /// Fills the console's stored-ship cache for whoever is operating it, then re-publishes the
    /// interface state so the drydock tab shows the fresh list. The cache exists because the state
    /// builder is synchronous and this list comes from the database.
    /// </summary>
    internal async Task RefreshDrydockState(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        component.CachedStoredShips = new();

        // No card, no account to list against: the drydock tab is per-operator, and an empty list
        // is the honest answer rather than everything the console has ever seen.
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true }
            || !TryComp<ActorComponent>(player, out var actor))
        {
            RefreshDrydockUi(uid, component, player, uiKey);
            return;
        }

        var rows = await _drydockStore.GetShipsByOwner(actor.PlayerSession.UserId.UserId);

        // The console or the operator may have gone during the read.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        component.CachedStoredShips = rows
            // The row state is the gate, not a process-local registry of what is flying. A ship
            // that is checked out is already in the world, and one under investigation is waiting
            // on a human decision; offering either would put a row on screen that retrieve is
            // going to refuse. Retrieve's own conditional claim is what actually enforces this -
            // this filter only keeps the console from advertising a button that cannot work.
            .Where(r => r.State == DrydockShipState.Stored && !r.Investigating)
            .Select(r => new StoredShipInfo(r.ShipGuid, r.ShipName, r.SizeClass))
            .ToList();

        RefreshDrydockUi(uid, component, player, uiKey);
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

        if (!TryComp<ShipOwnershipComponent>(shuttleUid, out var ownership)
            || !TryComp<ActorComponent>(player, out var actor)
            || ownership.OwnerUserId != actor.PlayerSession.UserId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-store-not-owner"));
            PlayDenySound(player, uid, component);
            return null;
        }

        var result = await _drydock.TryStoreShip(shuttleUid, ownership.OwnerUserId.UserId, DrydockRoundId);

        // The write yielded. The store itself has already succeeded or refused; everything below is
        // the console epilogue, and this is an async void handler, so a throw here is unhandled.
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

        if (!TryComp<ActorComponent>(player, out var actor))
            return null;

        if (_station.GetOwningStation(uid) is not { Valid: true } station)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-station"));
            PlayDenySound(player, uid, component);
            return null;
        }

        var grid = await _drydock.TryRetrieveShip(shipId, actor.PlayerSession.UserId.UserId, station, DrydockRoundId);

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
