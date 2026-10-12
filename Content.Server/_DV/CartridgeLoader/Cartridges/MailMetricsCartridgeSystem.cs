using Content.Server._DV.Cargo.Components;
using Content.Server._DV.Cargo.Systems;
using Content.Server.CartridgeLoader;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Content.Server._DV.Mail.Components;
using Content.Server._NF.SectorServices; // Frontier

namespace Content.Server._DV.CartridgeLoader.Cartridges;

public sealed partial class MailMetricsCartridgeSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private SectorServiceSystem _sectorService = default!; // Frontier

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MailMetricsCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<LogisticStatsUpdatedEvent>(OnLogisticsStatsUpdated);
        SubscribeLocalEvent<MailComponent, MapInitEvent>(OnMapInit);
    }

    private void OnUiReady(Entity<MailMetricsCartridgeComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateUI(ent); // Frontier: remove station as first arg // Triad: args.Loader<ent
    }

    private void OnLogisticsStatsUpdated(LogisticStatsUpdatedEvent args)
    {
        UpdateAllCartridges(); // Frontier: remove station
    }

    private void OnMapInit(EntityUid uid, MailComponent mail, MapInitEvent args)
    {
        UpdateAllCartridges(); // Frontier: remove station, no owner check
    }

    private void UpdateAllCartridges() // Frontier: remove station
    {
        var query = EntityQueryEnumerator<MailMetricsCartridgeComponent, CartridgeComponent>();
        while (query.MoveNext(out var uid, out var comp, out var cartridge))
        {
            if (cartridge.LoaderUid is not { } loader)
                continue;
            UpdateUI(uid); // Triad: loader<uid
        }
    }

    private void UpdateUI(EntityUid cartridge) // Triad: loader<cartridge
    {
        //if (_station.GetOwningStation(loader) is { } station) // Frontier
        //    ent.Comp.Station = station; // Frontier

        if (!TryComp<SectorLogisticStatsComponent>(_sectorService.GetServiceEntity(), out var logiStats)) // Frontier
            return; // Frontier

        // Get station's logistic stats
        var unopenedMailCount = GetUnopenedMailCount(); // Frontier: no station arg

        // Send logistic stats to cartridge client
        var state = new MailMetricUiState(logiStats.Metrics, unopenedMailCount);
        _cartridgeLoader.UpdateCartridgeUiState(cartridge, state); // Triad: loader<cartridge
    }


    private int GetUnopenedMailCount() // Frontier: remove EntityUid param
    {
        var unopenedMail = 0;

        var query = EntityQueryEnumerator<MailComponent>();

        while (query.MoveNext(out var _, out var comp))
        {
            // Frontier: remove station check, add profitable check (consider only possible profit as unopened)
            if (comp.IsLocked && comp.IsProfitable)
                unopenedMail++;
            // End Frontier
        }

        return unopenedMail;
    }
}
