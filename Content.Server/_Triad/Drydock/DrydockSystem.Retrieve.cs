using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Server._NF.Station.Components;
using Content.Server.Chemistry.Components;
using Content.Server.Database;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Gravity;
using Content.Server.Lathe.Components;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Maps;
using Content.Server.Power.EntitySystems;
using Content.Server.Power.Generator;
using Content.Server.Research.Systems;
using Content.Server.Station.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Wires;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._Shitmed.Autodoc.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared.Cabinet;
using Content.Shared.Chemistry;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Lathe;
using Content.Shared.Mind.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Power.Generator;
using Content.Shared.Research.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.SmartFridge;
using Content.Shared.Station.Components;
using Content.Shared.Xenoarchaeology.Equipment;
using Content.Shared.Xenoarchaeology.Equipment.Components;
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
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedShipRepairSystem _shipRepair = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private NPCSystem _npc = default!;
    [Dependency] private WiresSystem _wires = default!;
    [Dependency] private DeviceNetworkSystem _deviceNetwork = default!;
    [Dependency] private ResearchSystem _research = default!;
    [Dependency] private ShuttleConsoleLockSystem _consoleLock = default!;
    [Dependency] private GeneratorSystem _generator = default!;
    [Dependency] private SharedSmartFridgeSystem _smartFridge = default!;
    [Dependency] private SharedArtifactAnalyzerSystem _artifactAnalyzer = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private OpenableSystem _openable = default!;

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
    public async Task<DrydockRetrieve> TryRetrieveShip(Guid shipId, Guid ownerUserId, EntityUid stationUid, int? roundId)
    {
        // Read-only allows retrieve on purpose: it exists to stop a suspect build writing more bad
        // revisions, not to ground the fleet.
        if (!_cfg.GetCVar(TriadCCVars.DrydockEnabled))
            return DrydockRetrieve.Refused(DrydockRetrieveResult.Disabled);

        if (!TryComp<StationDataComponent>(stationUid, out var stationData)
            || _station.GetLargestGrid(stationData) is not { } targetGrid)
        {
            Log.Warning($"Drydock: retrieve of {shipId} refused, {ToPrettyString(stationUid)} is not a valid requesting station.");
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NoStation);
        }

        // The shipyard builds its staging map on the first purchase of a round and tears it down
        // at round end, so a retrieve before anyone has bought a ship would find none. Ask for it
        // the way a purchase does; the check after is for the map failing to come back at all.
        _shipyard.SetupShipyardIfNeeded();
        if (_shipyard.ShipyardMap is not { } shipyardMap)
        {
            Log.Error($"Drydock: retrieve of {shipId} refused, the shipyard could not stage a map.");
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NoStagingMap);
        }

        var current = await _store.LoadCurrent(shipId);
        if (current == null)
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NotFound);

        if (current.Ship.OwnerUserId != ownerUserId)
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NotOwned);

        // The console hides a ship under investigation; this is what actually refuses it. An
        // investigation is an admin's decision and a forged retrieve request must not walk past it.
        if (current.Ship.Investigating)
        {
            Log.Info($"Drydock: retrieve of {shipId} refused, the ship is under investigation.");
            return DrydockRetrieve.Refused(DrydockRetrieveResult.Investigating);
        }

        // The row's state names the refusal before the claim is tried. The claim below still
        // decides: this read can be stale by the time the claim lands.
        switch (current.Ship.State)
        {
            case DrydockShipState.CheckedOut:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.AlreadyOut);
            case DrydockShipState.Held:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.Held);
            case DrydockShipState.InEscrow:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.InEscrow);
            case DrydockShipState.Sold:
                return DrydockRetrieve.Refused(DrydockRetrieveResult.Sold);
        }

        // Claim before materializing. A ship that is checked out or held loses here.
        if (!await _store.TrySetState(shipId, DrydockShipState.Stored, DrydockShipState.CheckedOut,
                DrydockAuditAction.Retrieve, ownerUserId, roundId, null))
        {
            return DrydockRetrieve.Refused(DrydockRetrieveResult.NotStored);
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
                    return DrydockRetrieve.Refused(DrydockRetrieveResult.StationLost);
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
                return DrydockRetrieve.Refused(DrydockRetrieveResult.NoReadableRevision);
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

        return new DrydockRetrieve(DrydockRetrieveResult.Success, presented);
    }

    /// <summary>
    /// Puts back everything a normal spawn would have set up but a restored entity never gets.
    ///
    /// <para>This is the map-init boundary made concrete. A restored entity comes back already
    /// marked initialized, so <c>MapInitEvent</c> never fires for it again. That is deliberate and
    /// necessary, since re-firing it would re-run every one-shot spawner aboard. The cost is that
    /// anything a system only ever does on map init has to be done again here, by name.</para>
    ///
    /// <para>Most of them are ours rather than the reference's: a census of every map-init
    /// subscriber turned them up, and the test server turned up the console locks. Each one is a
    /// system whose entire runtime registration lives behind that event or the purchase path, so
    /// without this a retrieved ship comes back with dead machines that look perfectly fine, or a
    /// helm its own captain cannot unlock.</para>
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
        ReviveConsoleLocks(grid);
        ReviveGenerators(grid);
        ReviveSmartFridges(grid);
        ReviveArtifactAnalyzers(grid);
        ReviveFilledHands(grid);
        ReviveDispenserSlots(grid);
        ReviveCabinetLocks(grid);
        ScrubStaleLatheProduction(grid);

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
    /// A console lock and the grid lock beside it hold the ship's uid as a string, which is the
    /// one uid on the whole ship the loader cannot remap, so every locked console comes back keyed
    /// to a grid that no longer exists. The deed minted onto the card names the new grid, and the
    /// unlock compares the two as strings, so the captain's own deed would not open the helm
    /// (test server, 2026-09-06: "I cant unlock the ship with my deed"). The purchase and ship-load
    /// paths both stamp every console with the live uid; this is that stamp, and it re-locks the
    /// consoles the way both of them do.
    /// </summary>
    private void ReviveConsoleLocks(EntityUid grid)
    {
        var shuttleId = grid.ToString();
        var query = AllEntityQuery<ShuttleConsoleLockComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var lockComp, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            _consoleLock.SetShuttleId(uid, shuttleId, lockComp);
        }
    }

    /// <summary>
    /// A fuel generator's on flag persists, and the generator loop honours it, so a ship stored with
    /// its generators running comes back producing power. What does not persist is everything the
    /// flag drives: the running sprite, the ambient hum, the radiation source. A retrieved reactor
    /// therefore looked stopped while it ran, its captain pressed start, and start refused because
    /// it was already on (test server, 2026-09-06: "generators do not start", "z-pinches did not
    /// carry over their on state"; a signal toggle, which switches it off and on again, "worked").
    /// Re-applying the flag through the generator system re-derives all of it.
    /// </summary>
    private void ReviveGenerators(EntityUid grid)
    {
        var query = AllEntityQuery<FuelGeneratorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var generator, out var xform))
        {
            if (xform.GridUid != grid || !generator.On)
                continue;

            _generator.SetFuelGeneratorOn(uid, true, generator);
        }
    }

    /// <summary>
    /// A smart fridge's stock listing is an index over its container, rebuilt on map init because
    /// its key type cannot be a YAML mapping key. No map init here, so rebuild it by hand, or a
    /// stocked fridge reports itself empty and its contents are unreachable.
    /// </summary>
    private void ReviveSmartFridges(EntityUid grid)
    {
        var query = AllEntityQuery<SmartFridgeComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var fridge, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            _smartFridge.RebuildEntries((uid, fridge));
        }
    }

    /// <summary>
    /// An analysis console holds its analyzer as a NetEntity, which no loader remaps, and the only
    /// thing that re-resolves it from the device-link wire is the analyzer's map init. Without this
    /// the pair comes back linked on the wire and dead on the console.
    /// </summary>
    private void ReviveArtifactAnalyzers(EntityUid grid)
    {
        var query = AllEntityQuery<ArtifactAnalyzerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var analyzer, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            _artifactAnalyzer.RelinkConsole((uid, analyzer));
        }
    }

    /// <summary>
    /// A machine's hands are not data fields; the hand-fill component declares them and map init
    /// creates them. A retrieved robotic arm therefore had no hand to hold its tool in (test server,
    /// 2026-09-06: "interactors lose their handslot"). Re-create every declared hand that is missing.
    /// The fill items are not spawned again: whatever was in the hand persisted as a container
    /// child and is picked back up by the hand's container, and an empty hand was emptied on
    /// purpose.
    /// </summary>
    private void ReviveFilledHands(EntityUid grid)
    {
        var query = AllEntityQuery<HandsFillComponent, HandsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var fill, out var hands, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            foreach (var name in fill.Hands.Keys)
            {
                if (!_hands.TryGetHand(uid, name, out _, hands))
                    _hands.AddHand(uid, name, HandLocation.Middle, hands);
            }
        }
    }

    /// <summary>
    /// The item-slot registry is a read-only data field, so a slot added at runtime is never saved:
    /// only the prototype's own slots come back. A reagent dispenser registers its beaker slot on
    /// map init and its storage slots from its parts, so a retrieved one had jugs in containers no
    /// slot knew about and nowhere to put a new one (test server, 2026-09-06: "chemical dispensers
    /// break and lose all their contents"). The slot definitions themselves persist on the
    /// dispenser; this re-registers them, which finds the jugs already in their containers.
    /// </summary>
    private void ReviveDispenserSlots(EntityUid grid)
    {
        var query = AllEntityQuery<ReagentDispenserComponent, ItemSlotsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var dispenser, out var itemSlots, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            if (!_itemSlots.TryGetSlot(uid, SharedReagentDispenser.OutputSlotName, out _, itemSlots))
                _itemSlots.AddItemSlot(uid, SharedReagentDispenser.OutputSlotName, dispenser.BeakerSlot, itemSlots);

            var count = Math.Min(dispenser.StorageSlotIds.Count, dispenser.StorageSlots.Count);
            for (var i = 0; i < count; i++)
            {
                if (!_itemSlots.TryGetSlot(uid, dispenser.StorageSlotIds[i], out _, itemSlots))
                    _itemSlots.AddItemSlot(uid, dispenser.StorageSlotIds[i], dispenser.StorageSlots[i], itemSlots);
            }
        }
    }

    /// <summary>
    /// Same read-only registry: a slot's lock state is not saved either, and a cabinet locks its
    /// slot to its door on map init. A retrieved closed cabinet therefore handed out its contents
    /// through the closed door ("cabinets that require them to be opened no longer do").
    /// </summary>
    private void ReviveCabinetLocks(EntityUid grid)
    {
        var query = AllEntityQuery<ItemCabinetComponent, ItemSlotsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var cabinet, out var itemSlots, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            _itemSlots.SetLock(uid, cabinet.Slot, !_openable.IsOpen(uid), itemSlots);
        }
    }

    /// <summary>
    /// Scrub for documents written before the producing marker opted out of saving. A lathe stored
    /// mid-print reloaded carrying the marker with no recipe behind it: the lathe loop skips a
    /// producing lathe with no recipe and the reboot pass skips any lathe that is producing, so it
    /// neither finished nor restarted, forever. Dropping the marker lets the reboot pass resume the
    /// queue, which is the state that actually persisted.
    /// </summary>
    private void ScrubStaleLatheProduction(EntityUid grid)
    {
        var query = AllEntityQuery<LatheProducingComponent, LatheComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var lathe, out var xform))
        {
            if (xform.GridUid != grid || lathe.CurrentRecipe != null)
                continue;

            RemCompDeferred<LatheProducingComponent>(uid);
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
        // The roundstart variation passes run on every new station and read this marker off the
        // station, which is recreated below, so a retrieved ship was re-varied on every retrieve:
        // fresh trash and spills each time, some of it inside the hull. The marker on the grid is
        // what the rule now honours, and it rides the document, so a ship is varied once at most.
        // Stamped before the vessel check: a stationless retrieve must not be varied later either.
        EnsureComp<StationVariationHasRunComponent>(grid);

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
