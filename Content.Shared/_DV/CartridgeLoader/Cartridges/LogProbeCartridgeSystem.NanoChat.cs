using Content.Shared.Audio;
using Content.Shared.CartridgeLoader;
using Content.Shared._DV.CartridgeLoader.Cartridges;
using Content.Shared._DV.NanoChat;
using Content.Shared.Interaction; // Triad
using Robust.Shared.Audio;
using Robust.Shared.Network; // Triad
using Robust.Shared.Random;

namespace Content.Shared.CartridgeLoader.Cartridges; // Triad: Server<Shared, a partial lives in the assembly of the class it extends

public sealed partial class LogProbeCartridgeSystem
{
    [Dependency] private INetManager _net = default!; // Triad

    private void InitializeNanoChat()
    {
        SubscribeLocalEvent<NanoChatRecipientUpdatedEvent>(OnRecipientUpdated);
        SubscribeLocalEvent<NanoChatMessageReceivedEvent>(OnMessageReceived);
    }

    private void OnRecipientUpdated(ref NanoChatRecipientUpdatedEvent args)
    {
        var query = EntityQueryEnumerator<LogProbeCartridgeComponent, CartridgeComponent>();
        while (query.MoveNext(out var uid, out var probe, out var cartridge))
        {
            if (probe.ScannedNanoChatData == null || GetEntity(probe.ScannedNanoChatData.Value.Card) != args.CardUid)
                continue;

            if (!TryComp<NanoChatCardComponent>(args.CardUid, out var card))
                continue;

            probe.ScannedNanoChatData = new NanoChatData(
                new Dictionary<uint, NanoChatRecipient>(card.Recipients),
                probe.ScannedNanoChatData.Value.Messages,
                card.Number,
                GetNetEntity(args.CardUid));

            if (cartridge.LoaderUid != null)
                UpdateUiState((uid, probe), cartridge.LoaderUid.Value);
        }
    }

    private void OnMessageReceived(ref NanoChatMessageReceivedEvent args)
    {
        var query = EntityQueryEnumerator<LogProbeCartridgeComponent, CartridgeComponent>();
        while (query.MoveNext(out var uid, out var probe, out var cartridge))
        {
            if (probe.ScannedNanoChatData == null || GetEntity(probe.ScannedNanoChatData.Value.Card) != args.CardUid)
                continue;

            if (!TryComp<NanoChatCardComponent>(args.CardUid, out var card))
                continue;

            probe.ScannedNanoChatData = new NanoChatData(
                probe.ScannedNanoChatData.Value.Recipients,
                new Dictionary<uint, List<NanoChatMessage>>(card.Messages),
                card.Number,
                GetNetEntity(args.CardUid));

            if (cartridge.LoaderUid != null)
                UpdateUiState((uid, probe), cartridge.LoaderUid.Value);
        }
    }

    private void ScanNanoChatCard(Entity<LogProbeCartridgeComponent> ent,
        ref CartridgeRelayedEvent<AfterInteractEvent> args, // Triad: CartridgeAfterInteractEvent<CartridgeRelayedEvent
        EntityUid target,
        NanoChatCardComponent card)
    {
        // Triad: the scanned history is server data and is not networked, so the scan is not predicted
        if (_net.IsClient)
            return;

        _audio.PlayEntity(ent.Comp.SoundScan,
            args.Args.User,
            target,
            AudioParams.Default.WithVariation(0.25f));
        _popup.PopupCursor(Loc.GetString("log-probe-scan-nanochat", ("card", target)), args.Args.User);

        ent.Comp.PulledAccessLogs.Clear();

        ent.Comp.ScannedNanoChatData = new NanoChatData(
            new Dictionary<uint, NanoChatRecipient>(card.Recipients),
            new Dictionary<uint, List<NanoChatMessage>>(card.Messages),
            card.Number,
            GetNetEntity(target)
        );

        UpdateUiState(ent, args.Loader);
    }
}
