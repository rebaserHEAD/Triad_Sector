// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Triad.Worldgen;
using Content.Shared.GameTicking;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._Triad.Worldgen;

/// <summary>
///     A dormant contact ready to draw: position, derived outline, derived color. The wire never
///     carries geometry on the common path; this is what the recipe resolves into locally.
/// </summary>
public readonly record struct SensedContactView(Vector2 MapPosition, Vector2i[] Outline, Color Color);

public sealed class SensedContactsSystem : EntitySystem
{
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    // These two are coupled: the server answers every poll, so StaleAfter divided by
    // RequestThrottle is the number of consecutive dropped replies a console tolerates before it
    // blanks. Ten is a sane UDP margin. Do not raise the throttle without raising StaleAfter too.
    private static readonly TimeSpan RequestThrottle = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Cap on shape rolls per frame. A full-picture sync arrives as batches of up to 256
    ///     contacts, and rolling every shape on receipt would lump tens of milliseconds into one
    ///     frame; deriving on demand under this cap instead lets a cold picture fill over a few
    ///     frames, which is invisible at radar timescales. Rocks whose shape is not rolled yet
    ///     are simply skipped by <see cref="GetContacts"/> until a frame has budget for them.
    /// </summary>
    private const int MaxDerivesPerFrame = 16;

    private int _derivesThisFrame;

    // Keyed per console, matching the caches below. A single system-wide scalar let whichever
    // radar control ticked first in UI tree order consume the gate every cycle, so any second
    // open console never sent a request and rendered nothing.
    private readonly Dictionary<NetEntity, TimeSpan> _lastRequestTime = new();

    /// <summary>
    ///     The ids the server currently vouches for, per console. Ids only: the contact data
    ///     itself lives once in <see cref="_chart"/>, and the live view is a filter over it, so
    ///     the two can never disagree about what a rock looks like. A live id whose map entry is
    ///     gone (map change mid-poll, map deletion) simply misses the lookup and draws nothing.
    /// </summary>
    private readonly Dictionary<NetEntity, HashSet<int>> _live = new();

    private readonly Dictionary<NetEntity, TimeSpan> _lastUpdated = new();

    /// <summary>
    ///     The single contact store, and the chart: everything any console was ever sent, per
    ///     map, surviving range egress, console close, and link hiccups. Presentation memory,
    ///     never authority: it only ever holds what the server legitimately sent, existence-scope
    ///     removes evict from it, and a round restart or map deletion clears it wholesale. Keyed
    ///     by map because contact positions are map coordinates and records never change maps.
    /// </summary>
    private readonly Dictionary<MapId, Dictionary<int, ClientContact>> _chart = new();

    /// <summary>
    ///     Derived outlines keyed by the roll identity. Version is part of the key so a
    ///     re-versioned rock (blob-restored, later) re-derives instead of reusing the pristine
    ///     shape. Bounded by distinct rocks seen this round; dropped wholesale on round restart
    ///     once the chart layer lands, and a round's worth is at most a few MB.
    /// </summary>
    private readonly Dictionary<(string Proto, int Seed, int Version), Vector2i[]> _shapes = new();

    private readonly Dictionary<string, Color> _colors = new();

    /// <summary>
    ///     A contact as stored: legend index and color both resolved at receipt, geometry not yet
    ///     derived. Color is resolved eagerly because prototype component lookups touch
    ///     thread-local IoC (a debug assert fires even on the factory overload), and receipt is
    ///     the last point guaranteed to be on the game thread: <see cref="GetContacts"/> is a
    ///     public read API with callers off it (tests), so everything it touches must be plain
    ///     data or pure math.
    /// </summary>
    private readonly record struct ClientContact(
        int Version,
        SensedContactArm Arm,
        Vector2 MapPosition,
        SensedProtoRecipe Recipe,
        int Seed,
        Vector2i[]? Outline,
        Color Color);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<SensedContactsDeltaEvent>(OnDelta);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<MapComponent, EntityTerminatingEvent>(OnMapTerminating);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        // A new round is a new belt roll; everything below is round-scoped.
        _chart.Clear();
        _live.Clear();
        _lastUpdated.Clear();
        _lastRequestTime.Clear();
        _shapes.Clear();
        _colors.Clear();
    }

    // Maps never surface MapRemovedEvent client-side; component-filtered termination is the
    // house pattern for dropping per-map state.
    private void OnMapTerminating(EntityUid uid, MapComponent component, ref EntityTerminatingEvent args)
    {
        _chart.Remove(component.MapId);
    }

    // Tick, not FrameUpdate: a headless client (integration tests, replays) never renders a
    // frame, and a budget that only refills on render would wedge shut after the first sixteen
    // derives there. Ticks always run, and per-tick refill only makes the budget slightly more
    // generous per second on a high-refresh client.
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _derivesThisFrame = 0;
    }

    private void OnDelta(SensedContactsDeltaEvent ev)
    {
        if (!_live.TryGetValue(ev.Console, out var live))
        {
            live = new HashSet<int>();
            _live[ev.Console] = live;
        }

        // A full reset resets what the server vouches for, not what we remember: the chart
        // surviving it is the point of the chart.
        if (ev.FullReset)
            live.Clear();

        // The map is stamped by the server, so receipt never resolves the console entity: a
        // console can legitimately leave the client's view between request and reply, and
        // dropping the adds in that window would hole this console's picture for good (the
        // server's known-set already counts them as delivered).
        Dictionary<int, ClientContact>? mapChart = null;
        if (ev.Map != MapId.Nullspace && !_chart.TryGetValue(ev.Map, out mapChart))
        {
            mapChart = new Dictionary<int, ClientContact>();
            _chart[ev.Map] = mapChart;
        }

        foreach (var id in ev.Removes)
        {
            // Existence-scope: the rock is gone, so the memory of it goes too.
            live.Remove(id);
            mapChart?.Remove(id);
        }

        foreach (var id in ev.Fades)
        {
            // View-scope: the rock exists but left this console's picture. Chart keeps it.
            live.Remove(id);
        }

        // A Nullspace-stamped delta carries no adds by construction: the server's visibility
        // collection returns empty for an unmapped console, and the stamp comes from the same
        // transform in the same method. This guard therefore only ever drops adds from a
        // malformed event, which have no map to draw on anyway.
        if (mapChart is not null)
        {
            foreach (var contact in ev.Adds)
            {
                // Legend indices are only meaningful within the event that carried them, so they
                // are resolved here at receipt, never stored. Out-of-range means a malformed
                // event; dropping the contact beats throwing in a network handler.
                if (contact.ProtoIndex < 0 || contact.ProtoIndex >= ev.Legend.Count)
                    continue;

                var recipe = ev.Legend[contact.ProtoIndex];
                mapChart[contact.Id] = new ClientContact(contact.Version, contact.Arm,
                    contact.MapPosition, recipe, contact.Seed, contact.Outline, GetColor(recipe.ProtoId));
                live.Add(contact.Id);
            }
        }

        _lastUpdated[ev.Console] = _timing.CurTime;
    }

    /// <summary>
    /// Requests a refresh of sensed contacts for the given console, throttled to one network
    /// send per <see cref="RequestThrottle"/> per console. Each console gates independently, so
    /// several radar screens can be open at once without starving one another.
    /// </summary>
    public void RequestContacts(EntityUid console)
    {
        if (!Exists(console))
            return;

        var netConsole = GetNetEntity(console);

        if (_lastRequestTime.TryGetValue(netConsole, out var lastRequest)
            && _timing.CurTime - lastRequest < RequestThrottle)
        {
            return;
        }

        _lastRequestTime[netConsole] = _timing.CurTime;

        RaiseNetworkEvent(new RequestSensedContactsEvent(netConsole));
    }

    /// <summary>
    /// Streams the drawable contacts for the given console, deriving any outlines not yet rolled
    /// under the per-frame budget. Yields nothing if the console has no known contacts or its
    /// data hasn't been refreshed in a while (contacts are static, so a generous staleness window
    /// is used to avoid flicker). The map is a parameter for the same reasons as
    /// <see cref="GetChart"/>, and it doubles as the map-change guard: live ids from before an
    /// FTL jump belong to the old map's store, miss this one's, and draw nothing, rather than
    /// painting old-map rocks through the new view until the next poll's fades land.
    /// </summary>
    public IEnumerable<SensedContactView> GetContacts(EntityUid console, MapId map)
    {
        var netConsole = GetNetEntity(console);

        if (!_lastUpdated.TryGetValue(netConsole, out var lastUpdate)
            || _timing.CurTime - lastUpdate > StaleAfter
            || !_live.TryGetValue(netConsole, out var live)
            || !_chart.TryGetValue(map, out var mapChart))
        {
            yield break;
        }

        foreach (var id in live)
        {
            if (mapChart.TryGetValue(id, out var contact) && TryGetShape(contact, out var outline))
                yield return new SensedContactView(contact.MapPosition, outline, contact.Color);
        }
    }

    /// <summary>
    /// Streams the charted contacts for a console's current map that are NOT in its live view:
    /// last-known rocks the server is not currently vouching for, for the dimmed underlay. The
    /// map is a parameter rather than resolved here so this stays plain data and pure math like
    /// <see cref="GetContacts"/>; the radar control already has its view's map at draw time, and
    /// passing it is also what keeps a console that changed maps from projecting another map's
    /// chart through its own view transform. No staleness gate: a chart is exactly the thing
    /// that should keep painting when the link drops.
    /// </summary>
    public IEnumerable<SensedContactView> GetChart(EntityUid console, MapId map)
    {
        if (!_chart.TryGetValue(map, out var mapChart))
            yield break;

        var netConsole = GetNetEntity(console);
        _live.TryGetValue(netConsole, out var live);

        foreach (var (id, contact) in mapChart)
        {
            if (live is not null && live.Contains(id))
                continue;

            if (TryGetShape(contact, out var outline))
                yield return new SensedContactView(contact.MapPosition, outline, contact.Color);
        }
    }

    /// <summary>
    ///     Resolves a contact's outline, rolling and caching it if the frame budget allows.
    ///     False only for a not-yet-rolled shape past this frame's budget, an arm this client
    ///     does not understand, a roll that produced nothing (which the server never sends), or
    ///     a trace past the vertex valve, which no shipping prototype reaches and which logs.
    /// </summary>
    private bool TryGetShape(in ClientContact contact, out Vector2i[] outline)
    {
        outline = Array.Empty<Vector2i>();

        // The Explicit arm carries its geometry verbatim; nothing to roll.
        if (contact.Arm == SensedContactArm.Explicit)
        {
            if (contact.Outline is not { Length: > 2 })
                return false;

            outline = contact.Outline;
            return true;
        }

        // Unknown arms (anything newer than this client) are skipped rather than misdrawn.
        if (contact.Arm != SensedContactArm.Pristine)
            return false;

        var key = (contact.Recipe.ProtoId, contact.Seed, contact.Version);
        if (_shapes.TryGetValue(key, out var cached))
        {
            outline = cached;
            return cached.Length > 2;
        }

        if (_derivesThisFrame >= MaxDerivesPerFrame)
            return false;

        _derivesThisFrame++;

        // The same walk and trace the server rolled at describe time and the builder will roll at
        // materialize time; shared code and a shared seed are what make the painted silhouette
        // the rock that eventually loads in.
        var tiles = BlobShapeGen.Roll(new System.Random(contact.Seed), contact.Recipe);
        var traced = tiles.Count == 0 ? null : TileOutline.Trace(tiles);

        // Empty is cached too, so a valve-tripping shape logs once instead of re-rolling and
        // re-logging every frame the contact is on screen.
        if (traced is null && tiles.Count > 0)
        {
            Log.Warning($"Outline for {contact.Recipe.ProtoId} seed {contact.Seed} exceeded the "
                + $"trace valve ({TileOutline.MaxOutlineVerts} verts); contact will not draw.");
        }

        cached = traced ?? Array.Empty<Vector2i>();
        _shapes[key] = cached;
        outline = cached;
        return cached.Length > 2;
    }

    private Color GetColor(string protoId)
    {
        if (_colors.TryGetValue(protoId, out var color))
            return color;

        color = Color.Gray;
        // Game thread only (called from OnDelta): every TryGetComponent overload funnels into the
        // by-name one, whose debug assert resolves the component factory through thread-local IoC.
        if (_proto.TryIndex<EntityPrototype>(protoId, out var proto)
            && proto.TryGetComponent<IFFComponent>(out var iff, _factory))
        {
            color = iff.Color;
        }

        _colors[protoId] = color;
        return color;
    }
}
