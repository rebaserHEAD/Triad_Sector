using System.Linq;
using Content.Server._NF.SectorServices;
using Content.Server.StationRecords;
using Content.Shared._NF.BountyContracts;
using Content.Shared._Triad.CartridgeLoader; // Triad
using Content.Shared.Access.Components;
using Content.Shared.CartridgeLoader;
using Content.Shared.IdentityManagement;
using Content.Shared.PDA;
using Content.Shared.Popups; // Triad
using Content.Shared.StationRecords;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NF.BountyContracts;

public sealed partial class BountyContractSystem
{
    [Dependency] SectorServiceSystem _sectorService = default!;
    [Dependency] IGameTiming _timing = default!;
    [Dependency] private SharedPopupSystem _popup = default!; // Triad
    private void InitializeUi()
    {
        SubscribeLocalEvent<BountyContractsCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<BountyContractsCartridgeComponent, CartridgeMessageEvent>(OnUiMessage);
    }

    /// <summary>
    ///     Show create contract menu on ui cartridge.
    /// </summary>
    private void CartridgeOpenCreateUi(Entity<BountyContractsCartridgeComponent> cartridge, EntityUid loaderUid, ProtoId<BountyContractCollectionPrototype> collection)
    {
        var state = GetCreateState(cartridge, collection);
        _cartridgeLoader.UpdateCartridgeUiState(cartridge.Owner, state); // Triad: loaderUid<cartridge.Owner
    }

    /// <summary>
    ///     Show list all contracts menu on ui cartridge.
    /// </summary>
    private void CartridgeOpenListUi(Entity<BountyContractsCartridgeComponent> cartridge, EntityUid loaderUid, ProtoId<BountyContractCollectionPrototype>? collection = null)
    {
        var state = GetListState(cartridge, loaderUid, collection);

        if (state == null)
        {
            _cartridgeLoader.UpdateCartridgeUiState(cartridge.Owner, new BountyContractNoAccessUiState()); // Triad: the app says there is no board to read
            return;
        }

        _cartridgeLoader.UpdateCartridgeUiState(cartridge.Owner, state); // Triad: loaderUid<cartridge.Owner
    }

    // Triad: a posted or removed contract reaches every PDA showing a board list, a PDA on the create form keeps its form
    private void RefreshOtherLists(EntityUid except)
    {
        var query = EntityQueryEnumerator<BountyContractsCartridgeComponent, CartridgeComponent, CartridgeUiStateComponent>();
        while (query.MoveNext(out var uid, out var bounty, out var cartridge, out var stored))
        {
            if (uid == except || cartridge.LoaderUid is not { } loader || stored.State is not BountyContractListUiState)
                continue;

            if (_cartridgeLoader.IsProgramShowing((uid, cartridge)))
                CartridgeOpenListUi((uid, bounty), loader);
        }
    }
    // End Triad

    private void CartridgeRefreshListUi(Entity<BountyContractsCartridgeComponent> cartridge, EntityUid loaderUid, ProtoId<BountyContractCollectionPrototype>? collection = null)
    {
        // this will technically refresh it
        // by sending list state again
        CartridgeOpenListUi(cartridge, loaderUid, collection);
    }

    private BountyContractListUiState? GetListState(Entity<BountyContractsCartridgeComponent> cartridge, EntityUid loaderUid, ProtoId<BountyContractCollectionPrototype>? collection = null)
    {
        // Set the cartridge's collection if requested.
        if (collection != null)
            cartridge.Comp.Collection = collection;

        var contracts = GetPermittedContracts(cartridge, loaderUid, out var newCollection).ToList();
        if (newCollection == null)
            return null;

        var isAllowedCreate = HasWriteAccess(loaderUid, newCollection.Value);
        var isAllowedRemove = HasDeleteAccess(loaderUid, newCollection.Value);

        if (cartridge.Comp.Collection != newCollection)
            cartridge.Comp.Collection = newCollection;

        return new BountyContractListUiState(newCollection.Value, GetReadableCollections(loaderUid), contracts, isAllowedCreate, isAllowedRemove, GetNetEntity(loaderUid), cartridge.Comp.NotificationsEnabled);
    }

    private BountyContractCreateUiState GetCreateState(Entity<BountyContractsCartridgeComponent> cartridge, ProtoId<BountyContractCollectionPrototype> collection)
    {
        var bountyTargets = new HashSet<BountyContractTargetInfo>();
        var vessels = new HashSet<string>();

        // TODO: This will show all Stations, not only NT stations
        // TODO: Register all NT characters in some cache component on main station?
        var allStations = EntityQueryEnumerator<StationRecordsComponent, MetaDataComponent>();
        while (allStations.MoveNext(out var uid, out _, out var meta))
        {
            // get station IC name - its vessel name
            var name = meta.EntityName;
            vessels.Add(name);

            // get all characters registered on this station
            var icRecords = _records.GetRecordsOfType<GeneralStationRecord>(uid);
            foreach (var (_, icRecord) in icRecords)
            {
                var target = new BountyContractTargetInfo
                {
                    Name = icRecord.Name,
                    DNA = icRecord.DNA
                };

                // hashset will check if record is unique based on DNA field
                bountyTargets.Add(target);
            }
        }

        return new BountyContractCreateUiState(collection, bountyTargets.ToList(), vessels.ToList());
    }

    private void OnUiReady(EntityUid uid, BountyContractsCartridgeComponent component, CartridgeUiReadyEvent args)
    {
        CartridgeOpenListUi((uid, component), args.Loader);
    }

    private void OnUiMessage(EntityUid uid, BountyContractsCartridgeComponent component, CartridgeMessageEvent args)
    {
        if (args is BountyContractCommandMessageEvent command)
            OnCommandMessage((uid, component), ref command);
        else if (args is BountyContractTryRemoveMessageEvent remove)
            OnTryRemoveMessage((uid, component), ref remove);
        else if (args is BountyContractTryCreateMessageEvent create)
            OnTryCreateMessage((uid, component), ref create);
    }

    private void OnCommandMessage(Entity<BountyContractsCartridgeComponent> cartridge, ref BountyContractCommandMessageEvent args)
    {
        switch (args.Command)
        {
            case BountyContractCommand.OpenCreateUi:
                CartridgeOpenCreateUi(cartridge, GetEntity(args.LoaderUid), args.Collection);
                break;
            case BountyContractCommand.CloseCreateUi:
                CartridgeOpenListUi(cartridge, GetEntity(args.LoaderUid), args.Collection);
                break;
            case BountyContractCommand.RefreshList:
                CartridgeRefreshListUi(cartridge, GetEntity(args.LoaderUid), args.Collection);
                break;
            case BountyContractCommand.ToggleNotifications:
                cartridge.Comp.NotificationsEnabled = !cartridge.Comp.NotificationsEnabled;
                CartridgeRefreshListUi(cartridge, GetEntity(args.LoaderUid), args.Collection); // Force UI udpate
                break;
            default:
                return; //TODO: print to log?
        }
    }

    private void OnTryRemoveMessage(Entity<BountyContractsCartridgeComponent> cartridge, ref BountyContractTryRemoveMessageEvent args)
    {
        var loader = GetEntity(args.LoaderUid);

        // Check the delete access for the user on this collection.
        if (TryRemoveBountyContract(loader, args.Actor, args.ContractId))
        {
            CartridgeRefreshListUi(cartridge, loader);
            RefreshOtherLists(cartridge); // Triad
        }
    }

    private void OnTryCreateMessage(Entity<BountyContractsCartridgeComponent> cartridge, ref BountyContractTryCreateMessageEvent args)
    {
        var loader = GetEntity(args.LoaderUid);

        if (!cartridge.Comp.CreateEnabled)
        {
            // Triad: the poster is told the cooldown is why nothing happened
            var seconds = (int) Math.Ceiling((cartridge.Comp.NextCreate - _timing.CurTime).TotalSeconds);
            _popup.PopupEntity(Loc.GetString("bounty-contracts-ui-create-cooldown", ("seconds", Math.Max(seconds, 1))), loader, args.Actor);
            // End Triad
            return;
        }

        var c = args.Contract;
        var author = Identity.Name(args.Actor, EntityManager);

        // Try to post a bounty. If it works, update the requester's UI.
        if (TryCreateBountyContract(c.Collection, c.Category, c.Name, c.Reward, loader, args.Actor, c.Description, c.Vessel, c.DNA, author) != null)
        {
            cartridge.Comp.CreateEnabled = false;
            cartridge.Comp.NextCreate = _timing.CurTime + TimeSpan.FromSeconds(cartridge.Comp.CreateCooldown);

            CartridgeOpenListUi(cartridge, loader);
            RefreshOtherLists(cartridge); // Triad
        }
    }
}
