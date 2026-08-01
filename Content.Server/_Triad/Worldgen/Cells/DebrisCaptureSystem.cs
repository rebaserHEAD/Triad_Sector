// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Content.Server.Worldgen.Systems;
using Content.Server.Worldgen.Systems.Debris;
using Content.Server.Worldgen.Systems.GC;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Worldgen;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mind.Components;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     The capture rung of the two-rung ladder: a debris grid that was touched while
///     materialized is serialized to a compressed blob at cell unload instead of being rolled
///     back to its seed, and the blob is what materializes next visit. This is what makes
///     "rocks don't grow back rock" true for consumption: without it, unload regenerates every
///     looted crate and mined vein from the seed, which is a duplication exploit.
///
///     Untouched grids never pay for this. The Touched flag is event-driven and trigger-happy
///     (tile changes, anchor changes, any reparent under the grid including container looting,
///     any click interaction with something aboard), because the failure costs are asymmetric:
///     a false positive is a few KB of blob, a false negative regenerates someone's loot or
///     loses their construction.
///
///     Captures queue at cell unload and drain in Update under a time budget, so a burst of
///     unloading cells never stacks whole-grid serializes into one tick. Every queued grid
///     holds itself against the GC (TryCancelGC) until its capture succeeds or exhausts its
///     retries, so the deferral is free of data-loss races by construction.
///
///     The store is round-scoped server memory, same methodology as the shipyard save system
///     (engine serializer in-memory, MissingEntityBehaviour.Ignore, never a hand-rolled diff),
///     minus the signing: these blobs never leave the server, so there is nothing to tamper with.
/// </summary>
public sealed class DebrisCaptureSystem : EntitySystem
{
    [Dependency] private readonly CellDescribeSystem _describe = default!;
    [Dependency] private readonly GCQueueSystem _gc = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _xformSys = default!;

    /// <summary>
    ///     Serialization attempts a grid gets before it is released to the GC uncaptured. Three
    ///     is enough to ride out a transient failure without pinning an entity forever against a
    ///     deterministically broken serialize.
    /// </summary>
    public const byte MaxCaptureAttempts = 3;

    private long _budgetBytes;
    private float _captureBudgetMs;

    private readonly record struct CaptureEntry(byte[] Data, long Stamp);

    /// <summary>Compressed grid blobs keyed by record id. Round-scoped; never touches disk.</summary>
    private readonly Dictionary<int, CaptureEntry> _blobs = new();

    /// <summary>
    ///     Captures owed but not yet performed, drained by <see cref="Update"/> under the time
    ///     budget. The GC hold (<see cref="OnTryCancelGC"/>) keeps every queued grid alive until
    ///     its capture resolves, so deferral never loses state to the collector.
    /// </summary>
    private readonly Queue<DebrisRecord> _pending = new();

    private readonly HashSet<int> _pendingIds = new();

    /// <summary>
    ///     Eviction order, oldest stamp first. Lazily maintained: entries whose blob was dropped
    ///     or overwritten go stale in place and are skipped on pop, which keeps every mutation
    ///     O(log n) instead of rebuilding on each drop.
    /// </summary>
    private readonly PriorityQueue<int, long> _evictOrder = new();

    /// <summary>Scratch for the drain pass's mind check; rebuilt once per pass, not per rock.</summary>
    private readonly HashSet<EntityUid> _mindedGrids = new();

    private long _totalBytes;
    private long _stamp;

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, TriadCCVars.WorldgenCaptureBudgetMb, v => _budgetBytes = v * 1024L * 1024L, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenCaptureBudgetMs, v => _captureBudgetMs = v, true);

        // Before the placer: its handler queues these grids for GC, and enqueue-then-collect is
        // the honest order. Not load-bearing, though: the GC hold below vouches on the Touched
        // flag itself, so a grid is protected even if its pending entry lands late.
        SubscribeLocalEvent<CellRecordsComponent, WorldChunkUnloadedEvent>(OnCellUnloaded,
            before: [typeof(DebrisFeaturePlacerSystem)]);

        // The hold that makes deferred capture safe: a queued or retrying grid cancels its own
        // collection until the capture resolves. Distinct (component, event) pair from the
        // placer's OwnedDebris handler, so both get asked and either can cancel.
        SubscribeLocalEvent<RecordedDebrisComponent, TryCancelGC>(OnTryCancelGC);

        SubscribeLocalEvent<RecordedDebrisComponent, TileChangedEvent>(OnTileChanged);
        SubscribeLocalEvent<EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<AnchorStateChangedEvent>(OnAnchorChanged);

        // The interaction funnel: clicks on anything aboard a recorded grid. This is what catches
        // in-place depletion (siphoning a canister, draining a battery, emptying a solution),
        // which mutates component data without moving a tile, an anchor or a parent.
        SubscribeLocalEvent<ActivateInWorldEvent>(OnActivateInWorld);
        SubscribeLocalEvent<InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<InteractHandEvent>(OnInteractHand);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    public bool HasBlob(int recordId)
    {
        return _blobs.ContainsKey(recordId);
    }

    /// <summary>
    ///     The decompressed yaml of a stored blob, for tests and admin debugging. Never on a hot
    ///     path.
    /// </summary>
    public bool TryGetBlobYaml(int recordId, out string yaml)
    {
        yaml = string.Empty;

        if (!_blobs.TryGetValue(recordId, out var entry))
            return false;

        using var input = new MemoryStream(entry.Data, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        yaml = reader.ReadToEnd();
        return true;
    }

    /// <summary>
    ///     Forces the dirty bit, for tests and admin debugging (isolating one classifier
    ///     trigger needs a clean baseline). Never on a hot path.
    /// </summary>
    public void DebugSetTouched(EntityUid gridUid, bool value)
    {
        if (TryComp<RecordedDebrisComponent>(gridUid, out var recorded))
            recorded.Touched = value;
    }

    public void Drop(int recordId)
    {
        if (!_blobs.Remove(recordId, out var entry))
            return;

        _totalBytes -= entry.Data.Length;
        RecordMetrics.CaptureBlobs.Set(_blobs.Count);
        RecordMetrics.CaptureBytes.Set(_totalBytes);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _blobs.Clear();
        _pending.Clear();
        _pendingIds.Clear();
        _evictOrder.Clear();
        _totalBytes = 0;
        RecordMetrics.CaptureBlobs.Set(0);
        RecordMetrics.CaptureBytes.Set(0);
    }

    // Cell retirement (the kill switch, map teardown) drops this store's blobs from
    // CellDescribeSystem.OnCellShutdown: directed events allow one handler per
    // (component, event) pair, and the describe system owns that one.

    // ---- Dirty classifier ------------------------------------------------------------------
    //
    // All three triggers share a natural spawn guard: initial population (blob builder tiles,
    // interior spawns, blob restore) happens inside the spawn or load call, before
    // RecordedDebrisComponent is added to the grid, so building the rock never marks it touched.
    // A spawner that defers past that window costs one false-positive blob, which is the
    // accepted direction.

    private void OnTileChanged(EntityUid uid, RecordedDebrisComponent component, ref TileChangedEvent args)
    {
        component.Touched = true;
    }

    /// <summary>
    ///     Anything anchoring to or unanchoring from the grid, which includes destructible
    ///     deletion of anchored children: mining a wall rock detaches it on the way out, and
    ///     that detach is the only event pure wall-mining reliably raises (the tile under an
    ///     entity wall never changes). Grid teardown raises the same detaches for every anchored
    ///     child, hence the terminating gate.
    /// </summary>
    private void OnAnchorChanged(ref AnchorStateChangedEvent args)
    {
        if (args.Transform.GridUid is not { } grid)
            return;

        if (TryComp<RecordedDebrisComponent>(grid, out var recorded) && !Terminating(grid))
            recorded.Touched = true;
    }

    /// <summary>
    ///     Anything re-parenting under a recorded grid, checked at grid altitude rather than
    ///     direct-parent altitude on purpose: taking loot from a crate reparents crate-to-hand
    ///     and the grid never appears as either parent, so comparing parents directly misses
    ///     every container transfer, and a refilled crate is exactly the duplication this system
    ///     exists to close. Both sides are checked so a pickup by someone floating off the rock
    ///     (old side resolves through the crate) and a drop onto it (new side resolves through
    ///     the entity's own position) each land.
    /// </summary>
    private void OnParentChanged(ref EntParentChangedMessage args)
    {
        if (args.OldParent is { } old && TryComp<TransformComponent>(old, out var oldXform)
            && oldXform.GridUid is { } oldGrid)
        {
            MarkTouched(oldGrid, args.Entity);
        }

        if (args.Transform.GridUid is { } newGrid)
            MarkTouched(newGrid, args.Entity);
    }

    // The interaction funnel's three broadcast events (all raised with broadcast on in
    // SharedInteractionSystem): clicking anything aboard a recorded grid marks it, whether or not
    // a handler claims the click. This is deliberately blunt; per the classifier's asymmetry, a
    // wasted blob beats a regenerated canister. Verb-only paths that mutate state without a
    // click or any other trigger remain the known, accepted gap.

    private void OnActivateInWorld(ActivateInWorldEvent args)
    {
        MarkInteracted(args.Target);
    }

    private void OnInteractUsing(InteractUsingEvent args)
    {
        MarkInteracted(args.Target);
    }

    private void OnInteractHand(InteractHandEvent args)
    {
        MarkInteracted(args.Target);
    }

    private void MarkInteracted(EntityUid target)
    {
        if (TryComp<TransformComponent>(target, out var xform) && xform.GridUid is { } grid)
            MarkTouched(grid, target);
    }

    private void MarkTouched(EntityUid grid, EntityUid mover)
    {
        // The grid moving itself (map transfer, FTL bookkeeping) is not someone touching it.
        if (mover == grid)
            return;

        if (TryComp<RecordedDebrisComponent>(grid, out var recorded) && !Terminating(grid))
            recorded.Touched = true;
    }

    // ---- Capture at unload -----------------------------------------------------------------

    private void OnCellUnloaded(EntityUid uid, CellRecordsComponent cell, ref WorldChunkUnloadedEvent args)
    {
        foreach (var record in cell.Records.Values)
        {
            if (record.State != RecordState.Materialized || record.Entity is not { } ent || !Exists(ent))
                continue;

            if (!TryComp<RecordedDebrisComponent>(ent, out var recorded) || !recorded.Touched)
                continue;

            // Only grids capture: TryLoadGrid is the restore path and it loads grids. Non-grid
            // debris (marker spawners) has no interior state worth a blob and stays on the
            // rollback rung whatever touched it.
            if (!HasComp<MapGridComponent>(ent))
                continue;

            TryEnqueue(record);
        }
    }

    private void TryEnqueue(DebrisRecord record)
    {
        if (_pendingIds.Add(record.Id))
            _pending.Enqueue(record);
    }

    /// <summary>
    ///     The hold: a grid that still owes a capture cancels its own collection. Cancelled
    ///     entries leave the GC queue for good, so <see cref="Update"/> re-queues held grids
    ///     once their capture resolves; GcHeld is the receipt for that debt. At the attempt cap
    ///     the hold releases and the loss is loud, because pinning an entity forever against a
    ///     deterministically failing serializer is a worse leak than a logged regen.
    /// </summary>
    private void OnTryCancelGC(EntityUid uid, RecordedDebrisComponent recorded, ref TryCancelGC args)
    {
        if (!recorded.Touched || recorded.CaptureAttempts >= MaxCaptureAttempts)
            return;

        if (recorded.Record is not { } record || record.Entity != uid)
            return;

        // Non-grid debris never captures (no blob to owe), so it never holds either.
        if (!HasComp<MapGridComponent>(uid))
            return;

        args.Cancelled = true;
        recorded.GcHeld = true;
        TryEnqueue(record);
    }

    /// <summary>
    ///     Drains owed captures under the time budget, so a fleet-departure burst of unloading
    ///     cells spreads its serialization across ticks instead of stacking whole-grid
    ///     serializes into one. Safe to defer because every queued grid holds itself against GC
    ///     until its capture resolves.
    /// </summary>
    public override void Update(float frameTime)
    {
        if (_pending.Count == 0)
            return;

        var budget = TimeSpan.FromMilliseconds(_captureBudgetMs);
        var start = _timing.RealTime;

        BuildMindedGrids();

        // Snapshot the depth: minded grids re-queue onto the tail, and a live bound would walk
        // straight back into them within the same tick.
        var count = _pending.Count;
        for (var i = 0; i < count; i++)
        {
            if (_timing.RealTime - start > budget)
                break;

            var record = _pending.Dequeue();
            _pendingIds.Remove(record.Id);

            // Anything that died or retired while queued resolved its own bookkeeping
            // (OnDebrisTerminating, cell retire); the queue just lets go.
            if (record.State != RecordState.Materialized || record.Entity is not { } ent || !Exists(ent))
                continue;

            if (!TryComp<RecordedDebrisComponent>(ent, out var recorded))
                continue;

            if (!recorded.Touched)
            {
                // Captured by an earlier pass while this entry sat queued; nothing owed.
                ReleaseHold(ent, recorded);
                continue;
            }

            if (!TryComp<MapGridComponent>(ent, out var grid))
            {
                ReleaseHold(ent, recorded);
                continue;
            }

            // A mind aboard would be serialized into the blob like any other child, and a
            // player who exists both in a blob and in the world is a worse dupe than the one
            // this system closes. Dropped from the queue rather than re-queued: Touched stays
            // set, so the GC hold keeps vouching at each collection attempt and re-files the
            // grid here, which retries on the collector's cadence instead of spamming a
            // deferral per tick. A rock kept alive by a body aboard is the intended outcome.
            if (_mindedGrids.Contains(ent))
            {
                Log.Warning($"Deferring capture of debris {ToPrettyString(ent)}: a minded entity is aboard.");
                continue;
            }

            if (Capture(record, ent, grid, recorded))
            {
                recorded.CaptureAttempts = 0;
                ReleaseHold(ent, recorded);
                continue;
            }

            recorded.CaptureAttempts++;
            if (recorded.CaptureAttempts >= MaxCaptureAttempts)
            {
                Log.Error($"Giving up on capturing debris {ToPrettyString(ent)} (record {record.Id}) "
                    + $"after {MaxCaptureAttempts} attempts; its changes regenerate on next materialize.");
                RecordMetrics.CaptureGiveUps.Inc();
                ReleaseHold(ent, recorded);
                continue;
            }

            // Transient failure: retry on a later tick. The GC hold covers the gap.
            TryEnqueue(record);
        }
    }

    /// <summary>Pays back the GC debt taken on by <see cref="OnTryCancelGC"/>.</summary>
    private void ReleaseHold(EntityUid ent, RecordedDebrisComponent recorded)
    {
        if (!recorded.GcHeld)
            return;

        recorded.GcHeld = false;
        _gc.TryGCEntity(ent);
    }

    /// <returns>Whether the blob was stored; false keeps any earlier blob and Touched set.</returns>
    private bool Capture(DebrisRecord record, EntityUid ent, MapGridComponent grid, RecordedDebrisComponent recorded)
    {
        string yaml;
        try
        {
            // Same options profile as the shipyard save path. Ignore scopes the blob to the
            // grid: references out of it (a turret's last target, a device link off-grid)
            // serialize as invalid and drop on load, which is correct for a rock.
            var opts = SerializationOptions.Default with
            {
                MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                ErrorOnOrphan = false,
                LogAutoInclude = null,
            };

            var (node, _) = _mapLoader.SerializeEntitiesRecursive([ent], opts);

            var document = new YamlDocument(node.ToYaml());
            using var writer = new StringWriter();
            var stream = new YamlStream { document };
            stream.Save(new YamlMappingFix(new Emitter(writer)), false);
            yaml = writer.ToString();
        }
        catch (Exception e)
        {
            // Keep whatever blob an earlier cycle produced and leave Touched set. The caller
            // retries up to the attempt cap while the GC hold keeps the grid alive, so "retry
            // later" is a guarantee here, not a hope.
            Log.Error($"Failed to capture debris {ToPrettyString(ent)} (record {record.Id}): {e}");
            return false;
        }

        var raw = Encoding.UTF8.GetBytes(yaml);
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        var data = compressed.ToArray();

        if (_blobs.Remove(record.Id, out var old))
            _totalBytes -= old.Data.Length;

        EvictUntilFits(data.Length);

        var stamp = _stamp++;
        _blobs[record.Id] = new CaptureEntry(data, stamp);
        _evictOrder.Enqueue(record.Id, stamp);
        _totalBytes += data.Length;

        // The dormant projection: contact outline, data-space collision tiles and coarse bound
        // all re-derive from the grid's real tiles, rasterized into the dormant frame
        // (axis-aligned tile units around record.Point). For the usual unrotated, undrifted
        // rock this is exact; a drifted or spun one rasterizes to an honest approximation, and
        // the blob itself restores the true pose regardless.
        var tiles = RasterizeTiles(ent, grid, record.Point);
        var outline = TraceOutline(tiles);
        _describe.ApplyCapture(record, tiles, outline);

        recorded.Touched = false;

        RecordMetrics.Captures.Inc();
        RecordMetrics.CaptureBlobs.Set(_blobs.Count);
        RecordMetrics.CaptureBytes.Set(_totalBytes);
        return true;
    }

    private void EvictUntilFits(int incoming)
    {
        while (_blobs.Count > 0 && _totalBytes + incoming > _budgetBytes
               && _evictOrder.TryDequeue(out var oldestId, out var stamp))
        {
            // Stale order entries (dropped or re-captured blobs) fall through here; the live
            // blob's own entry is further down the queue.
            if (!_blobs.TryGetValue(oldestId, out var entry) || entry.Stamp != stamp)
                continue;

            Drop(oldestId);
            RecordMetrics.CaptureEvictions.Inc();

            // The record must fall back with its projections in step, or it paints the captured
            // shape while materializing the pristine one.
            if (!_describe.Records.TryGetValue(oldestId, out var evicted))
                continue;

            _describe.RevertCapture(evicted);

            // A restore leaves its blob resident (it is the capture for an untouched-since
            // unload), so the oldest blob can belong to a rock currently alive in the world.
            // The live grid still holds the true state: re-arm Touched and the next unload
            // recaptures fresh, instead of the untouched fast path silently keeping the
            // pristine revert and regenerating everything at the visit after.
            if (evicted.State == RecordState.Materialized && evicted.Entity is { } live
                && TryComp<RecordedDebrisComponent>(live, out var liveRecorded))
            {
                liveRecorded.Touched = true;
                Log.Warning($"Capture store over budget: evicted blob for live record {oldestId}; "
                    + "it recaptures at next unload. Raise triad.worldgen.capture_budget_mb.");
                continue;
            }

            Log.Warning($"Capture store over budget: evicted blob for record {oldestId}; "
                + "that rock reverts to its pristine roll. Raise triad.worldgen.capture_budget_mb.");
        }
    }

    /// <summary>
    ///     One pass over every minded entity per drain, not one per rock: the drain may capture
    ///     many grids in a burst, and the answer cannot change mid-drain.
    /// </summary>
    private void BuildMindedGrids()
    {
        _mindedGrids.Clear();

        var query = EntityQueryEnumerator<MindContainerComponent, TransformComponent>();
        while (query.MoveNext(out _, out var mind, out var xform))
        {
            if (mind.HasMind && xform.GridUid is { } grid)
                _mindedGrids.Add(grid);
        }
    }

    private HashSet<Vector2i> RasterizeTiles(EntityUid ent, MapGridComponent grid, Vector2 point)
    {
        var tiles = new HashSet<Vector2i>();
        var matrix = _xformSys.GetWorldMatrix(ent);

        foreach (var tile in _map.GetAllTiles(ent, grid))
        {
            var localCenter = (new Vector2(tile.GridIndices.X, tile.GridIndices.Y)
                               + new Vector2(0.5f, 0.5f)) * grid.TileSize;
            var world = Vector2.Transform(localCenter, matrix);
            tiles.Add((world - point).Floored());
        }

        return tiles;
    }

    private static Vector2i[]? TraceOutline(HashSet<Vector2i> tiles)
    {
        if (tiles.Count == 0)
            return null;

        var blobTiles = new List<BlobTile>(tiles.Count);
        foreach (var pos in tiles)
        {
            blobTiles.Add(new BlobTile(pos, 0));
        }

        return TileOutline.Trace(blobTiles);
    }

    // ---- Restore at materialize ------------------------------------------------------------

    /// <summary>
    ///     Loads a captured blob back onto the map. The yaml carries the grid's captured pose,
    ///     so no offset is applied: the rock returns exactly where it was when the cell let go
    ///     of it. Entities restore already map-initialized (the serializer stamps them, the
    ///     deserializer honours the stamp), which is the whole anti-dupe property: no fill, no
    ///     spawner and no loot table runs a second time.
    /// </summary>
    public bool TryRestore(DebrisRecord record, MapId mapId, out EntityUid restored)
    {
        restored = EntityUid.Invalid;

        if (!_blobs.TryGetValue(record.Id, out var entry))
            return false;

        try
        {
            using var input = new MemoryStream(entry.Data, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var textReader = new StreamReader(deflate, Encoding.UTF8);

            // Invalid-entity logging off: Ignore at capture time means dropped out-of-grid
            // references are expected in the file, not a surprise worth a log line each.
            var opts = DeserializationOptions.Default with { LogInvalidEntities = false };

            if (!_mapLoader.TryLoadGrid(mapId, textReader, $"triad-debris-{record.Id}", out var grid, opts))
            {
                RecordMetrics.CaptureRestoreFailures.Inc();
                return false;
            }

            restored = grid.Value.Owner;
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Failed to restore debris blob for record {record.Id}: {e}");
            RecordMetrics.CaptureRestoreFailures.Inc();
            return false;
        }
    }
}
