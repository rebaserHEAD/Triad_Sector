using Content.Server._NF.Trade;
using Content.Shared._NF.Trade;

namespace Content.Server.Cargo.Systems;

/// <summary>
/// A trade crate's destination, bound by id for the drydock's carry (<see cref="Content.Server._Triad.Cargo.TradeCrateCarrySystem"/>).
/// The three writes are the ones a crate's init makes when it draws its destination (<c>CargoSystem.TradeCrates.cs:64-69</c>):
/// the uid the price reads, the icon, and the label, which also names the crate.
/// </summary>
public sealed partial class CargoSystem
{
    /// <summary>The <see cref="TradeCrateDestinationComponent.DestinationProto"/> of a crate's destination, or null when it has no live one.</summary>
    public string? TradeCrateDestinationProto(TradeCrateComponent crate)
    {
        return TryComp<TradeCrateDestinationComponent>(crate.DestinationStation, out var destination)
            ? destination.DestinationProto.Id
            : null;
    }

    /// <summary>
    /// The destination this round that holds <paramref name="proto"/>: the crate's own when it holds it, otherwise the first in
    /// the order destinations registered, or null when none does. Only <c>CargoOther</c> is held by more than one
    /// (<c>PointOfInterestSystem.cs:92-95</c>).
    /// </summary>
    public EntityUid? FindTradeCrateDestination(Entity<TradeCrateComponent> crate, string proto)
    {
        EntityUid? first = null;
        foreach (var destination in _destinations)
        {
            if (!TryComp<TradeCrateDestinationComponent>(destination, out var comp) || comp.DestinationProto.Id != proto)
                continue;

            if (destination == crate.Comp.DestinationStation)
                return destination;

            first ??= destination;
        }

        return first;
    }

    /// <summary>Binds a crate to <paramref name="destination"/> as its init does: the uid, the icon and the label.</summary>
    public void SetTradeCrateDestination(Entity<TradeCrateComponent> crate, EntityUid destination)
    {
        crate.Comp.DestinationStation = destination;
        if (TryComp<TradeCrateDestinationComponent>(destination, out var destComp))
            _appearance.SetData(crate, TradeCrateVisuals.DestinationIcon, destComp.DestinationProto.Id);

        if (TryComp(destination, out MetaDataComponent? metadata))
            _label.Label(crate, metadata.EntityName);
    }

    /// <summary>
    /// Leaves a crate with no destination, no icon and no label, which is how a crate whose init found no destination is
    /// born (<c>CargoSystem.TradeCrates.cs:55-70</c>).
    /// </summary>
    public void ClearTradeCrateDestination(Entity<TradeCrateComponent> crate)
    {
        crate.Comp.DestinationStation = EntityUid.Invalid;
        _appearance.RemoveData(crate, TradeCrateVisuals.DestinationIcon);
        _label.Label(crate, null);
    }
}
