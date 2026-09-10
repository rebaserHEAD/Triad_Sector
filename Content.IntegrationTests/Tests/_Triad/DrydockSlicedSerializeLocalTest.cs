#nullable enable

// LOCAL SCAFFOLDING, NOT FOR COMMIT. Prices the store's serialize phase with and without the sliced
// serializer, and proves the sliced document still reloads.
//
// The metric is the WORST TICK, per DrydockStoreTimingLocalTest. Wall clock will get WORSE with
// slicing on and that is the design working: the ship is frozen on a paused private map, so making
// the one captain who pressed the button wait is free, and making sixty bystanders wait is not.
// Reading wall clock here would report this change failing exactly as it succeeds.
//
// The correctness arm matters as much as the timing one. The sliced document cannot be compared
// byte for byte against the engine's: we skip InitializeTileMap (cosmetic, the engine's own comment
// says it "is not actually required") and yaml ids are reserved in a different walk order. So the
// oracle is behavioural instead: store BOTH ways, retrieve BOTH ways, and require the reborn ship to
// carry the same entity population either way.

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

namespace Content.IntegrationTests.Tests._Triad
{
    [TestFixture]
    public sealed class DrydockSlicedSerializeLocalTest
    {
        private const int MeasuredBudgetMs = 2;
        private const int SampleStride = 10;

        private static readonly string[] KnownWorst = { "Windreign", "Prospector", "Zeros", "Phalanx", "Promise" };

        private sealed record Arm(
            double WorstTickMs,
            double WallMs,
            int Ticks,
            DrydockStoreResult Store,
            int Bytes,
            int StoredEntities,
            int RebornEntities);

        private sealed record Row(string Vessel, string Class, Arm Batch, Arm Sliced);

        [Test]
        public async Task PriceTheSlicedSerializerAgainstTheEngineCall()
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
            for (var i = 0; i < 4; i++)
                await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var map = await pair.CreateTestMap();
            EntityUid station = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, MeasuredBudgetMs);

                shipyard.SetupShipyardIfNeeded();

                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);
            });

            await pair.MakeCleanupImmune(map.Grid.Owner);
            await pair.RunTicksSync(5);

            var all = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .OrderBy(v => v.ID)
                .ToList();

            var sample = all.Where((_, i) => i % SampleStride == 0)
                .Concat(all.Where(v => KnownWorst.Contains(v.ID)))
                .DistinctBy(v => v.ID)
                .OrderBy(v => v.ID)
                .ToList();

            // One discarded cycle per arm before anything is recorded. The first store of a process
            // is measurably dearer than the second and we have not established why, so charging it to
            // whichever arm ran first would be most of the difference being measured.
            foreach (var sliced in new[] { false, true })
                await Cycle(pair, cfg, drydock, store, mapLoader, entMan, map.MapId, station, sample[0], owner, sliced);

            var rows = new List<Row>();
            var problems = new List<string>();

            for (var i = 0; i < sample.Count; i++)
            {
                var vessel = sample[i];
                var order = i % 2 == 0 ? new[] { false, true } : new[] { true, false };
                var arms = new Dictionary<bool, (Arm Arm, string Class)>();

                foreach (var sliced in order)
                {
                    var measured = await Cycle(pair, cfg, drydock, store, mapLoader, entMan, map.MapId, station, vessel, owner, sliced);
                    if (measured == null)
                        break;

                    arms[sliced] = measured.Value;
                }

                if (arms.Count != 2)
                {
                    TestContext.Out.WriteLine($"  skipped {vessel.ID}: did not complete both arms");
                    continue;
                }

                var batch = arms[false];
                var sliced2 = arms[true];

                foreach (var (name, a) in new[] { ("batch", batch.Arm), ("sliced", sliced2.Arm) })
                {
                    if (a.Store != DrydockStoreResult.Success)
                        problems.Add($"{vessel.ID}: {name} store returned {a.Store}");
                    else if (a.RebornEntities == 0)
                        problems.Add($"{vessel.ID}: {name} document did not reload");
                }

                // The real oracle. The two documents cannot match byte for byte, but the ship they
                // describe has to.
                if (batch.Arm.RebornEntities != sliced2.Arm.RebornEntities)
                {
                    problems.Add(
                        $"{vessel.ID}: reborn population differs, batch={batch.Arm.RebornEntities} sliced={sliced2.Arm.RebornEntities}");
                }

                rows.Add(new Row(vessel.ID, batch.Class, batch.Arm, sliced2.Arm));
            }

            Report(rows);

            // Printed rather than only asserted, because the assertion message is the first thing a
            // truncated console log loses and it is the only part of this run that decides anything.
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine($"=== correctness: {problems.Count} problem(s) ===");
            foreach (var p in problems)
                TestContext.Out.WriteLine($"  {p}");
            if (problems.Count == 0)
                TestContext.Out.WriteLine("  none: every hull stored and reloaded identically both ways.");

            Assert.That(rows, Is.Not.Empty, "Nothing was measured, so the run says nothing.");
            Assert.That(problems, Is.Empty,
                "The sliced serializer did not match the engine call:\n  " + string.Join("\n  ", problems));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Load a hull, store it in one mode with the store timed, retrieve it to prove the document
        /// reloads, then clean up. Returns null if the hull would not load at all.
        /// </summary>
        private static async Task<(Arm Arm, string Class)?> Cycle(
            Pair.TestPair pair,
            IConfigurationManager cfg,
            DrydockSystem drydock,
            DrydockStore store,
            MapLoaderSystem mapLoader,
            IEntityManager entMan,
            Robust.Shared.Map.MapId mapId,
            EntityUid station,
            VesselPrototype vessel,
            Guid owner,
            bool sliced)
        {
            EntityUid? loaded = null;
            await pair.Server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockSlicedSerialize, sliced);

                if (mapLoader.TryLoadGrid(mapId, vessel.ShuttlePath, out var grid))
                    loaded = grid!.Value.Owner;
            });

            await pair.RunTicksSync(3);

            if (loaded == null)
                return null;

            var measured = await RunOnServer(pair, () => drydock.TryStoreShip(loaded.Value, owner, null));
            var (result, shipId) = measured.Result;

            if (result != DrydockStoreResult.Success || shipId == null)
            {
                await pair.Server.WaitPost(() =>
                {
                    if (entMan.EntityExists(loaded.Value))
                        entMan.DeleteEntity(loaded.Value);
                });
                await pair.RunTicksSync(1);
                return (new Arm(measured.WorstTickMs, measured.WallMs, measured.Ticks, result, 0, 0, 0), "?");
            }

            var current = await store.LoadCurrent(shipId.Value);
            var bytes = current!.Revision.SizeBytes;
            var storedEntities = CountManifestEntities(current.Revision.Manifest);
            var sizeClass = current.Ship.SizeClass ?? "?";

            // Prove the document reloads, and count what came back. This is the whole correctness
            // argument for the sliced walk: it is not enough that it produced bytes.
            var reborn = 0;
            var back = await RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));
            if (back.Result.Succeeded && back.Result.Grid is { } rebornGrid)
            {
                await pair.Server.WaitPost(() => { reborn = CountTree(entMan, rebornGrid); });
                await pair.RunTicksSync(1);

                await pair.Server.WaitPost(() =>
                {
                    if (entMan.EntityExists(rebornGrid))
                        entMan.DeleteEntity(rebornGrid);
                });
                await pair.RunTicksSync(1);
            }

            await store.TryDeleteShip(shipId.Value, owner, null, "sliced-serialize run");
            await pair.RunTicksSync(1);

            var arm = new Arm(
                measured.WorstTickMs,
                measured.WallMs,
                measured.Ticks,
                result,
                bytes,
                storedEntities,
                reborn);

            return (arm, sizeClass);
        }

        private static int CountTree(IEntityManager entMan, EntityUid root)
        {
            var count = 0;
            var stack = new Stack<EntityUid>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var uid = stack.Pop();
                count++;

                var children = entMan.GetComponent<TransformComponent>(uid).ChildEnumerator;
                while (children.MoveNext(out var child))
                    stack.Push(child);
            }

            return count;
        }

        private static void Report(List<Row> rows)
        {
            var batchWorst = rows.Select(r => r.Batch.WorstTickMs).OrderBy(x => x).ToList();
            var slicedWorst = rows.Select(r => r.Sliced.WorstTickMs).OrderBy(x => x).ToList();
            var batchWall = rows.Select(r => r.Batch.WallMs).OrderBy(x => x).ToList();
            var slicedWall = rows.Select(r => r.Sliced.WallMs).OrderBy(x => x).ToList();

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine($"=== {rows.Count} vessels, stored both ways, at a {MeasuredBudgetMs} ms tick budget ===");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("batch  = TrySaveGrid, one un-interruptible engine call");
            TestContext.Out.WriteLine("sliced = SerializeEntity per entity against the tick budget");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine($"WORST TICK ms  batch  p50 {Pct(batchWorst, 0.5):F1}  p90 {Pct(batchWorst, 0.9):F1}  max {batchWorst.Last():F1}");
            TestContext.Out.WriteLine($"WORST TICK ms  sliced p50 {Pct(slicedWorst, 0.5):F1}  p90 {Pct(slicedWorst, 0.9):F1}  max {slicedWorst.Last():F1}");
            TestContext.Out.WriteLine($"wall ms        batch  p50 {Pct(batchWall, 0.5):F0}   sliced p50 {Pct(slicedWall, 0.5):F0}   (expected to RISE; not the metric)");
            TestContext.Out.WriteLine("");

            var overBudget = rows.Count(r => r.Sliced.WorstTickMs > MeasuredBudgetMs * 10);
            TestContext.Out.WriteLine($"sliced runs whose worst tick still exceeded 10x the budget: {overBudget} of {rows.Count}");

            var ratios = rows.Where(r => r.Sliced.WorstTickMs > 0)
                .Select(r => r.Batch.WorstTickMs / r.Sliced.WorstTickMs)
                .OrderBy(x => x)
                .ToList();
            if (ratios.Count > 0)
                TestContext.Out.WriteLine($"batch / sliced worst tick: p50 {Pct(ratios, 0.5):F2}x  max {ratios.Last():F2}x");

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("per vessel, worst ten by batch worst tick:");
            TestContext.Out.WriteLine("    batch   sliced   stored  reborn      KB  class          vessel");
            foreach (var r in rows.OrderByDescending(r => r.Batch.WorstTickMs).Take(10))
            {
                TestContext.Out.WriteLine(
                    $"  {r.Batch.WorstTickMs,7:F1} {r.Sliced.WorstTickMs,8:F1} {r.Batch.StoredEntities,8} {r.Batch.RebornEntities,7} {r.Batch.Bytes / 1024,7}  {r.Class,-13}  {r.Vessel}");
            }
        }

        private static double Pct(List<double> sorted, double p)
            => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int) (sorted.Count * p), 0, sorted.Count - 1)];

        private static int CountManifestEntities(string? manifest)
        {
            if (string.IsNullOrEmpty(manifest))
                return 0;

            var idx = manifest.IndexOf("\"e\"", StringComparison.Ordinal);
            return idx < 0 ? 0 : manifest.AsSpan(idx).ToString().Count(c => c == '{');
        }

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
                    LastSeenUserName = $"slice-{userId:N}",
                    FirstSeenTime = DateTime.UtcNow,
                    LastSeenTime = DateTime.UtcNow,
                    LastSeenAddress = IPAddress.Loopback,
                });

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }
    }
}
