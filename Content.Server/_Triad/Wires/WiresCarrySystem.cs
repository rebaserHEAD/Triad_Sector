using System.Linq;
using System.Reflection;
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
/// once they are cut, and a carried count that disagrees is logged and replaced.</para>
///
/// <para>A running wire timer (<see cref="WiresSystem.StartWireAction"/>, the revert a pulse arms) travels as its wire's
/// name, its key, its expiry method and the seconds it had left, and runs again for exactly those seconds. The expiry is a
/// private method on the wire's action, found again by its declaring type's name and its own on the rebuilt wire's action,
/// so a name that no longer binds, which only an edit since the store can cause, is logged and dropped.
/// <c>DrydockWiresTimersTest</c> audits every binding, so an upstream rename fails the suite instead. A timer re-arms when
/// its entity unpauses, because <c>WiresSystem.Update</c> counts a timer down whatever the pause and a power edge while
/// staged would cancel it, and at once on an entity loaded unpaused. <c>StartWireAction</c> ignores a key already running,
/// so a second restore raise re-arms nothing twice.</para>
/// </summary>
public sealed partial class WiresCarrySystem : EntitySystem
{
    /// <summary>The carried key: the name of each cut wire, in <see cref="Wire.OriginalPosition"/> order.</summary>
    public const string CutKey = "Wires.Cut";

    /// <summary>The carried key: each running wire timer (<see cref="CarriedWireTimer"/>).</summary>
    public const string TimersKey = "Wires.Timers";

    [Dependency] private WiresSystem _wires = default!;

    /// <summary>Timers restored onto a paused entity, waiting for it to unpause.</summary>
    private readonly Dictionary<EntityUid, List<(Wire Wire, object Key, WireActionDelegate Expiry, float TimeLeft)>> _held = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<WiresComponent, GridStoringEvent>(OnStoring);
        SubscribeLocalEvent<WiresComponent, GridRestoredEvent>(OnRestored);
        SubscribeLocalEvent<WiresComponent, EntityUnpausedEvent>(OnUnpaused);
        SubscribeLocalEvent<WiresComponent, EntityTerminatingEvent>(OnTerminating);
    }

    private void OnStoring(Entity<WiresComponent> ent, ref GridStoringEvent args)
    {
        var named = Named(ent.Comp.WiresList).ToList();
        var cut = named
            .Where(n => n.Wire.IsCut)
            .Select(n => n.Name)
            .ToArray();

        if (cut.Length > 0)
            args.Carry(CutKey, cut);

        var timers = new List<CarriedWireTimer>();
        foreach (var (key, timeLeft, onFinish) in _wires.RunningTimers(ent))
        {
            var wire = named.FirstOrDefault(n => ReferenceEquals(n.Wire, onFinish.Wire));
            var method = onFinish.Delegate.Method;
            if (wire.Wire == null || key is not Enum enumKey || method.DeclaringType == null)
            {
                Log.Warning($"{ToPrettyString(ent)}: a running wire timer keyed {key} is on no wire of this entity or has no enum key, and is not carried.");
                continue;
            }

            timers.Add(new CarriedWireTimer
            {
                Action = wire.Name.Action,
                Rank = wire.Name.Rank,
                Key = enumKey,
                ExpiryType = method.DeclaringType.Name,
                Expiry = method.Name,
                TimeLeft = timeLeft,
            });
        }

        if (timers.Count > 0)
            args.Carry(TimersKey, timers.OrderBy(t => t.Action, StringComparer.Ordinal).ThenBy(t => t.Rank).ThenBy(t => t.Key.ToString(), StringComparer.Ordinal).ToArray());
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

        if (args.TryGetCarried<CarriedWireTimer[]>(TimersKey, out var timers))
            RestoreTimers(ent, timers);

        if (_wires.TryGetData<bool>(ent, PowerWireActionKey.Pulsed, out var pulsed, ent.Comp) && pulsed
            && timers?.Any(t => Equals(t.Key, PowerWireActionKey.PulseCancel)) != true)
        {
            Log.Warning($"{ToPrettyString(ent)}: its power wire came back pulsed with no pulse timer carried, so nothing will end the pulse.");
        }

        _wires.RefreshUserInterface(ent, ent.Comp);
    }

    private void RestoreTimers(Entity<WiresComponent> ent, CarriedWireTimer[] timers)
    {
        var byName = Named(ent.Comp.WiresList).ToDictionary(named => named.Name, named => named.Wire);
        var resolved = new List<(Wire Wire, object Key, WireActionDelegate Expiry, float TimeLeft)>();
        foreach (var timer in timers)
        {
            if (!byName.TryGetValue((timer.Action, timer.Rank), out var wire)
                || wire.Action is not { } action
                || Expiry(action, timer.ExpiryType, timer.Expiry) is not { } expiry)
            {
                Log.Warning($"{ToPrettyString(ent)}: the timer {timer.Key} on wire {timer.Action}#{timer.Rank} ({timer.ExpiryType}.{timer.Expiry}) "
                            + "binds to nothing on the rebuilt wires, and is dropped.");
                continue;
            }

            resolved.Add((wire, timer.Key, expiry, timer.TimeLeft));
        }

        if (resolved.Count == 0)
            return;

        if (Paused(ent))
            _held[ent.Owner] = resolved;
        else
            Rearm(ent, resolved);
    }

    private void OnUnpaused(Entity<WiresComponent> ent, ref EntityUnpausedEvent args)
    {
        if (_held.Remove(ent.Owner, out var timers))
            Rearm(ent, timers);
    }

    private void OnTerminating(Entity<WiresComponent> ent, ref EntityTerminatingEvent args)
    {
        _held.Remove(ent.Owner);
    }

    private void Rearm(EntityUid uid, List<(Wire Wire, object Key, WireActionDelegate Expiry, float TimeLeft)> timers)
    {
        foreach (var (wire, key, expiry, timeLeft) in timers)
            _wires.StartWireAction(uid, timeLeft, key, new TimedWireEvent(expiry, wire));
    }

    /// <summary>
    /// The expiry <paramref name="name"/> declared on <paramref name="declaringType"/> in <paramref name="action"/>'s type or
    /// one of its bases, bound to <paramref name="action"/>, or null when there is no such method taking a wire. A base's
    /// private method is found only on the base, so the walk looks at each type in turn.
    /// </summary>
    public static WireActionDelegate? Expiry(IWireAction action, string declaringType, string name)
    {
        for (var type = action.GetType(); type != null; type = type.BaseType)
        {
            if (type.Name != declaringType)
                continue;

            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, new[] { typeof(Wire) }, null);
            return method is { ReturnType: var returns } && returns == typeof(void)
                ? Delegate.CreateDelegate(typeof(WireActionDelegate), action, method, throwOnBindFailure: false) as WireActionDelegate
                : null;
        }

        return null;
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

/// <summary>
/// One running wire timer as it travels: the wire it runs on, by name (<see cref="WiresCarrySystem"/>), its key, the
/// private method its end calls, by the type that declares it and its own name, and the seconds it had left.
/// </summary>
[DataDefinition]
public sealed partial class CarriedWireTimer
{
    [DataField] public string Action = string.Empty;

    [DataField] public int Rank;

    [DataField] public Enum Key = default!;

    [DataField] public string ExpiryType = string.Empty;

    [DataField] public string Expiry = string.Empty;

    [DataField] public float TimeLeft;
}
