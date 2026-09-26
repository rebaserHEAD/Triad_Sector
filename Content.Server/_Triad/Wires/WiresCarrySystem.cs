using System.Linq;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Power;
using Content.Server.Wires;
using Content.Shared.Power;

namespace Content.Server._Triad.Wires;

/// <summary>
/// A wired entity's wire list and cut wires across a drydock store and load. <see cref="WiresComponent.WiresList"/> is not
/// a data field and only map init builds it (<c>WiresSystem.OnMapInit</c>), which a restore never raises. The store carries
/// which wires are cut; the directed restore builds the list once, while it is empty, marks the carried wires cut and pushes
/// the panel state.
///
/// <para>A wire is named by its action's type name, empty for a wire with no action, and its rank among the wires of that
/// name in <see cref="Wire.OriginalPosition"/> order, which is its place in the layout prototype and the same in every
/// round. Its place in the list is not: a layout is cached per round and dropped at round restart, and an entity with
/// <see cref="WiresComponent.AlwaysRandomize"/> is shuffled at every build, so the order, colours and letters come back as
/// the round has them. A carried name the rebuilt list does not hold was dropped by an edit to the layout prototype since
/// the store, and is logged and left uncut.</para>
///
/// <para>The restore never calls an action's <c>Cut</c>: each action's effect is a data field or a manifest member already
/// set, and the power wire's <c>Cut</c> adds to its counter on every call. That counter,
/// <see cref="PowerWireActionKey.CutWires"/>, is the number of cut power wires, as the live cut path keeps it (a wire is
/// marked cut only when its action's <c>Cut</c> returns true, <c>WiresSystem.UpdateWires</c>), so it is set from the wires
/// once they are cut, and a carried count that disagrees is logged and replaced. Timed wires and a power wire's pulse are
/// not restored.</para>
/// </summary>
public sealed partial class WiresCarrySystem : EntitySystem
{
    /// <summary>The carried key: the name of each cut wire, in <see cref="Wire.OriginalPosition"/> order.</summary>
    public const string CutKey = "Wires.Cut";

    [Dependency] private WiresSystem _wires = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<WiresComponent, GridStoringEvent>(OnStoring);
        SubscribeLocalEvent<WiresComponent, GridRestoredEvent>(OnRestored);
    }

    private void OnStoring(Entity<WiresComponent> ent, ref GridStoringEvent args)
    {
        var cut = Named(ent.Comp.WiresList)
            .Where(named => named.Wire.IsCut)
            .Select(named => named.Name)
            .ToArray();

        if (cut.Length > 0)
            args.Carry(CutKey, cut);
    }

    private void OnRestored(Entity<WiresComponent> ent, ref GridRestoredEvent args)
    {
        int? carriedCount = _wires.TryGetData<int?>(ent, PowerWireActionKey.CutWires, out var count, ent.Comp) ? count : null;

        if (ent.Comp.WiresList.Count == 0 && !string.IsNullOrEmpty(ent.Comp.LayoutId))
            _wires.SetOrCreateWireLayout(ent, ent.Comp);

        if (args.TryGetCarried<(string Action, int Rank)[]>(CutKey, out var cut))
        {
            var byName = Named(ent.Comp.WiresList).ToDictionary(named => named.Name, named => named.Wire);
            var missed = 0;
            foreach (var name in cut)
            {
                if (byName.TryGetValue(name, out var wire))
                    wire.IsCut = true;
                else
                    missed++;
            }

            if (missed > 0)
                Log.Warning($"{ToPrettyString(ent)}: {missed} of {cut.Length} carried cut wires name no wire in layout {ent.Comp.LayoutId}, and are left uncut.");
        }

        if (_wires.HasData(ent, PowerWireActionKey.CutWires, ent.Comp))
        {
            var cutPower = ent.Comp.WiresList.Count(wire => wire.IsCut && wire.Action is PowerWireAction);
            if (carriedCount is { } carried && carried != cutPower)
                Log.Warning($"{ToPrettyString(ent)}: carried {carried} cut power wires and {cutPower} came back cut; the count is set to {cutPower}.");

            _wires.SetData(ent, PowerWireActionKey.CutWires, cutPower, ent.Comp);
        }

        _wires.RefreshUserInterface(ent, ent.Comp);
    }

    /// <summary>
    /// Each wire with its name, in <see cref="Wire.OriginalPosition"/> order. A type name names one action, since a layout
    /// prototype's <c>!type:</c> tag resolves the action by it.
    /// </summary>
    private static IEnumerable<((string Action, int Rank) Name, Wire Wire)> Named(List<Wire> wires)
    {
        var ranks = new Dictionary<string, int>();
        foreach (var wire in wires.OrderBy(wire => wire.OriginalPosition))
        {
            var action = wire.Action?.GetType().Name ?? string.Empty;
            var rank = ranks.GetValueOrDefault(action);
            ranks[action] = rank + 1;
            yield return ((action, rank), wire);
        }
    }
}
