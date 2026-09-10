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
            Task<T>? task = null;
            await pair.Server.WaitPost(() => { task = run(); });

            var wall = Stopwatch.StartNew();
            var worstTickMs = 0d;
            var ticks = 0;

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
