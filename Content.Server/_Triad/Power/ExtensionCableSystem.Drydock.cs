using Content.Server.Power.Components;

namespace Content.Server.Power.EntitySystems;

/// <summary>
/// Pairing a receiver with a chosen provider, for the drydock's load.
///
/// <para>A receiver pairs once, when it starts, with the nearest connectable provider in range, and never looks again
/// (<see cref="OnReceiverStarted"/>, <see cref="TryFindAvailableProvider"/>). Which one is nearest and connectable at that
/// moment depends on which of them has started, so the same hull loaded in another order can hand a receiver to a dead-end
/// stub of cable a tile closer than the one that fed it, and the machine stays dark (three receivers in the 111-hull
/// ladder, 2026-09-19). The pairing is not a data field, so an image load carries the provider it had and sets it back
/// here, after every entity has started.</para>
/// </summary>
public sealed partial class ExtensionCableSystem
{
    /// <summary>
    /// Pairs <paramref name="receiver"/> with <paramref name="provider"/>, replacing any pairing it has, with the same
    /// bookkeeping and events as a pairing made at startup: both sides' lists and the connected and disconnected events.
    /// Refuses, changing nothing, when the provider is not connectable, the receiver is not, they are on different grids,
    /// or the provider is out of the range the startup search would have allowed.
    /// </summary>
    /// <returns>Whether the receiver is now paired with <paramref name="provider"/>.</returns>
    public bool TryPairReceiver(Entity<ExtensionCableReceiverComponent> receiver, Entity<ExtensionCableProviderComponent> provider)
    {
        if (receiver.Comp.Provider?.Owner == provider.Owner)
            return true;

        if (!receiver.Comp.Connectable || !provider.Comp.Connectable)
            return false;

        var receiverXform = Transform(receiver);
        var providerXform = Transform(provider);
        if (receiverXform.GridUid == null || receiverXform.GridUid != providerXform.GridUid)
            return false;

        var distance = (providerXform.LocalPosition - receiverXform.LocalPosition).Length();
        if (distance > Math.Min(receiver.Comp.ReceptionRange, provider.Comp.TransferRange))
            return false;

        // The unpairing half of Disconnect, without clearing Connectable, which is the anchoring state and is unchanged.
        if (receiver.Comp.Provider is { } previous)
        {
            RaiseLocalEvent(receiver.Owner, new ProviderDisconnectedEvent(previous), broadcast: false);
            RaiseLocalEvent(previous.Owner, new ReceiverDisconnectedEvent(receiver), broadcast: false);
            previous.Comp.LinkedReceivers.Remove(receiver);
        }

        // The pairing half of TryFindAndSetProvider, with the provider given rather than searched for.
        receiver.Comp.Provider = provider;
        provider.Comp.LinkedReceivers.Add(receiver);
        RaiseLocalEvent(receiver.Owner, new ProviderConnectedEvent(provider), broadcast: false);
        RaiseLocalEvent(provider.Owner, new ReceiverConnectedEvent(receiver), broadcast: false);
        return true;
    }
}
