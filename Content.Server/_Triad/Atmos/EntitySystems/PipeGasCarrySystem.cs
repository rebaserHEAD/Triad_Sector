using System.Linq;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.NodeGroups;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.Atmos;
using Content.Shared.NodeContainer;
using Content.Shared.NodeContainer.NodeGroups;

namespace Content.Server._Triad.Atmos.EntitySystems;

/// <summary>
/// Carries each pipe net's gas across a drydock store and load. A net's air lives on its node group, which no component
/// holds, so the store gives every kept pipe node its share of its net's air, keyed by node name because a two-port device
/// sits in two nets, and the load pours each share back into the net its node joins.
///
/// <para>The split is by volume, in one pass over everything the store keeps, taken at the store's first node container
/// and held for the rest of that store, because nets are keyed by live group identity and groups are rebuilt between
/// ticks (<c>NodeGroupSystem.DoGroupUpdates</c>), so a sliced store must not read a net twice. A net's
/// air belongs to the stored grid in proportion to the volume of its nodes that go with the grid, which is every node not
/// on another grid: an unsavable member on this grid is deleted with it, so its share goes to the kept members, while a
/// member docked on another grid keeps its share, since <c>PipeNet.RemoveNode</c> leaves the surviving net that fraction.
/// This holds whatever the caller does about docks; the console store undocks before the store runs, and the undock's own
/// handler empties a docked net (<c>DockablePipeSystem.OnUndock</c>), which is what live play does too.</para>
///
/// <para>The store only reads: nothing is added to or dirtied on the live grid.</para>
/// </summary>
public sealed partial class PipeGasCarrySystem : EntitySystem
{
    /// <summary>The carried key: each pipe node's share, by node name.</summary>
    public const string SharesKey = "PipeGas.Shares";

    [Dependency] private AtmosphereSystem _atmosphere = default!;

    /// <summary>One store's shares, by entity, taken once per store and gone with it.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DrydockStoreToken, Dictionary<EntityUid, Dictionary<string, GasMixture>>> _split = new();

    /// <summary>Restored shares whose node had no group yet, by entity, waiting for its first rebuild.</summary>
    private readonly Dictionary<EntityUid, Dictionary<string, GasMixture>> _held = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NodeContainerComponent, GridStoringEvent>(OnStoring);
        SubscribeLocalEvent<NodeContainerComponent, GridRestoredEvent>(OnRestored);
        SubscribeLocalEvent<NodeContainerComponent, NodeGroupsRebuilt>(OnNodeGroupsRebuilt);
        SubscribeLocalEvent<NodeContainerComponent, EntityTerminatingEvent>(OnTerminating);
    }

    /// <summary>Whether a restored share of <paramref name="uid"/> is still waiting for its node's first rebuild.</summary>
    internal bool Holds(EntityUid uid) => _held.ContainsKey(uid);

    private void OnStoring(Entity<NodeContainerComponent> ent, ref GridStoringEvent args)
    {
        var grid = args.Grid;
        var shares = _split.GetValue(args.Store, token => Split(grid, token));
        if (shares.TryGetValue(ent.Owner, out var mine))
            args.Carry(SharesKey, mine);
    }

    /// <summary>
    /// Every kept pipe node's share of its net, in one pass. A net holding no gas gives no share, since pouring nothing
    /// changes nothing.
    /// </summary>
    private Dictionary<EntityUid, Dictionary<string, GasMixture>> Split(EntityUid grid, DrydockStoreToken token)
    {
        var nets = new Dictionary<IPipeNet, List<(EntityUid Owner, string Name, PipeNode Pipe)>>(ReferenceEqualityComparer.Instance);
        foreach (var uid in token.Kept)
        {
            if (!TryComp<NodeContainerComponent>(uid, out var container))
                continue;

            foreach (var (name, node) in container.Nodes)
            {
                if (node is not PipeNode { NodeGroup: IPipeNet net } pipe)
                    continue;

                if (!nets.TryGetValue(net, out var members))
                    nets[net] = members = new List<(EntityUid, string, PipeNode)>();

                members.Add((uid, name, pipe));
            }
        }

        var shares = new Dictionary<EntityUid, Dictionary<string, GasMixture>>();
        foreach (var (net, members) in nets)
        {
            var air = net.Air;
            if (air.TotalMoles <= 0f)
                continue;

            var keptVolume = 0f;
            foreach (var (_, _, pipe) in members)
                keptVolume += pipe.Volume;

            var netVolume = 0f;
            var survivingVolume = 0f;
            foreach (var node in net.Nodes)
            {
                if (node is not PipeNode pipe)
                    continue;

                netVolume += pipe.Volume;
                if (Transform(pipe.Owner).GridUid != grid)
                    survivingVolume += pipe.Volume;
            }

            if (keptVolume <= 0f || netVolume <= 0f)
                continue;

            var portion = (netVolume - survivingVolume) / netVolume;
            foreach (var (owner, name, pipe) in members)
            {
                var share = new GasMixture(air) { Volume = pipe.Volume };
                share.Multiply(portion * pipe.Volume / keptVolume);

                if (!shares.TryGetValue(owner, out var byName))
                    shares[owner] = byName = new Dictionary<string, GasMixture>();

                byName[name] = share;
            }
        }

        return shares;
    }

    private void OnRestored(Entity<NodeContainerComponent> ent, ref GridRestoredEvent args)
    {
        if (!args.TryGetCarried<Dictionary<string, GasMixture>>(SharesKey, out var shares) || shares.Count == 0)
            return;

        // Node groups are built by the first update after the load, not during it, so a share usually waits here for its
        // node's first rebuild. The table is system state: the staging map is paused and that rebuild may come ticks later.
        _held[ent.Owner] = shares;
        Pour(ent);
    }

    private void OnNodeGroupsRebuilt(Entity<NodeContainerComponent> ent, ref NodeGroupsRebuilt args)
    {
        Pour(ent);
    }

    private void OnTerminating(Entity<NodeContainerComponent> ent, ref EntityTerminatingEvent args)
    {
        _held.Remove(ent.Owner);
    }

    /// <summary>
    /// Merges each held share whose node now has a net into that net, never assigns, since every member of a net pours its
    /// own share and the net sums back from all of them. A share is removed from the table in the same call that pours
    /// it: <see cref="NodeGroupsRebuilt"/> fires again on every later cut, weld or anchor, and a share left behind would
    /// be merged again each time. A share whose node the entity no longer has is dropped, since it can never be poured.
    /// </summary>
    private void Pour(Entity<NodeContainerComponent> ent)
    {
        if (!_held.TryGetValue(ent.Owner, out var shares))
            return;

        foreach (var name in shares.Keys.ToList())
        {
            if (!ent.Comp.Nodes.TryGetValue(name, out var node) || node is not PipeNode pipe)
            {
                shares.Remove(name);
                continue;
            }

            if (pipe.NodeGroup == null)
                continue;

            _atmosphere.Merge(pipe.Air, shares[name]);
            shares.Remove(name);
        }

        if (shares.Count == 0)
            _held.Remove(ent.Owner);
    }
}
