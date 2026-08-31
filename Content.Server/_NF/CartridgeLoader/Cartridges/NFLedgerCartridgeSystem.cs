using Content.Server.CartridgeLoader;
using Content.Shared.CartridgeLoader;
using Content.Server._NF.SectorServices;
using Content.Shared._NF.Bank.BUI;
using System.Diagnostics.CodeAnalysis;
using Content.Server._NF.Bank;

namespace Content.Server._NF.CartridgeLoader.Cartridges;

// System for ledger cartridges - pushes updates to PDA UI when ledger is updated.
public sealed partial class NFLedgerCartridgeSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private SectorServiceSystem _sectorService = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NFLedgerCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<SectorLedgerUpdatedEvent>(OnSectorLedgerUpdated);
    }
    private void OnUiReady(Entity<NFLedgerCartridgeComponent> ent, ref CartridgeUiReadyEvent args)
    {
        if (GetUIState(out var uiState))
            UpdateUI(args.Loader, uiState);
    }

    private void OnSectorLedgerUpdated(SectorLedgerUpdatedEvent args)
    {
        UpdateAllCartridges();
    }

    private void UpdateAllCartridges()
    {
        var query = EntityQueryEnumerator<NFLedgerCartridgeComponent, CartridgeComponent>();

        if (!GetUIState(out var uiState))
            return;

        while (query.MoveNext(out _, out _, out var cartridge))
        {
            if (cartridge.LoaderUid is not { } loader)
                continue;
            UpdateUI(loader, uiState);
        }
    }

    private bool GetUIState([NotNullWhen(true)] out NFLedgerState? uiState)
    {
        uiState = null;
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorBankComponent? ledger))
            return false;

        // Triad: BlackMarket is hidden from creditflow - it is the smuggling economy's meter, not
        // a department, and its movements are nobody's business on a PDA.
        var entryList = new List<NFLedgerEntry>(ledger.AccountLedgerEntries.Count);
        foreach (var ledgerEntry in ledger.AccountLedgerEntries)
        {
            if (ledgerEntry.Key.Account == Content.Shared._NF.Bank.Components.SectorBankAccount.BlackMarket)
                continue;
            entryList.Add(new NFLedgerEntry
            {
                Account = ledgerEntry.Key.Account,
                Type = ledgerEntry.Key.Type,
                Amount = ledgerEntry.Value,
            });
        }
        uiState = new NFLedgerState(entryList.ToArray());
        return true;
    }

    private void UpdateUI(EntityUid loader, NFLedgerState state)
    {
        _cartridgeLoader.UpdateCartridgeUiState(loader, state);
    }
}
