using System.Numerics;
using Content.Shared._Mono.CCVar;
using Content.Shared._Mono.Detection;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Worldgen;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;

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
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly TransformSystem _xformSys = default!;

    /// <summary>Requests between opportunistic sweeps for known-sets whose console died.</summary>
    private const int SweepInterval = 64;

    private bool _enabled;
    private float _visualMul;
    private int _addsPerPoll;
    private int _sweepCounter;

    /// <summary>Per (session, console) set of record ids that session's client currently holds.</summary>
    private readonly Dictionary<(ICommonSession Session, EntityUid Console), HashSet<int>> _known = new();

    // Scratch, cleared at the top of every request rather than after the send: the delta event
    // needs its own List copies anyway (see the comment in OnContactsRequested), so these only
    // ever hold one request's intermediate state.
    private readonly List<DebrisRecord> _tempVisibleCache = new();
    private readonly HashSet<int> _tempVisibleIdsCache = new();
    private readonly List<SensedContactData> _tempAddsCache = new();
    private readonly List<int> _tempRemovesCache = new();
    private readonly List<(ICommonSession Session, EntityUid Console)> _tempEvictCache = new();

    public override void Initialize()
    {
        base.Initialize();

        Subs.CVar(_cfg, TriadCCVars.WorldgenSensedEnabled, v => _enabled = v, true);
        Subs.CVar(_cfg, TriadCCVars.WorldgenContactAddsPerPoll, v => _addsPerPoll = v, true);
        Subs.CVar(_cfg, MonoCVars.VisualDetectionMultiplier, v => _visualMul = v, true);

        SubscribeNetworkEvent<RequestSensedContactsEvent>(OnContactsRequested);
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    private void OnContactsRequested(RequestSensedContactsEvent ev, EntitySessionEventArgs args)
    {
        SweepDeadConsoles();

        if (!_enabled)
            return;

        if (!TryGetEntity(ev.Console, out var consoleUid) || !TryComp<RadarConsoleComponent>(consoleUid, out var radar))
            return;

        var key = (args.SenderSession, consoleUid.Value);
        var isNew = !_known.TryGetValue(key, out var knownIds);
        knownIds ??= new HashSet<int>();

        CollectVisible(consoleUid.Value, radar);

        _tempAddsCache.Clear();
        _tempRemovesCache.Clear();

        foreach (var record in _tempVisibleCache)
        {
            if (knownIds.Contains(record.Id))
                continue;

            if (_tempAddsCache.Count >= _addsPerPoll)
                break; // rest arrive on a later poll

            _tempAddsCache.Add(new SensedContactData(record.Id, record.Point, record.Hull!, record.IffColor));
        }

        foreach (var id in knownIds)
        {
            if (!_tempVisibleIdsCache.Contains(id))
                _tempRemovesCache.Add(id);
        }

        if (_tempAddsCache.Count == 0 && _tempRemovesCache.Count == 0 && !isNew)
            return;

        // Reflect exactly what is about to be sent: capped adds included, uncapped removes included.
        foreach (var add in _tempAddsCache)
            knownIds.Add(add.Id);
        foreach (var removed in _tempRemovesCache)
            knownIds.Remove(removed);

        _known[key] = knownIds;

        // Fresh copies, not the pooled caches: those get cleared on this system's very next
        // request, which can race the network layer's own serialization of this event.
        var adds = new List<SensedContactData>(_tempAddsCache);
        var removes = new List<int>(_tempRemovesCache);

        RaiseNetworkEvent(new SensedContactsDeltaEvent(ev.Console, isNew, adds, removes), args.SenderSession);

        if (_tempAddsCache.Count > 0)
            SensedMetrics.ContactsSent.Inc(_tempAddsCache.Count);
    }

    /// <summary>
    ///     Fills the visible-record scratch lists for one console: dormant, shaped records on the
    ///     console's map within both hard radar range and the same visual-channel radius
    ///     <see cref="DetectionSystem.IsGridDetected"/> would apply to the eventual grid. That
    ///     math is duplicated here rather than shared because there is no grid yet to hand it.
    /// </summary>
    private void CollectVisible(EntityUid consoleUid, RadarConsoleComponent radar)
    {
        _tempVisibleCache.Clear();
        _tempVisibleIdsCache.Clear();

        var consoleMap = Transform(consoleUid).MapUid;
        if (consoleMap is null)
            return;

        var consolePos = _xformSys.GetWorldPosition(consoleUid);
        var maxRangeSq = radar.MaxRange * radar.MaxRange;

        // Not EnsureComp: this runs off a network request and most consoles never carry the
        // component, in which case the multiplier is the prototype default of 1f.
        TryComp<DetectionRangeMultiplierComponent>(consoleUid, out var consoleMul);
        var visualMultiplier = consoleMul?.VisualMultiplier ?? 1f;
        var alwaysDetect = consoleMul?.AlwaysDetect ?? false;

        foreach (var record in _describe.Records.Values)
        {
            if (record.Hull is null || record.State != SensedState.Dormant || record.Map != consoleMap)
                continue;

            var distSq = (consolePos - record.Point).LengthSquared();
            if (distSq > maxRangeSq)
                continue;

            if (!alwaysDetect)
            {
                var detectRadius = record.DetectSignature * visualMultiplier * _visualMul + record.DetectBias;
                if (distSq > detectRadius * detectRadius)
                    continue;
            }

            _tempVisibleCache.Add(record);
            _tempVisibleIdsCache.Add(record.Id);
        }
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
