using Content.Server._Triad.Drydock.Loader;
using Content.Server.Cargo.Systems;
using Content.Shared._NF.Trade;

namespace Content.Server._Triad.Cargo;

/// <summary>
/// Carries a trade crate's destination across a drydock store and load. A crate draws its destination at component init
/// (<c>CargoSystem.TradeCrates.cs:53-70</c>), so a loaded crate draws again and would come back pointing at a random
/// station, and its destination is a station's uid, which cannot travel. The store carries the destination's
/// <c>DestinationProto</c> id, which is what the crate's icon draws and names the same destination round after round; the
/// directed restore binds the crate to the station holding that id this round, with its icon and label.
///
/// <para>A carried id no station holds this round leaves the crate on the destination its init drew, which is already
/// consistent in uid, icon and label, with a warning. When the round has no destination at all, init drew nothing and the
/// image's icon and label would claim one the price never pays, so the crate is cleared: a crate whose init finds no
/// destination is born with none, no icon and no label (<c>CargoSystem.TradeCrates.cs:55-70</c>), and this leaves it the
/// same. An image stored without the key keeps init's draw, silently.</para>
/// </summary>
public sealed class TradeCrateCarrySystem : EntitySystem
{
    /// <summary>The carried key: the destination's <c>DestinationProto</c> id.</summary>
    public const string DestinationKey = "TradeCrate.Destination";

    [Dependency] private CargoSystem _cargo = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TradeCrateComponent, GridStoringEvent>(OnStoring);
        SubscribeLocalEvent<TradeCrateComponent, GridRestoredEvent>(OnRestored);
    }

    private void OnStoring(Entity<TradeCrateComponent> ent, ref GridStoringEvent args)
    {
        if (_cargo.TradeCrateDestinationProto(ent.Comp) is { } proto)
            args.Carry(DestinationKey, proto);
    }

    private void OnRestored(Entity<TradeCrateComponent> ent, ref GridRestoredEvent args)
    {
        if (!args.TryGetCarried<string>(DestinationKey, out var proto))
            return;

        if (_cargo.FindTradeCrateDestination(ent, proto) is { } destination)
        {
            _cargo.SetTradeCrateDestination(ent, destination);
            return;
        }

        if (_cargo.TradeCrateDestinationProto(ent.Comp) is { } drawn)
        {
            Log.Warning($"Drydock: trade crate {ToPrettyString(ent)} was bound to destination {proto}, which no station holds this round; it keeps {drawn}, drawn at load.");
            return;
        }

        _cargo.ClearTradeCrateDestination(ent);
        Log.Warning($"Drydock: trade crate {ToPrettyString(ent)} was bound to destination {proto}, and this round has no destination at all; it has none.");
    }
}
