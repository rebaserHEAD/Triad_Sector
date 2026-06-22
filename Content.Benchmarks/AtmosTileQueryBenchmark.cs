// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Content.IntegrationTests;
using Content.IntegrationTests.Pair;
using Robust.UnitTesting.Pool;
using Robust.Shared;
using Robust.Shared.Analyzers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Benchmarks;

/// <summary>
/// Measures the per-tile entity-lookup cost that atmospherics pays every atmos tick
/// (fire hotspots and high-pressure deltas each query their tile's entities).
/// Compares the default <see cref="LookupFlags.All"/> (walks all four trees and recurses
/// into containers) against the narrowed <c>Dynamic | Sundries</c> flags that the
/// HighPressureDelta path actually consumes. The delta between the two benchmarks is the
/// per-query uplift delivered by the Triad flag-narrowing fix.
/// </summary>
[Virtual]
public class AtmosTileQueryBenchmark
{
    public const string Map = "Maps/saltern.yml";

    private TestPair _pair = default!;
    private IEntityManager _entMan = default!;
    private EntityLookupSystem _lookup = default!;
    private EntityUid _grid;
    private Vector2i[] _tiles = default!;
    private readonly HashSet<EntityUid> _set = new();

    [GlobalSetup]
    public void Setup()
    {
        ProgramShared.PathOffset = "../../../../";
        PoolManager.Startup();

        // Triad: BenchmarkDotNet runs outside NUnit, so pass an ExternalTestContext instead of
        // letting PoolManager default to the NUnit context (which throws on TestContext.WorkDirectory).
        _pair = PoolManager.GetServerClient(testContext: new ExternalTestContext(nameof(AtmosTileQueryBenchmark), TextWriter.Null))
            .GetAwaiter().GetResult();
        _entMan = _pair.Server.ResolveDependency<IEntityManager>();
        _lookup = _entMan.System<EntityLookupSystem>();

        _pair.Server.ResolveDependency<IRobustRandom>().SetSeed(42);
        _pair.Server.WaitPost(() =>
        {
            var path = new ResPath(Map);
            var opts = DeserializationOptions.Default with { InitializeMaps = true };
            if (!_entMan.System<MapLoaderSystem>().TryLoadMap(path, out _, out _, opts))
                throw new Exception("Map load failed");
        }).GetAwaiter().GetResult();

        // Pick the grid with the most tiles (the station) and collect its non-empty tile coords.
        var mapSys = _entMan.System<SharedMapSystem>();
        var bestCount = -1;
        var gridEnum = _entMan.AllEntityQueryEnumerator<MapGridComponent>();
        while (gridEnum.MoveNext(out var uid, out var grid))
        {
            var local = new List<Vector2i>();
            foreach (var tileRef in mapSys.GetAllTiles(uid, grid))
                local.Add(tileRef.GridIndices);

            if (local.Count > bestCount)
            {
                bestCount = local.Count;
                _tiles = local.ToArray();
                _grid = uid;
            }
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _pair.DisposeAsync();
        PoolManager.Shutdown();
    }

    /// <summary>
    /// Current behaviour: atmos tile queries default to <see cref="LookupFlags.All"/>.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int AllFlags()
    {
        var total = 0;
        foreach (var tile in _tiles)
        {
            _set.Clear();
            _lookup.GetLocalEntitiesIntersecting(_grid, tile, _set, 0f, LookupFlags.All);
            total += _set.Count;
        }

        return total;
    }

    /// <summary>
    /// Fixed behaviour: HighPressureDelta only consumes dynamic/sundries movable bodies.
    /// </summary>
    [Benchmark]
    public int NarrowFlags()
    {
        var total = 0;
        foreach (var tile in _tiles)
        {
            _set.Clear();
            _lookup.GetLocalEntitiesIntersecting(_grid, tile, _set, 0f, LookupFlags.Dynamic | LookupFlags.Sundries);
            total += _set.Count;
        }

        return total;
    }
}
