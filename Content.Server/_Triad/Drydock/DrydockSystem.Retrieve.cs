using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Server._NF.Station.Components;
using Content.Server.Database;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Gravity;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Maps;
using Content.Server.Power.EntitySystems;
using Content.Server.Research.Systems;
using Content.Server.Station.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Wires;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared.Damage;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Mind.Components;
using Content.Shared.Research.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Station.Components;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The retrieve half: claim the ship's row, verify a revision, materialize it on the shipyard's
/// staging map, revive the state that a normal spawn would have set up, and present it at the
/// requesting station.
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedShipRepairSystem _shipRepair = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly WiresSystem _wires = default!;
    [Dependency] private readonly DeviceNetworkSystem _deviceNetwork = default!;
    [Dependency] private readonly ResearchSystem _research = default!;

    /// <summary>
    /// Retrieves a stored ship and presents it at <paramref name="stationUid"/>.
    ///
    /// <para>The claim is a single conditional state transition, and it happens before anything is
    /// materialized. That is what makes two simultaneous retrieves safe: both ask the database to
    /// move the row from stored to checked out, exactly one wins, and the loser never touches a map.
    /// The reference implementation used a process-local dictionary for this, which does not survive
    /// a restart and does not span two server processes; a row does both.</para>
    ///
    /// <para>Every failure after the claim releases it, or the ship would be unretrievable until an
    /// administrator noticed.</para>
    /// </summary>
    public async Task<EntityUid?> TryRetrieveShip(Guid shipId, Guid ownerUserId, EntityUid stationUid, int? roundId)
    {
        // Read-only allows retrieve on purpose: it exists to stop a suspect build writing more bad
        // revisions, not to ground the fleet.
        if (!_cfg.GetCVar(TriadCCVars.DrydockEnabled))
            return null;

        if (!TryComp<StationDataComponent>(stationUid, out var stationData)
            || _station.GetLargestGrid(stationData) is not { } targetGrid)
        {
            Log.Warning($"Drydock: retrieve of {shipId} refused, {ToPrettyString(stationUid)} is not a valid requesting station.");
            return null;
        }

        if (_shipyard.ShipyardMap is not { } shipyardMap)
        {
            Log.Error($"Drydock: retrieve of {shipId} refused, there is no shipyard staging map.");
            return null;
        }

        var current = await _store.LoadCurrent(shipId);
        if (current == null || current.Ship.OwnerUserId != ownerUserId)
            return null;

        // The console hides a ship under investigation; this is what actually refuses it. An
        // investigation is an admin's decision and a forged retrieve request must not walk past it.
        if (current.Ship.Investigating)
        {
            Log.Info($"Drydock: retrieve of {shipId} refused, the ship is under investigation.");
            return null;
        }

        // Claim before materializing. A ship that is checked out or held loses here.
        if (!await _store.TrySetState(shipId, DrydockShipState.Stored, DrydockShipState.CheckedOut,
                DrydockAuditAction.Retrieve, ownerUserId, roundId, null))
        {
            return null;
        }

        var claimed = true;
        EntityUid? presented = null;
        try
        {
            var keepBlobs = _cfg.GetCVar(TriadCCVars.DrydockKeepBlobs);
            var oldest = keepBlobs > 0 ? Math.Max(1, current.Ship.CurrentRevision - keepBlobs + 1) : 1;

            for (var revision = current.Ship.CurrentRevision; revision >= oldest; revision--)
            {
                var stored = revision == current.Ship.CurrentRevision
                    ? current
                    : await _store.LoadRevision(shipId, revision);

                if (stored == null)
                    continue;

                byte[] yamlBytes;
                try
                {
                    yamlBytes = DecompressZstd(stored.Blob);
                }
                catch (Exception e)
                {
                    Log.Error($"Drydock: {shipId} revision {revision} would not decompress, falling back: {e.Message}");
                    continue;
                }

                if (!SHA256.HashData(yamlBytes).AsSpan().SequenceEqual(stored.Revision.Checksum))
                {
                    Log.Error($"Drydock: {shipId} revision {revision} failed its checksum, falling back.");
                    continue;
                }

                // A fallback is a retrieve of an older state than the one the player last put
                // away, and the newer state is still on disk for now. It goes on the timeline so
                // an admin can see it before pruning takes the skipped document, because a
                // fallback followed by a few ordinary stores is how a latest state disappears.
                if (revision != current.Ship.CurrentRevision)
                {
                    Log.Warning($"Drydock: {shipId} retrieved from fallback revision {revision}; revision {current.Ship.CurrentRevision} is unreadable.");
                    await _store.WriteAudit(new DrydockAudit
                    {
                        ShipGuid = shipId,
                        ShipName = current.Ship.ShipName,
                        BerthId = current.Ship.BerthId,
                        Action = DrydockAuditAction.Fallback,
                        ActorUserId = ownerUserId,
                        Revision = revision,
                        RoundId = roundId,
                        Reason = $"revision {current.Ship.CurrentRevision} would not load; retrieved from {revision}",
                    });
                }

                using var reader = new StreamReader(new MemoryStream(yamlBytes), Encoding.UTF8);

                // Space the load out along the staging map rather than dropping every retrieve on
                // the same spot. The ship is docked away synchronously below, so it occupies the
                // slot for part of one tick, but two grids landing on top of each other for even
                // that long is not worth the physics.
                var offset = new System.Numerics.Vector2(1000f * (CountStagedGrids(shipyardMap) + 1), 0f);

                if (!_mapLoader.TryLoadGrid(shipyardMap, reader, $"drydock/{shipId}", out var loaded, offset: offset))
                {
                    Log.Error($"Drydock: {shipId} revision {revision} passed its checksum but would not load.");
                    continue;
                }

                var grid = loaded!.Value.Owner;

                // A document with no shuttle component describes something that cannot dock or fly.
                // Treat it as an unusable revision and try the one before it.
                if (!TryComp<ShuttleComponent>(grid, out var shuttle))
                {
                    Del(grid);
                    Log.Error($"Drydock: {shipId} revision {revision} has no shuttle component, falling back.");
                    continue;
                }

                // The station was checked before the database work, and those awaits are the one
                // window in which it can die. Everything from here to the dock is synchronous, so
                // one re-check covers it.
                if (!Exists(targetGrid))
                {
                    Del(grid);
                    Log.Warning($"Drydock: {shipId} had its dock target die mid-retrieve; refused.");
                    return null;
                }

                try
                {
                    Revive(grid, stored.Ship);

                    if (!_shuttle.TryFTLDock(grid, shuttle, targetGrid))
                        Log.Warning($"Drydock: {shipId} found no docking config at {ToPrettyString(stationUid)}; presented by proximity.");

                    claimed = false; // The claim is now correct: the ship really is out.
                    presented = grid;
                }
                catch
                {
                    // A throw here would otherwise leave a live grid stranded on the staging map
                    // while the claim is released, which is the duplicate this whole gate exists to
                    // prevent. Scrap it, then let the failure travel.
                    Del(grid);
                    throw;
                }

                break;
            }

            if (presented == null)
            {
                Log.Error($"Drydock: {shipId} has no revision that verifies; retrieve refused.");
                return null;
            }
        }
        finally
        {
            if (claimed)
            {
                await _store.TrySetState(shipId, DrydockShipState.CheckedOut, DrydockShipState.Stored,
                    DrydockAuditAction.Release, null, roundId, "retrieve failed");
            }
        }

        // The berth empties only now, after the ship is docked and the claim is confirmed, and
        // never inside the claim: a failure after the claim releases the state without ever having
        // to re-seat a berth somebody else may have taken. If this write fails the ship is out and
        // still shown in its slot, which its next store heals and an admin move can fix; a ship
        // that is already docked is not scrapped over a bookkeeping column.
        try
        {
            await _store.VacateBerth(shipId);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: {shipId} is out but its berth could not be vacated: {e.Message}");
        }

        return presented;
    }

    /// <summary>
    /// Puts back everything a normal spawn would have set up but a restored entity never gets.
    ///
    /// <para>This is the map-init boundary made concrete. A restored entity comes back already
    /// marked initialized, so <c>MapInitEvent</c> never fires for it again. That is deliberate and
    /// necessary, since re-firing it would re-run every one-shot spawner aboard. The cost is that
    /// anything a system only ever does on map init has to be done again here, by name.</para>
    ///
    /// <para>Six of them, and the last three are ours rather than the reference's: a census of every
    /// map-init subscriber turned them up. Each one is a system whose entire runtime registration
    /// lives behind that event, so without this a retrieved ship comes back with dead machines that
    /// look perfectly fine.</para>
    /// </summary>
    private void Revive(EntityUid grid, DrydockShip record)
    {
        // The general fidelity net first: everything captured into a sidecar goes back before
        // anything else reads component state.
        var restore = _fidelity.RestoreCaptured(grid);
        if (restore.Skipped.Count > 0)
            Log.Warning($"Drydock: {record.ShipGuid} restored with {restore.Skipped.Count} captured field(s) skipped.");

        // Scrubs for documents written before the store learned to strip these. A stale FTL
        // component leaves the ship stuck mid-jump with the shuttle system erroring every tick, and
        // a stale in-progress marker blocks every container aboard, hands included.
        if (HasComp<FTLComponent>(grid))
            RemComp<FTLComponent>(grid);

        if (HasComp<DrydockInProgressComponent>(grid))
            RemComp<DrydockInProgressComponent>(grid);

        ReviveGravity(grid);
        ReviveNpcs(grid);
        ReviveWires(grid);
        ReviveDeviceNetwork(grid);
        ReviveResearchClients(grid);

        RehydrateDamage(grid);

        // The repair baseline is derived state, stripped at store. Retrieve fires neither map init
        // nor the purchase event, and the repair system subscribes only to the latter.
        _shipRepair.GenerateRepairData(grid);

        // The row is authoritative for the name too: a rename made while the ship was stored is a
        // row update, and this is where the hull and its deed learn it. Before the station, which
        // takes its name from the grid.
        _shipyard.StampStoredName(grid, record.ShipName);
        RefreshShipOwnership(grid, record);
        RecreateStation(grid, record);
    }

    /// <summary>
    /// A gravity generator pushes gravity onto its grid only on the edge where its charge
    /// activates. On load the charge comes back already full, so the loop sees no edge and never
    /// pushes, while the generator's own active flag is not serialized and reads false. The result
    /// is a live generator and no gravity. Re-raising the activation lets its own handler do the
    /// work, which matters because the component is access-locked to that system.
    /// </summary>
    private void ReviveGravity(EntityUid grid)
    {
        var query = AllEntityQuery<GravityGeneratorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            var activated = new ChargedMachineActivatedEvent();
            RaiseLocalEvent(uid, ref activated);
        }
    }

    /// <summary>
    /// Autopilot is an HTN behaviour on the shuttle console, and turrets and drones are HTN too.
    /// The active marker and the blackboard's owner are installed only on map init, so without this
    /// a restored ship's autopilot never plans and never steers.
    ///
    /// <para>The stored autopilot destination is dropped deliberately: a ship that has been sitting
    /// in a drydock has no business resuming a course to a point that may no longer mean
    /// anything.</para>
    /// </summary>
    private void ReviveNpcs(EntityUid grid)
    {
        var query = AllEntityQuery<HTNComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var htn, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            // A minded HTN should not have survived the organics gate, but the NPC system refuses to
            // wake one anyway, so match that rather than fight it.
            if (TryComp<MindContainerComponent>(uid, out var mind) && mind.HasMind)
                continue;

            htn.Blackboard.SetValue(NPCBlackboard.Owner, uid);

            if (TryComp<ShuttleConsoleComponent>(uid, out var console))
                htn.Blackboard.Remove<EntityCoordinates>(console.AutopilotTargetKey);

            _npc.WakeNPC(uid, htn);
        }
    }

    /// <summary>
    /// A wired machine's actual wire list is not a data field, so it does not persist, and the only
    /// thing that ever built it was map init. Without this every panel on a restored ship opens
    /// empty: nothing to cut, nothing to pulse, on every airlock and every APC aboard.
    /// </summary>
    private void ReviveWires(EntityUid grid)
    {
        var query = AllEntityQuery<WiresComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var wires, out var xform))
        {
            if (xform.GridUid != grid || string.IsNullOrEmpty(wires.LayoutId))
                continue;

            _wires.SetOrCreateWireLayout(uid, wires);
        }
    }

    /// <summary>
    /// Device network membership is runtime registration held by the network system, not state on
    /// the device, and joining happens on map init. Without this a restored ship's air alarms,
    /// sensors and consoles are all present, all powered, and all deaf.
    /// </summary>
    private void ReviveDeviceNetwork(EntityUid grid)
    {
        var query = AllEntityQuery<DeviceNetworkComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var device, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            _deviceNetwork.ConnectDevice(uid, device);
        }
    }

    /// <summary>
    /// A research client's server link is a plain property rather than a data field, and the
    /// registration that sets it runs on map init by scanning the client's own grid for servers.
    /// This repeats that scan, which is the same shape and therefore the same result.
    /// </summary>
    private void ReviveResearchClients(EntityUid grid)
    {
        var servers = new List<Entity<ResearchServerComponent>>();
        var serverQuery = AllEntityQuery<ResearchServerComponent, TransformComponent>();
        while (serverQuery.MoveNext(out var serverUid, out var server, out var serverXform))
        {
            if (serverXform.GridUid == grid)
                servers.Add((serverUid, server));
        }

        if (servers.Count == 0)
            return;

        var clientQuery = AllEntityQuery<ResearchClientComponent, TransformComponent>();
        while (clientQuery.MoveNext(out var uid, out var client, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            foreach (var server in servers)
                _research.RegisterClient(uid, server.Owner, client, server.Comp);
        }
    }

    /// <summary>
    /// Applies each damage sidecar back onto its holder and removes it.
    ///
    /// <para>Run from this explicit pass rather than a startup hook on purpose. Applying damage at
    /// component startup fires the damage-changed event into the destructible system while the grid
    /// is still settling, which risks tripping a destruction threshold against state that is not
    /// final yet. Running after the load has returned lets thresholds see a finished ship.</para>
    /// </summary>
    private void RehydrateDamage(EntityUid grid)
    {
        var query = AllEntityQuery<DrydockDamageSidecarComponent, DamageableComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var sidecar, out var damageable, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            var damage = new DamageSpecifier { DamageDict = new Dictionary<string, FixedPoint2>(sidecar.DamageDict) };
            _damageable.SetDamage(uid, damageable, damage);
            RemComp<DrydockDamageSidecarComponent>(uid);
        }
    }

    /// <summary>
    /// The row is authoritative for ownership, and this is where the grid learns it. The
    /// ownership component rides the document, so without this a transferred ship would come back
    /// stamped with its previous owner, the console would refuse the new owner's store as "not
    /// yours", and a store by the old owner would file the row back under them. Its last-status
    /// timestamp is round-scoped absolute time too, and a previous round's clock would feed the
    /// offline-deletion timer nonsense, so that is re-derived here as well.
    /// </summary>
    private void RefreshShipOwnership(EntityUid grid, DrydockShip record)
    {
        var ownership = EnsureComp<ShipOwnershipComponent>(grid);

        ownership.OwnerUserId = new NetUserId(record.OwnerUserId);
        ownership.IsOwnerOnline = _player.TryGetSessionById(ownership.OwnerUserId, out _);
        ownership.LastStatusChangeTime = _timing.CurTime;
        Dirty(grid, ownership);
    }

    /// <summary>
    /// A ship is its own station only when its vessel has a matching map prototype. The ship
    /// persists and the station is recreated, because a station is round-scoped. The ship's own
    /// name is passed through so a player's rename survives rather than being replaced by the
    /// prototype's name generator. A document with no vessel prototype comes back stationless, and
    /// its serialized membership is removed rather than left dangling and lying to whatever reads
    /// it.
    /// </summary>
    private void RecreateStation(EntityUid grid, DrydockShip record)
    {
        if (string.IsNullOrEmpty(record.VesselProto)
            || !_protoMan.TryIndex<GameMapPrototype>(record.VesselProto, out var stationProto)
            || !stationProto.Stations.TryGetValue(record.VesselProto, out var stationConfig))
        {
            Log.Info($"Drydock: {record.ShipGuid} retrieved stationless (vessel prototype '{record.VesselProto}').");
            RemComp<StationMemberComponent>(grid);
            return;
        }

        var station = _station.InitializeNewStation(stationConfig, new[] { grid }, Name(grid));
        EnsureComp<ExtraShuttleInformationComponent>(station).Vessel = record.VesselProto;
    }

    private int CountStagedGrids(MapId map)
    {
        var count = 0;
        var query = AllEntityQuery<Robust.Shared.Map.Components.MapGridComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var xform))
        {
            if (xform.MapID == map)
                count++;
        }

        return count;
    }

    private static byte[] DecompressZstd(byte[] input)
    {
        using var decompress = new Robust.Shared.Utility.ZStdDecompressStream(new MemoryStream(input));
        using var output = new MemoryStream();
        decompress.CopyTo(output);
        return output.ToArray();
    }
}
