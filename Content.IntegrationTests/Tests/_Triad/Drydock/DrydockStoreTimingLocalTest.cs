#nullable enable

// LOCAL SCAFFOLDING, NOT FOR COMMIT. Measures what a store costs the server, and how big the
// document is, across the whole roster.
//
// The metric changed when the store learned to slice. Total wall time is no longer the number that
// matters and is actively misleading as one: a sliced store is meant to take longer in wall clock,
// because the ticks it spends waiting are ticks every other player got to keep. Reading the old
// stopwatch would report this design getting worse exactly as it works. So the rig now times each
// tick of the pump separately and reports the WORST one, which is the figure the whole change is
// about: what a bystander who never touched the console feels.
//
// The worst tick is expected to sit above the budget cvar, and by roughly the length of the longest
// call the pipeline cannot interrupt. Two of those dominate: serializing the grid, and the
// round-trip validation load. MeasureTheBlockingSpanAlone below prices the first of them directly,
// so the two numbers can be read against each other.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    [TestFixture]
    public sealed class DrydockStoreTimingLocalTest
    {
        /// <summary>
        /// The budget the sweep runs at, stated rather than inherited, so two runs on two machines
        /// are comparing the same thing. The shipping default, because the interesting question is
        /// what the setting we actually ship does to the worst tick.
        /// </summary>
        private const int MeasuredBudgetMs = 2;

        private sealed record Row(
            string Vessel,
            string Class,
            double WallMs,
            double WorstTickMs,
            int Ticks,
            int Uncompressed,
            int Entities);

        [Test]
        public async Task MeasureStoreCostAcrossTheRoster()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapLoader = server.System<MapLoaderSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            for (var i = 0; i < 3; i++)
                await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var map = await pair.CreateTestMap();

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);

                // Slicing on, at the shipping default. This rig exists to price the worst tick, and
                // at a budget of zero there is no job, no suspension and therefore no worst tick to
                // price: the whole store lands in one of them, which is the state this design was
                // written to leave behind.
                cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, MeasuredBudgetMs);

                shipyard.SetupShipyardIfNeeded();

                var station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);
            });

            // The station stands on the test grid, which the fork's janitors are built to delete.
            await pair.MakeCleanupImmune(map.Grid.Owner);

            await pair.RunTicksSync(5);

            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .OrderBy(v => v.ID)
                .ToList();

            var rows = new List<Row>();

            foreach (var vessel in vessels)
            {
                EntityUid? loaded = null;
                await server.WaitPost(() =>
                {
                    if (mapLoader.TryLoadGrid(map.MapId, vessel.ShuttlePath, out var grid))
                        loaded = grid!.Value.Owner;
                });
                await pair.RunTicksSync(3);

                if (loaded == null)
                    continue;

                var measured = await RunOnServer(pair, () => drydock.TryStoreShip(loaded.Value, owner, null));
                var (result, shipId) = measured.Result;

                if (result != DrydockStoreResult.Success || shipId == null)
                {
                    TestContext.Out.WriteLine($"  skipped {vessel.ID}: {result}");
                    await server.WaitPost(() => { if (entMan.EntityExists(loaded.Value)) entMan.DeleteEntity(loaded.Value); });
                    continue;
                }

                var current = await store.LoadCurrent(shipId.Value);
                rows.Add(new Row(
                    vessel.ID,
                    current!.Ship.SizeClass ?? "?",
                    measured.WallMs,
                    measured.WorstTickMs,
                    measured.Ticks,
                    current.Revision.SizeBytes,
                    CountManifestEntities(current.Revision.Manifest)));

                await store.TryDeleteShip(shipId.Value, owner, null, "timing run");
                await pair.RunTicksSync(1);
            }

            Report(rows);

            Assert.That(rows, Is.Not.Empty, "Nothing was measured, so the run says nothing.");
            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The part that still blocks, after slicing. Everything the store does per entity can be
        /// spread over ticks; serializing the grid cannot, because it is one call into the engine's
        /// serializer with no seam a content pipeline can suspend at. So this number is the floor
        /// under the worst tick the sweep above reports: no budget can make a store's worst tick
        /// smaller than its longest un-interruptible call.
        ///
        /// <para>Compression is timed here too, though it no longer belongs in that floor: it is
        /// pure byte work touching no entity, so the pipeline hands it to a worker thread. It is
        /// kept in the report as the control on that decision, since the reason to move it off the
        /// game thread is exactly how large this column is.</para>
        /// </summary>
        [Test]
        public async Task MeasureTheBlockingSpanAlone()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapLoader = server.System<MapLoaderSystem>();

            var map = await pair.CreateTestMap();
            await pair.RunTicksSync(3);

            // A spread across the roster, plus the four the full run measured as the slowest,
            // because the worst case is the number that decides whether this is a problem.
            var worst = new[] { "Windreign", "Prospector", "Zeros", "Phalanx", "Promise" };
            var all = protoMan.EnumeratePrototypes<VesselPrototype>().Where(v => !v.Abstract).OrderBy(v => v.ID).ToList();
            var sample = all.Where((_, i) => i % 12 == 0)
                .Concat(all.Where(v => worst.Contains(v.ID)))
                .DistinctBy(v => v.ID)
                .ToList();

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("=== the synchronous span, per vessel ===");
            TestContext.Out.WriteLine("  serialize   compress    total   KB   vessel");

            foreach (var vessel in sample)
            {
                EntityUid? grid = null;
                await server.WaitPost(() =>
                {
                    if (mapLoader.TryLoadGrid(map.MapId, vessel.ShuttlePath, out var g))
                        grid = g!.Value.Owner;
                });
                await pair.RunTicksSync(3);
                if (grid == null)
                    continue;

                double serialize = 0, compress = 0;
                var bytes = 0;

                await server.WaitPost(() =>
                {
                    // The production options, so this measures what the store measures.
                    var opts = new Robust.Shared.EntitySerialization.SerializationOptions
                    {
                        MissingEntityBehaviour = Robust.Shared.EntitySerialization.MissingEntityBehaviour.Ignore,
                    };

                    var sw = Stopwatch.StartNew();
                    using var writer = new System.IO.StringWriter();
                    mapLoader.TrySaveGrid(grid.Value, writer, opts);
                    sw.Stop();
                    serialize = sw.Elapsed.TotalMilliseconds;

                    var yaml = System.Text.Encoding.UTF8.GetBytes(writer.ToString());
                    bytes = yaml.Length;

                    var sw2 = Stopwatch.StartNew();
                    using (var outStream = new System.IO.MemoryStream())
                    {
                        using (var z = new Robust.Shared.Utility.ZStdCompressStream(outStream, ownStream: false))
                            z.Write(yaml, 0, yaml.Length);
                    }
                    sw2.Stop();
                    compress = sw2.Elapsed.TotalMilliseconds;
                });

                TestContext.Out.WriteLine($"  {serialize,8:F0}ms {compress,8:F0}ms {serialize + compress,8:F0}ms {bytes / 1024,5} {vessel.ID}");

                await server.WaitPost(() => { if (entMan.EntityExists(grid.Value)) entMan.DeleteEntity(grid.Value); });
                await pair.RunTicksSync(1);
            }

            await pair.CleanReturnAsync();
        }

        private static void Report(List<Row> rows)
        {
            var worst = rows.Select(r => r.WorstTickMs).OrderBy(x => x).ToList();
            var wall = rows.Select(r => r.WallMs).OrderBy(x => x).ToList();
            var bytes = rows.Select(r => r.Uncompressed).OrderBy(x => x).ToList();

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine($"=== stored {rows.Count} vessels at a {MeasuredBudgetMs} ms tick budget ===");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("The metric: worst tick. Anything above the budget is a call the pipeline could not");
            TestContext.Out.WriteLine("interrupt, and the two that dominate are the grid serialize and the validation load.");
            TestContext.Out.WriteLine($"worst tick ms  min {worst.First():F1}  p50 {Pct(worst, 0.5):F1}  p90 {Pct(worst, 0.9):F1}  max {worst.Last():F1}");
            TestContext.Out.WriteLine($"wall ms        min {wall.First():F0}  p50 {Pct(wall, 0.5):F0}  p90 {Pct(wall, 0.9):F0}  max {wall.Last():F0}   (context, not the metric)");
            TestContext.Out.WriteLine($"document KB    min {bytes.First() / 1024}  p50 {Pct(bytes.Select(b => (double)b).ToList(), 0.5) / 1024:F0}  max {bytes.Last() / 1024}");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("worst ten ticks:");
            foreach (var r in rows.OrderByDescending(r => r.WorstTickMs).Take(10))
                TestContext.Out.WriteLine($"  {r.WorstTickMs,7:F1} ms worst  {r.Ticks,5} ticks  {r.WallMs,7:F0} ms wall  {r.Uncompressed / 1024,5} KB  {r.Entities,5} ents  {r.Class,-13} {r.Vessel}");

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("by class:");
            foreach (var g in rows.GroupBy(r => r.Class).OrderBy(g => g.Key))
                TestContext.Out.WriteLine($"  {g.Key,-13} n={g.Count(),3}  median worst tick {Pct(g.Select(r => r.WorstTickMs).OrderBy(x => x).ToList(), 0.5):F1} ms  median wall {Pct(g.Select(r => r.WallMs).OrderBy(x => x).ToList(), 0.5):F0} ms  median {Pct(g.Select(r => (double)r.Uncompressed).OrderBy(x => x).ToList(), 0.5) / 1024:F0} KB");
        }

        private static double Pct(List<double> sorted, double p)
            => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1)];

        private static int CountManifestEntities(string? manifest)
        {
            if (string.IsNullOrEmpty(manifest))
                return 0;

            // The manifest is {"v":1,"e":[...]}; counting braces inside the array is enough for a
            // scale reading and avoids pulling a json dependency into a throwaway harness.
            var idx = manifest.IndexOf("\"e\"", StringComparison.Ordinal);
            return idx < 0 ? 0 : manifest.AsSpan(idx).ToString().Count(c => c == '{');
        }

        /// <summary>
        /// Runs a pipeline and prices every tick it takes, one at a time.
        ///
        /// <para>The worst single tick is the number that matters. A store is allowed to be slow;
        /// it is not allowed to be felt by anybody who did not ask for it, and one tick is the unit
        /// of being felt. Timing the pump as a whole, which is what this used to do, answers a
        /// question nobody has: it counts every idle tick the pair spent waiting on the database as
        /// though the server had been busy for it.</para>
        ///
        /// <para>Wall clock is still reported alongside, because it is what the captain at the
        /// console experiences and it is what decides whether the progress indicator is worth its
        /// weight. It is context, not the metric.</para>
        ///
        /// <para>This measures the whole tick, not the job's own slice: everything else the server
        /// does that tick is in the figure. That is deliberate, since a bystander feels the tick and
        /// not the slice. The job's own worst slice is narrower and is reported by the pipeline's
        /// timing line in the server log rather than read from here.</para>
        /// </summary>
        private static async Task<(T Result, double WallMs, double WorstTickMs, int Ticks)> RunOnServer<T>(
            Pair.TestPair pair,
            Func<Task<T>> run)
        {
            var wall = Stopwatch.StartNew();

            // The kick-off is timed and counted as a tick, because on the unsliced arm it IS the
            // expensive one. With no job the pipeline runs on the caller's async path and only a
            // real await hands the thread back, so everything from the start to the first database
            // call - freeze, purge, appraise, sidecars, strip, capture, prepare, serialize and
            // validate - executes inside this WaitPost, before the loop below ever starts a
            // stopwatch. Timing only the loop reports the unsliced store as an order of magnitude
            // cheaper than the sliced one by simply not looking at its worst span.
            Task<T>? task = null;
            var kick = Stopwatch.StartNew();
            await pair.Server.WaitPost(() => { task = run(); });
            kick.Stop();

            var worstTickMs = kick.Elapsed.TotalMilliseconds;
            var ticks = 1;

            while (task == null || !task.IsCompleted)
            {
                var tick = Stopwatch.StartNew();
                await pair.RunTicksSync(1);
                tick.Stop();

                ticks++;
                if (tick.Elapsed.TotalMilliseconds > worstTickMs)
                    worstTickMs = tick.Elapsed.TotalMilliseconds;
            }

            wall.Stop();

            return (await task, wall.Elapsed.TotalMilliseconds, worstTickMs, ticks);
        }

        /// <summary>
        /// Old against new, on the same hulls in the same server, with the per-phase breakdown of
        /// what a tick now pays.
        ///
        /// <para>The headline comparison uses ONE instrument for both arms: the pump's own per-tick
        /// stopwatch. That is a whole pair tick, server and client and sync, so it carries a couple
        /// of milliseconds of harness overhead, which is why it is quoted only against figures far
        /// above that and why both arms are measured with it rather than one arm with it and the
        /// other with something narrower.</para>
        ///
        /// <para>The per-phase table comes from the pipeline's own <see cref="DrydockSpanMeter"/>,
        /// which is main-thread pipeline time only and exists solely on the sliced arm. There is no
        /// per-phase old column on purpose: deriving one from the wall-clock phase timer would be
        /// invalid for every phase whose mark straddles a real await, because those marks contain
        /// database and thread-pool time plus up to a whole tick of parking. Printing that beside a
        /// sliced figure that correctly excludes both would invent an improvement in the three
        /// Task.Run phases that are byte-identical on the two paths.</para>
        ///
        /// <para>What the old arm gets instead is its worst tick, which is the number the design is
        /// actually judged against, measured the same way as the new one.</para>
        /// </summary>
        [Test]
        public async Task CompareSlicedAgainstUnsliced()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapLoader = server.System<MapLoaderSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);
            for (var i = 0; i < 3; i++)
                await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var map = await pair.CreateTestMap();

            EntityUid station = default;
            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                shipyard.SetupShipyardIfNeeded();

                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);
            });

            await pair.MakeCleanupImmune(map.Grid.Owner);
            await pair.RunTicksSync(5);

            // A spread rather than the roster: this runs every hull twice per repeat, so breadth
            // costs four times what the one-armed sweep costs. Smallest, largest and two from the
            // middle is enough to show whether the per-tick cost tracks hull size.
            var all = protoMan.EnumeratePrototypes<VesselPrototype>().Where(v => !v.Abstract).OrderBy(v => v.ID).ToList();
            var picked = new[] { "Kestrel", "Prospector", "Windreign", "Zeros" };
            var sample = all.Where(v => picked.Contains(v.ID)).ToList();
            if (sample.Count == 0)
                sample = all.Where((_, i) => i % 37 == 0).ToList();

            Assert.That(sample, Is.Not.Empty, "Nothing to measure, so the run says nothing.");

            const int repeats = 3;
            var arms = new[] { 0, MeasuredBudgetMs };

            // hull -> budget -> the worst tick each repeat produced, by the pump's own clock.
            var pumped = new Dictionary<string, Dictionary<int, List<double>>>();
            // phase -> worst tick it ever cost on the sliced arm, and the total it ever cost.
            var phaseWorst = new Dictionary<DrydockPhase, double>();
            var phaseTotal = new Dictionary<DrydockPhase, double>();
            var meterWorst = new Dictionary<string, double>();

            // Unmeasured, so the first hull does not pay for JIT on behalf of the arm it happens to
            // run in.
            await RunOneStore(pair, drydock, store, mapLoader, entMan, server, cfg, map, owner, sample[0], 0);

            foreach (var vessel in sample)
            {
                pumped[vessel.ID] = new Dictionary<int, List<double>>();

                for (var rep = 0; rep < repeats; rep++)
                {
                    foreach (var budget in arms)
                    {
                        var measured = await RunOneStore(pair, drydock, store, mapLoader, entMan, server, cfg, map, owner, vessel, budget);
                        if (measured == null)
                            continue;

                        if (!pumped[vessel.ID].TryGetValue(budget, out var list))
                            pumped[vessel.ID][budget] = list = new List<double>();
                        list.Add(measured.WorstTickMs);

                        if (budget <= 0)
                            continue;

                        // The tripwire: a stale table would otherwise be read as this run's.
                        Assert.That(measured.CostsRun, Is.EqualTo(measured.ExpectedRun),
                            $"{vessel.ID}: the phase table belongs to a different run, so every number below it would be someone else's.");

                        meterWorst[vessel.ID] = Math.Max(meterWorst.GetValueOrDefault(vessel.ID), measured.MeterWorstMs);

                        foreach (var (phase, cost) in measured.Costs)
                        {
                            phaseWorst[phase] = Math.Max(phaseWorst.GetValueOrDefault(phase), cost.WorstTickMs);
                            phaseTotal[phase] = Math.Max(phaseTotal.GetValueOrDefault(phase), cost.TotalMs);
                        }
                    }
                }
            }

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine($"=== worst tick, unsliced against sliced at {MeasuredBudgetMs}ms, {repeats} repeats ===");
            TestContext.Out.WriteLine("  both columns are the pump's own per-tick clock, so they compare like for like.");
            TestContext.Out.WriteLine("  meter is the pipeline's own main-thread accounting, sliced arm only.");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine($"{"vessel",-14} {"old min",9} {"old med",9} {"old max",9} {"new min",9} {"new med",9} {"new max",9} {"meter",9}");

            foreach (var vessel in sample)
            {
                if (!pumped.TryGetValue(vessel.ID, out var byBudget) || byBudget.Count < 2)
                    continue;

                var old = Stats(byBudget.GetValueOrDefault(0));
                var now = Stats(byBudget.GetValueOrDefault(MeasuredBudgetMs));
                TestContext.Out.WriteLine(
                    $"{vessel.ID,-14} {old.Min,9:F1} {old.Med,9:F1} {old.Max,9:F1} {now.Min,9:F1} {now.Med,9:F1} {now.Max,9:F1} {meterWorst.GetValueOrDefault(vessel.ID),9:F1}");
            }

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("=== sliced arm, per phase: the worst any single tick paid ===");
            TestContext.Out.WriteLine($"{"phase",-14} {"worst tick",11} {"total",10}");
            foreach (var (phase, worst) in phaseWorst.OrderByDescending(p => p.Value))
                TestContext.Out.WriteLine($"{phase,-14} {worst,11:F2} {phaseTotal.GetValueOrDefault(phase),10:F1}");

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("Caveats that belong with these numbers:");
            TestContext.Out.WriteLine("  - DebugOpt, not Release. Absolute values are pessimistic; the ratio is the point.");
            TestContext.Out.WriteLine("  - The pump tick is a whole pair tick, so it carries harness overhead. Its floor is not zero.");
            TestContext.Out.WriteLine("  - Rows do not sum to the headline: a span before the first phase opens counts against the");
            TestContext.Out.WriteLine("    tick and against no phase, and Despawn's head is booked to Commit because the pipeline");
            TestContext.Out.WriteLine("    deliberately refuses to suspend between the commit and ctx.Committed.");
            TestContext.Out.WriteLine("  - The full-tree reparent runs before Begin(Freeze), so it books to the phase before it.");
            TestContext.Out.WriteLine("  - Hash, drift and compress run on a worker thread on BOTH arms, so their per-tick cost is");
            TestContext.Out.WriteLine("    near zero here and that is not an improvement over the old path.");

            Assert.That(phaseWorst, Is.Not.Empty, "No phase costs were recorded, so the per-phase table says nothing.");
            await pair.CleanReturnAsync();
        }

        private static (double Min, double Med, double Max) Stats(List<double>? values)
        {
            if (values == null || values.Count == 0)
                return (0, 0, 0);

            var sorted = values.OrderBy(v => v).ToList();
            return (sorted[0], sorted[sorted.Count / 2], sorted[^1]);
        }

        private sealed record StoreMeasurement(
            double WorstTickMs,
            double MeterWorstMs,
            int CostsRun,
            int ExpectedRun,
            IReadOnlyDictionary<DrydockPhase, DrydockPhaseCost> Costs);

        /// <summary>Loads a hull, stores it at the given budget, reads the meter, and cleans up after itself.</summary>
        private static async Task<StoreMeasurement?> RunOneStore(
            Pair.TestPair pair,
            DrydockSystem drydock,
            DrydockStore store,
            MapLoaderSystem mapLoader,
            IEntityManager entMan,
            Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server,
            IConfigurationManager cfg,
            TestMapData map,
            Guid owner,
            VesselPrototype vessel,
            int budget)
        {
            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, budget));

            EntityUid? loaded = null;
            await server.WaitPost(() =>
            {
                if (mapLoader.TryLoadGrid(map.MapId, vessel.ShuttlePath, out var grid))
                    loaded = grid!.Value.Owner;
            });
            await pair.RunTicksSync(3);

            if (loaded == null)
                return null;

            var before = drydock.LastPhaseCostsRun;
            var measured = await RunOnServer(pair, () => drydock.TryStoreShip(loaded.Value, owner, null));
            var (result, shipId) = measured.Result;

            if (result != DrydockStoreResult.Success || shipId == null)
            {
                await server.WaitPost(() => { if (entMan.EntityExists(loaded.Value)) entMan.DeleteEntity(loaded.Value); });
                return null;
            }

            var costs = drydock.LastPhaseCosts;
            var meterWorst = drydock.LastWorstTickMs;
            var run = drydock.LastPhaseCostsRun;

            await store.TryDeleteShip(shipId.Value, owner, null, "benchmark");
            await pair.RunTicksSync(1);

            return new StoreMeasurement(
                measured.WorstTickMs,
                meterWorst,
                run,
                before + 1,
                costs ?? new Dictionary<DrydockPhase, DrydockPhaseCost>());
        }

        private static Task InsertPlayer(IServerDbManager db, Guid userId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                context.Player.Add(new Player
                {
                    UserId = userId,
                    LastSeenUserName = $"timing-{userId:N}",
                    FirstSeenTime = DateTime.UtcNow,
                    LastSeenTime = DateTime.UtcNow,
                    LastSeenAddress = IPAddress.Loopback,
                });

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }
    }
}
