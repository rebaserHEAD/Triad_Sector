#nullable enable

using System;
using System.Collections.Generic;
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
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Breadth, where the other drydock tests are depth. Everything else builds a ship out of a few
    /// hand-picked entities chosen to fail for one reason at a time; this one takes the ships the
    /// fork actually sells and puts every one of them through a round trip.
    ///
    /// <para>It is the test that answers the question the hand-built ones cannot: whether this
    /// fork's own content survives, including the Monolith-derived content the implementation this
    /// was ported from never saw. A vessel that refuses to store, comes back missing entities, or
    /// comes back physically inconsistent is a ship somebody paid for.</para>
    ///
    /// <para>It deliberately does not stop at the first failure. A sweep whose value is "which of
    /// the hundred and forty are broken" is worth much less if it only ever names one, so every
    /// vessel is attempted and the failures are reported together.</para>
    ///
    /// <para>One pair for the whole sweep, because acquiring a pooled pair costs roughly twenty
    /// seconds against a round trip's few, so a pair per vessel would make this an hour instead of
    /// minutes. About three minutes for the whole roster.</para>
    ///
    /// <para><b>What the first two runs found.</b> Vessels were being refused by the store's own
    /// round-trip validation, and <em>which</em> ones was not stable: two identical runs on
    /// 2026-08-26, with nothing changed between them, refused Behir and Horizon, then Medicus. The
    /// mechanism was sound effects: a sound played at grid coordinates is a real grid child until
    /// its despawn timer fires, but its prototype declares <c>save: false</c>, so the serializer
    /// never writes it. The validation counted it live, never saw it reload, and refused whichever
    /// ship had a sound in the air at that instant. The validation now counts through the
    /// serializer's own exclusion, and
    /// <see cref="DrydockRoundTripTest.ALiveSoundEffectDoesNotBlockTheStore"/> plants a sound
    /// deliberately to hold that fix down. With the timing removed, a refusal here is a real
    /// defect in a real hull, so refusals are asserted like every other failure.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRosterSweepTest
    {

        [Test]
        public async Task EveryRosterVesselSurvivesARoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var drydock = server.System<DrydockSystem>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapLoader = server.System<MapLoaderSystem>();

            var owner = Guid.NewGuid();
            await InsertPlayer(db, owner);

            // Every vessel is stored and retrieved in turn, so one berth would do; three keep a
            // single failed retrieve from turning every later store into a capacity refusal.
            var store = server.ResolveDependency<DrydockStore>();
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

            await pair.RunTicksSync(5);

            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .OrderBy(v => v.ID)
                .ToList();

            Assert.That(vessels, Is.Not.Empty, "The control: an empty roster would make this sweep vacuous.");

            var failures = new List<string>();
            var deltas = new List<string>();
            var swept = 0;

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
                {
                    failures.Add($"{vessel.ID}: its shuttle file at {vessel.ShuttlePath} would not load at all.");
                    continue;
                }

                var before = await CensusGrid(pair, loaded.Value);

                try
                {
                    var (result, shipId) = await RunOnServer(pair,
                        () => drydock.TryStoreShip(loaded.Value, owner, null));

                    if (result != DrydockStoreResult.Success || shipId == null)
                    {
                        failures.Add($"{vessel.ID}: store refused with {result}.");
                        await Cleanup(pair, loaded);
                        continue;
                    }

                    await pair.RunTicksSync(3);

                    var retrieved = await RunOnServer(pair,
                        () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));

                    if (!retrieved.Succeeded)
                    {
                        failures.Add($"{vessel.ID}: stored, then would not come back ({retrieved.Result}).");
                        continue;
                    }

                    // Ticking is the physics assertion. A grid whose broadphase or contact list came
                    // back inconsistent throws on the tick, not on the load, and the harness turns
                    // that into a failure here rather than somewhere unrelated later.
                    await pair.RunTicksSync(10);

                    var after = await CensusGrid(pair, retrieved.Grid!.Value);

                    // Reported, deliberately not asserted. Census equality is the wrong bar for a
                    // roster sweep, because a store is not supposed to be lossless: it empties
                    // unoccupied AI cores on purpose, which costs the brain vessel, the holo and
                    // their action entities. Drop and duplicate detection is the store's own
                    // validation diff, which runs on every vessel here and is what refuses the two
                    // that refuse. This line exists so a change in what a round trip costs shows up
                    // in the run output instead of being invisible.
                    if (!SameCensus(before, after))
                        deltas.Add($"{vessel.ID}: {DescribeDelta(before, after)}");

                    await Cleanup(pair, retrieved.Grid);
                }
                catch (Exception e)
                {
                    failures.Add($"{vessel.ID}: threw {e.GetType().Name}: {e.Message}");
                }

                swept++;
            }

            if (deltas.Count > 0)
            {
                await TestContext.Out.WriteLineAsync(
                    $"[roster-sweep] {deltas.Count} vessel(s) changed manifest across the round trip:"
                    + Environment.NewLine + string.Join(Environment.NewLine, deltas));
            }

            Assert.That(failures, Is.Empty,
                $"{failures.Count} roster vessel(s) failed a drydock round trip outright, out of {vessels.Count} "
                + $"({swept} completed a full attempt):{Environment.NewLine}"
                + string.Join(Environment.NewLine, failures));

            await pair.CleanReturnAsync();
        }

        private static async Task Cleanup(TestPair pair, EntityUid? grid)
        {
            if (grid == null)
                return;

            await pair.Server.WaitPost(() =>
            {
                if (!pair.Server.EntMan.Deleted(grid.Value))
                    pair.Server.EntMan.DeleteEntity(grid.Value);
            });

            await pair.RunTicksSync(3);
        }

        private static bool SameCensus(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            if (before.Count != after.Count)
                return false;

            foreach (var (proto, count) in before)
            {
                if (!after.TryGetValue(proto, out var got) || got != count)
                    return false;
            }

            return true;
        }

        private static string DescribeDelta(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            var parts = new List<string>();

            foreach (var (proto, count) in before)
            {
                var got = after.GetValueOrDefault(proto);
                if (got != count)
                    parts.Add($"{proto} {count}->{got}");
            }

            foreach (var (proto, count) in after)
            {
                if (!before.ContainsKey(proto))
                    parts.Add($"{proto} 0->{count}");
            }

            // A whole-ship difference is not worth printing in full; the first few name the shape.
            return string.Join(", ", parts.Take(8)) + (parts.Count > 8 ? $", and {parts.Count - 8} more" : "");
        }

        private static async Task<Dictionary<string, int>> CensusGrid(TestPair pair, EntityUid grid)
        {
            var census = new Dictionary<string, int>();
            var entMan = pair.Server.EntMan;

            await pair.Server.WaitPost(() =>
            {
                var stack = new Stack<EntityUid>();
                stack.Push(grid);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    var children = entMan.GetComponent<TransformComponent>(current).ChildEnumerator;

                    while (children.MoveNext(out var child))
                    {
                        stack.Push(child);

                        // The serializer's own exclusion: a save: false prototype (sounds, chat
                        // effects) is never written into a store, so a census that counts one is
                        // measuring timing rather than the round trip. Counting sounds made the
                        // first run of this sweep report a hundred and twenty differences that
                        // were all timing, and the store's validation had the same bug.
                        var meta = entMan.GetComponent<MetaDataComponent>(child);
                        if (meta.EntityPrototype?.MapSavable == false)
                            continue;

                        var proto = meta.EntityPrototype?.ID ?? "<no prototype>";
                        census[proto] = census.GetValueOrDefault(proto) + 1;
                    }
                }
            });

            return census;
        }

        private static async Task<T> RunOnServer<T>(TestPair pair, Func<Task<T>> start)
        {
            Task<T>? task = null;
            await pair.Server.WaitPost(() => task = start());

            for (var i = 0; i < 900 && !task!.IsCompleted; i++)
            {
                await pair.RunTicksSync(1);
            }

            Assert.That(task!.IsCompleted, Is.True, "A drydock operation never completed.");
            return await task;
        }

        private static Task InsertPlayer(IServerDbManager db, Guid userId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                context.Player.Add(new Player
                {
                    UserId = userId,
                    LastSeenUserName = $"drydock-sweep-{userId:N}",
                    FirstSeenTime = DateTime.UtcNow,
                    LastSeenTime = DateTime.UtcNow,
                    LastSeenAddress = IPAddress.Loopback,
                });

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }
    }
}
