#nullable enable

// LOCAL SCAFFOLDING, NOT FOR COMMIT. Measures how long a store actually takes and how big the
// document is, across the whole roster. Several decisions are blocked on these two numbers:
// blob retention depth, the manifest horizon, and whether a real progress bar is worth the work
// of moving compression off the game thread.

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
        private sealed record Row(string Vessel, string Class, double Ms, int Uncompressed, int Entities);

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
                shipyard.SetupShipyardIfNeeded();

                var station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);
            });

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

                var sw = Stopwatch.StartNew();
                var (result, shipId) = await RunOnServer(pair, () => drydock.TryStoreShip(loaded.Value, owner, null));
                sw.Stop();

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
                    sw.Elapsed.TotalMilliseconds,
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
        /// The part that actually blocks. TryStoreShip's wall time includes database awaits that
        /// yield the thread; serializing the grid and compressing the result do not. This times
        /// those two alone, which is the number that says whether the server stalls.
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
            var ms = rows.Select(r => r.Ms).OrderBy(x => x).ToList();
            var bytes = rows.Select(r => r.Uncompressed).OrderBy(x => x).ToList();

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine($"=== stored {rows.Count} vessels ===");
            TestContext.Out.WriteLine($"store ms      min {ms.First():F0}  p50 {Pct(ms, 0.5):F0}  p90 {Pct(ms, 0.9):F0}  max {ms.Last():F0}");
            TestContext.Out.WriteLine($"document KB   min {bytes.First() / 1024}  p50 {Pct(bytes.Select(b => (double)b).ToList(), 0.5) / 1024:F0}  max {bytes.Last() / 1024}");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("slowest ten:");
            foreach (var r in rows.OrderByDescending(r => r.Ms).Take(10))
                TestContext.Out.WriteLine($"  {r.Ms,7:F0} ms  {r.Uncompressed / 1024,5} KB  {r.Entities,5} ents  {r.Class,-13} {r.Vessel}");

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("by class:");
            foreach (var g in rows.GroupBy(r => r.Class).OrderBy(g => g.Key))
                TestContext.Out.WriteLine($"  {g.Key,-13} n={g.Count(),3}  median {Pct(g.Select(r => r.Ms).OrderBy(x => x).ToList(), 0.5):F0} ms  median {Pct(g.Select(r => (double)r.Uncompressed).OrderBy(x => x).ToList(), 0.5) / 1024:F0} KB");
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

        private static async Task<T> RunOnServer<T>(Pair.TestPair pair, Func<Task<T>> run)
        {
            Task<T>? task = null;
            await pair.Server.WaitPost(() => { task = run(); });

            while (task == null || !task.IsCompleted)
                await pair.RunTicksSync(1);

            return await task;
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
