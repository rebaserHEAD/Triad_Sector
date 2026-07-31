// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.GameTicking;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Worldgen;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Worldgen.Cells;

/// <summary>
///     Server half of the radar contact channel for dormant worldgen debris. Diffs each
///     console's currently-visible <see cref="CellDescribeSystem.Records"/> set against what
///     that session was last sent and replies with only the delta. Materialized records are
///     excluded on purpose: their grid draws its own blip once
///     <see cref="DebrisMaterializeQueueSystem"/> spawns it, so the contact hands off to the
///     grid instead of doubling it.
/// </summary>
public sealed class SensedContactsSystem : EntitySystem
{
    [Dependency] private readonly CellDescribeSystem _describe = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly TransformSystem _xformSys = default!;

    /// <summary>Requests between opportunistic sweeps for known-sets whose console died.</summary>
    private const int SweepInterval = 64;

    private bool _enabled;
    private float _describeRange;
    private int _addsPerPoll;
    private int _sweepCounter;

    /// <summary>
    ///     Per (session, console) map of record id to the <see cref="DebrisRecord.Version"/> that
    ///     session's client currently holds. A version mismatch re-sends the contact exactly like
    ///     a missing id, which is the entire dirty mechanism: bump the record, and every viewer
    ///     picks up the change on its next poll.
    /// </summary>
    private readonly Dictionary<(ICommonSession Session, EntityUid Console), Dictionary<int, int>> _known = new();

    /// <summary>
    ///     Roll parameters per prototype id, mirrored once from the server-only
    ///     BlobFloorPlanBuilder component. Null entries cache the misses too. Never invalidated:
    ///     prototypes are static for the round.
    /// </summary>
    private readonly Dictionary<string, SensedProtoRecipe?> _recipeCache = new();

    // Scan scratch only, cleared at the top of every request. The lists that ride the delta
    // event are allocated fresh per send instead: the network layer serializes the event after
    // this method returns, so a pooled list would be cleared out from under it by the very next
    // request.
    private readonly List<DebrisRecord> _tempVisibleCache = new();
    private readonly HashSet<int> _tempVisibleIdsCache = new();
    private readonly Dictionary<string, int> _tempLegendIndexCache = new();
    private readonly List<(ICommonSession Session, EntityUid Console)> _tempEvictCache = new();

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, TriadCCVars.WorldgenSensedEnabled, OnSensedEnabledChanged, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenContactAddsPerPoll, v => _addsPerPoll = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenDescribeRange, v => _describeRange = v, true);

        SubscribeNetworkEvent<RequestSensedContactsEvent>(OnContactsRequested);
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    /// <summary>
    ///     Drops every known-set when the tier is switched off. Nothing maintains those sets while
    ///     the tier is disabled, so switching it back on hands each console a full reset instead of
    ///     diffing against a set that has been sitting still. Same shape the decal and gas-overlay
    ///     systems use to drop their per-session sent-state on the NetPVS toggle. This is hygiene,
    ///     not a visual fix: record ids never recycle and removes are uncapped, so a stale set
    ///     would converge on its own.
    /// </summary>
    private void OnSensedEnabledChanged(bool value)
    {
        if (value == _enabled)
            return;

        _enabled = value;

        if (!value)
            _known.Clear();
    }

    /// <summary>
    ///     Whether <paramref name="actor"/> currently has any interface open on the console.
    ///     Checked across every UI key rather than one named key on purpose: four separate controls
    ///     request contacts (nav, radar, the Crescent drone console and the Mono fire-control
    ///     console) and each opens its own key, so a named-key check would silently blank whichever
    ///     one it forgot, and would blank a fifth the day someone adds it.
    /// </summary>
    private bool IsViewing(EntityUid console, EntityUid actor)
    {
        if (!TryComp<UserInterfaceComponent>(console, out var ui))
            return false;

        foreach (var actors in ui.Actors.Values)
        {
            if (actors.Contains(actor))
                return true;
        }

        return false;
    }

    private void OnContactsRequested(RequestSensedContactsEvent ev, EntitySessionEventArgs args)
    {
        SweepDeadConsoles();

        if (!_enabled)
            return;

        if (!TryGetEntity(ev.Console, out var consoleUid) || !TryComp<RadarConsoleComponent>(consoleUid, out var radar))
            return;

        // Authorize against the sender, not just the target. A NetEntity is a small sequential
        // integer, so without this a modified client can walk the id space and pull the sensed
        // picture off any radar console on any map without ever approaching one. Serving only
        // consoles the sender actually has open reuses the range and access rules the UI system
        // already enforced when it let them open it, rather than re-deriving a weaker copy here.
        if (args.SenderSession.AttachedEntity is not { } actor || !IsViewing(consoleUid.Value, actor))
            return;

        var key = (args.SenderSession, consoleUid.Value);
        var isNew = !_known.TryGetValue(key, out var knownIds);
        knownIds ??= new Dictionary<int, int>();

        CollectVisible(consoleUid.Value, radar);

        _tempLegendIndexCache.Clear();

        // These ride the event, so they are allocated per send; see the scratch-field comment.
        var legend = new List<SensedProtoRecipe>();
        var adds = new List<SensedContactData>();
        var removes = new List<int>();
        var fades = new List<int>();

        foreach (var record in _tempVisibleCache)
        {
            // Same-version contacts are settled; a version mismatch re-sends exactly like a
            // missing id, and the client upserts by id either way.
            if (knownIds.TryGetValue(record.Id, out var knownVersion) && knownVersion == record.Version)
                continue;

            if (adds.Count >= _addsPerPoll)
                break; // rest arrive on a later poll

            // CollectVisible only passes shaped records, and a record is only shaped if its
            // prototype carried a blob builder, so the recipe lookup cannot miss in practice.
            // Guarded anyway: a contact the client cannot roll is worse than no contact.
            if (GetRecipe(record.Proto) is not { } recipe)
                continue;

            if (!_tempLegendIndexCache.TryGetValue(record.Proto, out var protoIndex))
            {
                protoIndex = legend.Count;
                legend.Add(recipe);
                _tempLegendIndexCache[record.Proto] = protoIndex;
            }

            adds.Add(new SensedContactData(record.Id, record.Version, SensedContactArm.Pristine,
                record.Point, protoIndex, record.Seed, null));
        }

        foreach (var id in knownIds.Keys)
        {
            if (_tempVisibleIdsCache.Contains(id))
                continue;

            // WHY the id left view decides what the client forgets. Gone from the record index
            // entirely (destroyed in play, cell retired) or burned out as a ghost rock: the rock
            // is not out there, so it leaves the chart too. Still in the index but filtered out
            // of this console's picture (range, map, materialized handoff): the rock exists, the
            // console just cannot vouch for it right now, and the chart keeps the last known.
            if (!_describe.Records.TryGetValue(id, out var gone) || gone.GaveUp)
                removes.Add(id);
            else
                fades.Add(id);
        }

        // An empty delta is still sent, on purpose: the reply is the client's keepalive. The client
        // only stamps a console's last-updated time when a delta lands, and blanks that console
        // after its own staleness window of silence. A settled picture (parked, docked,
        // station-keeping, or a nav UI reopened onto an unchanged set) produces no adds and no
        // removes forever, so staying quiet here reads as the debris having vanished.

        // Reflect exactly what is about to be sent: capped adds included, uncapped removes and
        // fades included. Fades leave the known-set like removes do; if the rock re-enters view
        // it re-sends as an add and the client upserts by id.
        foreach (var add in adds)
            knownIds[add.Id] = add.Version;
        foreach (var removed in removes)
            knownIds.Remove(removed);
        foreach (var faded in fades)
            knownIds.Remove(faded);

        _known[key] = knownIds;

        RaiseNetworkEvent(new SensedContactsDeltaEvent(ev.Console, Transform(consoleUid.Value).MapID,
            _gameTicker.RoundId, isNew, legend, adds, removes, fades), args.SenderSession);

        if (adds.Count > 0)
            SensedMetrics.ContactsSent.Inc(adds.Count);
    }

    /// <summary>
    ///     Fills the visible-record scratch lists for one console: dormant, shaped, non-ghost
    ///     records on the console's map within radar range and the tier's describe radius.
    ///     Deliberately flat gates and no sensor model: whether a rock paints is a draw
    ///     decision, and real grids keep their own detection untouched.
    /// </summary>
    private void CollectVisible(EntityUid consoleUid, RadarConsoleComponent radar)
    {
        _tempVisibleCache.Clear();
        _tempVisibleIdsCache.Clear();

        var consoleMap = Transform(consoleUid).MapUid;
        if (consoleMap is null)
            return;

        // Timed because this walk is unindexed and its input never shrinks in-round: cost is
        // consoles x live records, twice a second. If the perception side ever starts eating the
        // residency win, it shows up here first.
        var scanStart = _timing.RealTime;
        SensedMetrics.ContactScanRecords.Observe(_describe.Records.Count);

        var consolePos = _xformSys.GetWorldPosition(consoleUid);
        var maxRangeSq = radar.MaxRange * radar.MaxRange;
        var describeRangeSq = _describeRange * _describeRange;

        foreach (var record in _describe.Records.Values)
        {
            if (!record.Shaped || record.State != SensedState.Dormant || record.Map != consoleMap)
                continue;

            // Painting a ghost rock means flying to an outline with nothing there. The count is
            // zeroed by EnqueueCell on the next cell load, so the contact comes back if the way
            // clears.
            if (record.GaveUp)
                continue;

            // Dormant debris is a navigation aid, not a sensor puzzle: if the describe sweep
            // decided a rock is out there, it paints, capped by the console's MaxRange and the
            // tier's own describe radius so the picture never reaches farther from the console
            // than the picture it generates.
            //
            // This does not reopen a handoff seam: materialization happens inside the chunk-load
            // radius, which is far closer than either cap, so a record always becomes a grid
            // well within the range that grid draws its own blip at.
            var distSq = (consolePos - record.Point).LengthSquared();
            if (distSq > maxRangeSq || distSq > describeRangeSq)
                continue;

            _tempVisibleCache.Add(record);
            _tempVisibleIdsCache.Add(record.Id);
        }

        SensedMetrics.ContactScan.Observe((_timing.RealTime - scanStart).TotalSeconds);
    }

    /// <summary>
    ///     Roll parameters for a prototype, mirrored from its server-only BlobFloorPlanBuilder
    ///     component so the client can re-run the identical walk. Null for prototypes without a
    ///     blob builder, whose records are never shaped and never reach the adds loop.
    /// </summary>
    private SensedProtoRecipe? GetRecipe(string protoId)
    {
        if (_recipeCache.TryGetValue(protoId, out var cached))
            return cached;

        SensedProtoRecipe? recipe = null;
        if (_proto.TryIndex<EntityPrototype>(protoId, out var proto))
            recipe = DebrisRecipe.TryFrom(proto);

        _recipeCache[protoId] = recipe;
        return recipe;
    }

    /// <summary>
    ///     A console can be deleted (grid scrapped, ship deleted) between requests with no event
    ///     this system hears; without this its known-set would hold that pair forever.
    /// </summary>
    private void SweepDeadConsoles()
    {
        if (++_sweepCounter < SweepInterval)
            return;
        _sweepCounter = 0;

        _tempEvictCache.Clear();
        foreach (var key in _known.Keys)
        {
            if (!Exists(key.Console))
                _tempEvictCache.Add(key);
        }

        foreach (var key in _tempEvictCache)
            _known.Remove(key);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.NewStatus != SessionStatus.Disconnected)
            return;

        _tempEvictCache.Clear();
        foreach (var key in _known.Keys)
        {
            if (key.Session == e.Session)
                _tempEvictCache.Add(key);
        }

        foreach (var key in _tempEvictCache)
            _known.Remove(key);
    }
}
