using System.Linq;
using Content.Server._Triad.Drydock.Loader;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;

namespace Content.Server._Triad.Hands;

/// <summary>
/// Carries a hand-bearing entity's hands across a drydock store and load. A hands component's hands are not data fields,
/// so a restored entity would come back with none, while each hand's container, named after the hand, comes back holding
/// what the hand held. The store carries each hand's name and location and the active hand's name; the directed restore
/// adds each hand again and sets the active one.
///
/// <para>Each hand, its location, its contents and the active hand come back. <c>AddHand</c> returns at once for a hand
/// the entity has, and otherwise ensures the container of the hand's name, which is the restored one, so the held item
/// becomes the hand's own again with no spawn and a second raise adds nothing. The hands' order is not replayed:
/// <c>SortedHands</c> is rebuilt by the engine's own insertion rule, which places a hand by its location against the
/// hands already there (<see cref="SharedHandsSystem.AddToSortedHands"/>), so an entity with several hands may list them
/// in another order, which only iteration and the UI read.</para>
///
/// <para><c>HandsFill</c> needs nothing suppressed: a restore raises no map init, and a map init on a restored entity
/// would find every hand already there.</para>
/// </summary>
public sealed partial class HandsCarrySystem : EntitySystem
{
    /// <summary>The carried key: each hand's name and location, in the order the entity listed them.</summary>
    public const string ListKey = "Hands.List";

    /// <summary>The carried key: the active hand's name, when there is one.</summary>
    public const string ActiveKey = "Hands.Active";

    [Dependency] private SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<HandsComponent, GridStoringEvent>(OnStoring);
        SubscribeLocalEvent<HandsComponent, GridRestoredEvent>(OnRestored);
    }

    private void OnStoring(Entity<HandsComponent> ent, ref GridStoringEvent args)
    {
        if (ent.Comp.SortedHands.Count == 0)
            return;

        var list = ent.Comp.SortedHands.Select(name => (name, ent.Comp.Hands[name].Location)).ToArray();
        args.Carry(ListKey, list);

        if (ent.Comp.ActiveHand is { } active)
            args.Carry(ActiveKey, active.Name);
    }

    private void OnRestored(Entity<HandsComponent> ent, ref GridRestoredEvent args)
    {
        if (!args.TryGetCarried<(string Name, HandLocation Location)[]>(ListKey, out var list))
            return;

        foreach (var (name, location) in list)
            _hands.AddHand(ent, name, location, ent.Comp);

        if (args.TryGetCarried<string>(ActiveKey, out var active))
            _hands.TrySetActiveHand(ent, active, ent.Comp);
    }
}
