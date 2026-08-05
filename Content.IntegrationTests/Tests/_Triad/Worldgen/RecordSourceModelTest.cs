// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Content.Server._Triad.Worldgen.Cells;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Prototypes;
using Content.Shared._Triad.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._Triad.Worldgen;

/// <summary>
///     Pins the source model: presence decides what debris exists, sensors decide only what a
///     screen is told about. A radar must never bring debris into being, or an observing ghost,
///     a mech, or a dragon drifting past decides the contents of the belt. Everything present
///     anchors the world at the same reach, whether it is a ship, a person, or a station.
/// </summary>
[TestFixture]
public sealed class RecordSourceModelTest
{
    private const string WorldgenConfig = "NFDefault";
    private const string RadarOnly = "TriadRecordsTestRadarOnly";
    private const string Anchor = "TriadRecordsTestAnchor";
    private const string ShortAnchor = "TriadRecordsTestAnchorShort";

    private const float DescribeRange = 1024f;

    // String rather than float so the prototype below and the envelope math stay one value; only
    // string constants are foldable into a constant interpolated string.
    private const string ShortRange = "256";

    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {RadarOnly}
  components:
  - type: RadarConsole

- type: entity
  id: {Anchor}
  components:
  - type: DescribeAnchor

- type: entity
  id: {ShortAnchor}
  components:
  - type: DescribeAnchor
    range: {ShortRange}
";

    /// <summary>
    ///     Worldgen map with the live belt config and a single entity of the given prototype at
    ///     origin, ticked long enough for several 1 Hz describe sweeps to have run.
    /// </summary>
    private static async Task<EntityUid> SetupWithSource(
        Content.IntegrationTests.Pair.TestPair pair, string sourceProto)
    {
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var serManager = server.ResolveDependency<ISerializationManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var mapSystem = entManager.System<SharedMapSystem>();

        EntityUid mapUid = default;

        await server.WaitPost(() =>
        {
            cfg.SetCVar(TriadCCVars.WorldgenRecordsEnabled, true);
            cfg.SetCVar(TriadCCVars.WorldgenDescribeRange, DescribeRange);
            cfg.SetCVar(TriadCCVars.WorldgenDescribeBudgetMs, 500f);
            cfg.SetCVar(TriadCCVars.WorldgenMaterializeBudgetMs, 500f);

            mapUid = mapSystem.CreateMap(out var mapId);
            protoManager.Index<WorldgenConfigPrototype>(WorldgenConfig).Apply(mapUid, serManager, entManager);

            entManager.SpawnEntity(sourceProto, new MapCoordinates(Vector2.Zero, mapId));
        });

        // Describe runs at 1 Hz; four seconds covers several sweeps whether or not the pooled
        // server is under load, which matters most for the assertion that nothing happened.
        await server.WaitRunTicks(240);
        await server.WaitIdleAsync();

        return mapUid;
    }

    /// <summary>
    ///     Chunk coordinates of every cell the describe sweep has decided on this map. Asserting on
    ///     cells rather than on debris records is deliberate: a cell is described whenever the sweep
    ///     reaches it, while whether that cell then rolls any debris is a Poisson draw, so a
    ///     record-based assertion would be flaky at short range and could pass spuriously when the
    ///     sweep ran but happened to roll nothing.
    /// </summary>
    private static List<Vector2i> DescribedCells(IEntityManager entManager, EntityUid map)
    {
        var result = new List<Vector2i>();
        var cells = entManager.EntityQueryEnumerator<CellRecordsComponent, WorldChunkComponent>();
        while (cells.MoveNext(out _, out _, out var chunk))
        {
            if (chunk.Map == map)
                result.Add(chunk.Coordinates);
        }

        return result;
    }

    private static async Task Cleanup(Content.IntegrationTests.Pair.TestPair pair, EntityUid map)
    {
        // These fixtures build their own worldgen maps rather than using pair.TestMap, so the
        // pooled server will not reclaim them and a live describe source would keep rolling
        // debris on every later test that borrows this pair.
        var entManager = pair.Server.ResolveDependency<IEntityManager>();
        await pair.Server.WaitPost(() => entManager.DeleteEntity(map));
        await pair.Server.WaitIdleAsync();
    }

    /// <summary>
    ///     The regression guard for the deleted console-source loop. A bare radar is exactly the
    ///     shape of MobObserverBase, BaseMech, and the hardsuit helmets, all of which carry
    ///     RadarConsole; if any of them can describe, an observer is deciding what exists.
    /// </summary>
    [Test]
    public async Task RadarAloneDescribesNothing()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();

        var map = await SetupWithSource(pair, RadarOnly);

        Assert.That(DescribedCells(entManager, map), Is.Empty,
            "A radar console is a sensor, not a presence: it must not cause any cell to be described.");

        await Cleanup(pair, map);
        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     The other half of the same invariant: something present with no sensor and no chunk
    ///     loader still makes the space around it real, which is what lets a station or POI hold
    ///     its surroundings without crewing a shuttle console.
    /// </summary>
    [Test]
    public async Task AnchorDescribesWithoutSensorOrLoader()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();

        var map = await SetupWithSource(pair, Anchor);

        Assert.That(DescribedCells(entManager, map), Is.Not.Empty,
            "A describe anchor carries no RadarConsole and no WorldLoader, so this fails if describe " +
            "is still coupled to sensors or to chunk loading.");

        await Cleanup(pair, map);
        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     An anchor's own range overrides the server-wide describe range, so a large POI can hold
    ///     more space real than a shuttle without moving the global knob.
    /// </summary>
    [Test]
    public async Task AnchorRangeBoundsItsFootprint()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();
        var entManager = server.ResolveDependency<IEntityManager>();

        var map = await SetupWithSource(pair, ShortAnchor);
        var cells = DescribedCells(entManager, map);

        Assert.That(cells, Is.Not.Empty, "Short-range anchor should still describe its own neighbourhood.");

        // The sweep enumerates a disc in chunk space of ceil(range / chunkSize) + 1, so that is the
        // whole reachable envelope. A short anchor that described past it would mean the per-anchor
        // range is being ignored in favour of the server-wide describe range.
        var range = float.Parse(ShortRange, CultureInfo.InvariantCulture);
        var chunkRadius = (int) MathF.Ceiling(range / Content.Server.Worldgen.WorldGen.ChunkSize) + 1;

        Assert.That(cells.Max(c => MathF.Sqrt(c.X * c.X + c.Y * c.Y)), Is.LessThanOrEqualTo(chunkRadius + 0.001f),
            $"A {ShortRange}m anchor described cells outside its own envelope of {chunkRadius} chunks, " +
            "so the range field is not bounding the sweep.");

        // And it must genuinely be shorter than the global range, or the bound above proves nothing.
        var globalChunkRadius = (int) MathF.Ceiling(DescribeRange / Content.Server.Worldgen.WorldGen.ChunkSize) + 1;
        Assert.That(chunkRadius, Is.LessThan(globalChunkRadius));

        await Cleanup(pair, map);
        await pair.CleanReturnAsync();
    }
}
