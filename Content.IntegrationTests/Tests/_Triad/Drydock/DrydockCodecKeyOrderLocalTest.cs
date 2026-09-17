#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Whether a mapping's key order is content.
    ///
    /// <para>The grid image is meant to be stored as <c>jsonb</c> so a modify operation can address one
    /// field in SQL. PostgreSQL normalizes a <c>jsonb</c> object: it sorts the keys (by length, then
    /// bytewise) and drops duplicates. The engine's own node model does the opposite, keeping insertion
    /// order in <see cref="MappingDataNode"/>'s list and writing it back in that order, so the storage
    /// type can only be <c>jsonb</c> if no reader depends on the order it drops.</para>
    ///
    /// <para>The measurement: parse each shuttle file, write it back through one pipeline twice untouched
    /// and once with every mapping re-inserted in PostgreSQL's key order, then load all three and compare
    /// them with <see cref="DrydockFidelitySystem.DeepSnapshotGrid"/>.</para>
    ///
    /// <para>The control is the second untouched load, and it is not optional: a load rolls its own
    /// randomness (wire seeds, device addresses, random spawners), so two loads of one file differ by
    /// dozens of keys before key order is touched at all. Only a difference the sorted load shows and the
    /// untouched reload does not can be key order, and that difference names the reader that depends on it.</para>
    ///
    /// <para>The control's blind spot, stated rather than left to be found: a key that is both rolled per
    /// load and read in mapping order is hidden by the floor it sets. So a clean run says there is no
    /// order-dependent reader among the keys that hold steady across loads, not that there is none at all.
    /// On this corpus the masked set is wire seeds, device addresses and what a random spawner rolled,
    /// every one of which the image carries as a value.</para>
    ///
    /// <para>And a second: a key that only the sorted load carries cannot be told from a random spawn while
    /// there is one sorted load, so the "present in all three" filter drops it with the spawns. Tightening
    /// that needs a second sorted load to give those keys their own floor, not more triage here.</para>
    ///
    /// <para>Run: <c>dotnet test Content.IntegrationTests --no-build --filter "FullyQualifiedName~DrydockCodecKeyOrderLocalTest" --logger "console;verbosity=detailed"</c>.</para>
    /// </summary>
    [TestFixture]
    [Explicit("Codec measurement. Run deliberately and read its report.")]
    [TestOf(typeof(DrydockFidelitySystem))]
    public sealed class DrydockCodecKeyOrderLocalTest
    {
        /// <summary>The seed every load rolls from, so randomness is identical across the three.</summary>
        private const int LoadSeed = 20260917;

        /// <summary>PostgreSQL's <c>jsonb</c> object key order: shorter keys first, then bytewise.</summary>
        private sealed class JsonbKeyOrder : IComparer<string>
        {
            public static readonly JsonbKeyOrder Instance = new();

            public int Compare(string? x, string? y)
            {
                if (x == null || y == null)
                    return string.CompareOrdinal(x, y);

                return x.Length != y.Length
                    ? x.Length.CompareTo(y.Length)
                    : string.CompareOrdinal(x, y);
            }
        }

        [Test]
        public async Task KeyOrderIsNotContent()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var resMan = server.ResolveDependency<IResourceManager>();
            var mapLoader = server.System<MapLoaderSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();

            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Select(vessel => vessel.ShuttlePath)
                .Distinct()
                .OrderBy(path => path.ToString(), StringComparer.Ordinal)
                .ToList();

            Assert.That(vessels, Is.Not.Empty, "The control: no vessel prototype named a shuttle file.");

            var map = await pair.CreateTestMap();
            var differing = new List<string>();
            var compared = 0;
            var reordered = 0;
            var floor = 0;
            var rolled = 0;
            var clocked = 0;
            var reorderedRenders = 0;
            var renderOnly = new List<string>();
            var unseeded = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var path in vessels)
            {
                if (!resMan.TryContentFileRead(path, out var stream))
                    continue;

                DataNode root;
                using (var reader = new StreamReader(stream))
                {
                    var documents = DataNodeParser.ParseYamlStream(reader).ToList();
                    if (documents.Count != 1)
                        continue;

                    root = documents[0].Root;
                }

                var sorted = Reorder(root, out var moved);
                reordered += moved;

                var plainPath = new ResPath($"/DrydockKeyOrder/{path.FilenameWithoutExtension}_plain.yml");
                var sortedPath = new ResPath($"/DrydockKeyOrder/{path.FilenameWithoutExtension}_sorted.yml");
                Write(resMan, plainPath, root);
                Write(resMan, sortedPath, sorted);

                // One load at a time, so two grids never share a tick or a tile.
                var first = await Load(pair, fidelity, mapLoader, map.MapId, plainPath);
                var control = await Load(pair, fidelity, mapLoader, map.MapId, plainPath);
                var sortedLoad = await Load(pair, fidelity, mapLoader, map.MapId, sortedPath);

                if (first == null || control == null || sortedLoad == null)
                {
                    differing.Add($"{path}: a load failed (plain {first != null}, control {control != null}, sorted {sortedLoad != null})");
                    continue;
                }

                compared++;

                // What a reload alone moves: wire seeds, device addresses, whatever a random spawner rolled.
                var noise = KeysOf(DrydockStateSnapshot.Diff(first, control));
                var candidates = KeysOf(DrydockStateSnapshot.Diff(first, sortedLoad));
                candidates.ExceptWith(noise);
                floor += noise.Count;
                foreach (var key in noise)
                {
                    var member = key[(key.IndexOf('|') + 1)..];
                    var cut = member.IndexOfAny(new[] { ' ', ':' });
                    member = cut < 0 ? member : member[..cut];
                    unseeded[member] = unseeded.GetValueOrDefault(member) + 1;
                }

                // Two more subtractions the per-key floor cannot make on its own. A random spawner rolls a
                // different prototype each load, so the keys it produces are not the same keys twice and a
                // key set differencing cannot cancel them; only a key all three loads carry is comparable.
                // And a time renders against a clock that moved between the loads, so its drift is the run's,
                // not the order's. Both exclusions are counted and reported rather than assumed harmless.
                var shared = candidates.Count;
                candidates.RemoveWhere(key => !first.Values.ContainsKey(key)
                                              || !control.Values.ContainsKey(key)
                                              || !sortedLoad.Values.ContainsKey(key));
                var spawned = shared - candidates.Count;
                var timed = candidates.RemoveWhere(key => key.EndsWith(DrydockFidelitySystem.TimeSuffix, StringComparison.Ordinal));
                rolled += spawned;
                clocked += timed;

                // A dictionary field renders in enumeration order, so a re-keyed document can print the same
                // pairs in a different order. That is the detector's render moving, not the loaded state, and
                // the two are told apart by comparing the renders as multisets of characters.
                var resorted = new List<string>();
                foreach (var key in candidates.ToList())
                {
                    if (!SameContent(first.Values[key], sortedLoad.Values[key]))
                        continue;

                    candidates.Remove(key);
                    resorted.Add(key);
                }

                reorderedRenders += resorted.Count;
                if (resorted.Count > 0)
                    renderOnly.Add($"{path}: {resorted.Count} key(s) hold the same content in a different order, e.g. {resorted.Order().First()}");

                if (candidates.Count > 0)
                    differing.Add($"{path}: {candidates.Count} key(s) only key order moves, e.g. {string.Join("; ", candidates.Order().Take(3))}");
            }

            await TestContext.Out.WriteLineAsync(
                $"[keyorder] seed {LoadSeed}, {compared} file(s) loaded three times each, {reordered} mapping(s) re-keyed into PostgreSQL order, "
                + $"{floor} key(s) moved by a reload alone (the control), {rolled} key(s) a random spawn made or unmade, {clocked} time key(s) the clock moved, {reorderedRenders} key(s) the same content in a different render order, {differing.Count} file(s) left where key order moved one nothing else explains.");
            foreach (var line in differing)
                await TestContext.Out.WriteLineAsync($"[keyorder] {line}");

            foreach (var line in renderOnly.Take(10))
                await TestContext.Out.WriteLineAsync($"[keyorder] render order only: {line}");

            Assert.That(compared, Is.GreaterThan(0), "The control: nothing was compared.");
            Assert.That(reordered, Is.GreaterThan(0), "The control: no mapping was re-keyed, so the test proves nothing.");
            // With the seed in, a zero floor is the good outcome rather than a broken control: it says two
            // untouched loads agreed exactly, which is what makes a difference under the sorted load readable.
            if (floor == 0)
            {
                await TestContext.Out.WriteLineAsync("[keyorder] the floor is zero: seeding made two untouched loads identical.");
            }
            else
            {
                await TestContext.Out.WriteLineAsync(
                    $"[keyorder] the floor is {floor} key(s) the seed does not reach, subtracted from every comparison. "
                    + "Ranked by member below. A time renders with its clock-relative half, which moves every tick, so every time key sits here whatever the seed does; anything else here is randomness the seed cannot reach:");
                foreach (var (member, count) in unseeded.OrderByDescending(pair => pair.Value).Take(25))
                    await TestContext.Out.WriteLineAsync($"[keyorder] unseeded {count,6}  {member}");
            }

            await pair.CleanReturnAsync();
        }

        /// <summary>Loads one grid, snapshots it and deletes it again, so no two grids share a tick.</summary>
        private static async Task<DrydockStateSnapshot?> Load(
            TestPair pair,
            DrydockFidelitySystem fidelity,
            MapLoaderSystem mapLoader,
            MapId mapId,
            ResPath path)
        {
            var server = pair.Server;
            DrydockStateSnapshot? snapshot = null;

            await server.WaitPost(() =>
            {
                // Every load rolls from the same seed, so a random sprite, a toilet seat or a random fill
                // lands the same way each time and the comparison is left with key order. Whatever the seed
                // does not reach still shows in the floor below.
                server.ResolveDependency<IRobustRandom>().SetSeed(LoadSeed);

                if (!mapLoader.TryLoadGrid(mapId, path, out var loaded))
                    return;

                snapshot = fidelity.DeepSnapshotGrid(loaded.Value.Owner);
                server.EntMan.DeleteEntity(loaded.Value.Owner);
            });

            await pair.RunTicksSync(2);
            return snapshot;
        }

        /// <summary>The keys a difference list names, so two runs compare by key rather than by rendered value.</summary>
        private static HashSet<string> KeysOf(IEnumerable<string> diff)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in diff)
            {
                var body = line[(line.IndexOf(' ') + 1)..].TrimStart();
                var colon = body.IndexOf(": ", StringComparison.Ordinal);
                var space = body.IndexOf(" (", StringComparison.Ordinal);
                var cut = colon >= 0 ? colon : space >= 0 ? space : body.Length;
                keys.Add(body[..cut]);
            }

            return keys;
        }

        /// <summary>Whether two renders hold the same characters, which is how a collection printed in a
        /// different order is told from one whose content changed.</summary>
        private static bool SameContent(string left, string right)
        {
            if (left.Length != right.Length)
                return false;

            var a = left.ToCharArray();
            var b = right.ToCharArray();
            Array.Sort(a);
            Array.Sort(b);
            return a.AsSpan().SequenceEqual(b);
        }

        /// <summary>A copy of the document with every mapping's entries re-inserted in key order.</summary>
        private static DataNode Reorder(DataNode node, out int moved)
        {
            var count = 0;
            var result = Walk(node, ref count);
            moved = count;
            return result;

            static DataNode Walk(DataNode node, ref int moved)
            {
                switch (node)
                {
                    case MappingDataNode mapping:
                    {
                        var keys = mapping.Keys.ToList();
                        var ordered = keys.OrderBy(key => key, JsonbKeyOrder.Instance).ToList();
                        if (!keys.SequenceEqual(ordered, StringComparer.Ordinal))
                            moved++;

                        var copy = new MappingDataNode(mapping.Count) { Tag = mapping.Tag };
                        foreach (var key in ordered)
                            copy.Add(key, Walk(mapping[key], ref moved));

                        return copy;
                    }

                    case SequenceDataNode sequence:
                    {
                        var copy = new SequenceDataNode(sequence.Count) { Tag = sequence.Tag };
                        foreach (var item in sequence)
                            copy.Add(Walk(item, ref moved));

                        return copy;
                    }

                    default:
                        return node.Copy();
                }
            }
        }

        private static void Write(IResourceManager resMan, ResPath path, DataNode node)
        {
            resMan.UserData.CreateDir(path.Directory);
            using var stream = resMan.UserData.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            node.Write(writer);
        }
    }
}
