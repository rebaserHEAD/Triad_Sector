#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Codec;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The codec against every hull the shipyard sells, and the earliest honest signal about whether
    /// the grid image holds water: it answers the codec's half of the question before the image
    /// writer or the loader exist.
    ///
    /// <para>Per component of every entity on every vessel file, four steps and three questions.
    /// Write it through <see cref="DrydockCodec"/>; encode the node tree to JSON and decode it back,
    /// which is what a row is; read the decoded tree back to a component; write that component
    /// again. The questions are whether the write throws at all, whether the JSON round trip changes
    /// the tree, and whether the second write equals the first. That last one is the assertion the
    /// design page calls idempotence, and it is the one that catches a field that writes but does
    /// not read back, which a single write cannot see.</para>
    ///
    /// <para>This is a measurement, not a gate, and it is reported as one: nothing here asserts a
    /// clean corpus, because the point is to find out. What it does assert is its own coverage, so a
    /// run that compared nothing cannot read as a run that found nothing. Every hull prints what it
    /// carried whether or not it had anything to report, and a failure never stops the walk: one row
    /// per hull per failing component, collected and counted at the end, because a harness that dies
    /// on hull one tells you about hull one.</para>
    ///
    /// <para>What it does not cover. It compares what the codec wrote against what the codec read,
    /// so it says nothing about whether a restored component behaves: a startup handler, an election
    /// in query order and the power solver's first tick are the loader's business and no round trip
    /// reaches them. It runs on hulls as their files describe them, which is one shape of grid; a
    /// ship that has been lived in reaches states no file contains. And a component excluded from
    /// the walk below is unmeasured rather than clean.</para>
    ///
    /// <para>Run: <c>dotnet test Content.IntegrationTests --no-build --filter "FullyQualifiedName~DrydockCodecRoundTripLocalTest" --logger "console;verbosity=detailed"</c>.</para>
    /// </summary>
    [TestFixture]
    [Explicit("Codec measurement over the whole shuttle corpus. Run deliberately and read its report.")]
    [TestOf(typeof(DrydockCodec))]
    public sealed class DrydockCodecRoundTripLocalTest
    {
        /// <summary>How many example rows each kind of finding prints before it is only counted.</summary>
        private const int Examples = 5;

        [Test]
        public async Task TheCodecRoundTripsTheShuttleCorpus()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var factory = server.ResolveDependency<IComponentFactory>();
            var serialization = server.ResolveDependency<ISerializationManager>();
            var timing = server.ResolveDependency<IGameTiming>();
            var mapLoader = server.System<MapLoaderSystem>();

            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Select(vessel => vessel.ShuttlePath)
                .Distinct()
                .OrderBy(path => path.ToString(), StringComparer.Ordinal)
                .ToList();

            Assert.That(vessels, Is.Not.Empty, "The control: no vessel prototype named a shuttle file.");

            var map = await pair.CreateTestMap();
            var report = new Report();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            foreach (var path in vessels)
            {
                await server.WaitPost(() =>
                {
                    if (!mapLoader.TryLoadGrid(map.MapId, path, out var loaded))
                    {
                        report.Unloadable.Add(path.ToString());
                        return;
                    }

                    var grid = loaded.Value.Owner;

                    try
                    {
                        Measure(entMan, factory, serialization, timing, grid, path.ToString(), report);
                    }
                    finally
                    {
                        entMan.DeleteEntity(grid);
                    }
                });

                await pair.RunTicksSync(2);
            }

            clock.Stop();
            await report.Write(clock.Elapsed, vessels.Count);

            // The coverage controls. None is a verdict on the codec: they are what makes the numbers
            // above mean anything, because an empty walk reports a clean corpus and so does a codec
            // that wrote an empty mapping for every component it was handed.
            Assert.That(report.Hulls, Is.GreaterThan(0), "The control: no hull was loaded, so nothing was measured.");
            Assert.That(report.Components, Is.GreaterThan(0), "The control: no component was compared.");
            Assert.That(report.Entities, Is.GreaterThan(report.Hulls), "The control: hulls carried no entities beyond themselves.");
            Assert.That(report.Keys, Is.GreaterThan(0),
                "The control: every component compared wrote an empty mapping, so a clean corpus here would mean the codec wrote nothing rather than that it wrote everything.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// One hull, walked as a tree from the grid rather than queried from the world, because a
        /// grid's children are its ancestry and a world query would take in the map and every other
        /// hull on it.
        /// </summary>
        private static void Measure(
            IEntityManager entMan,
            IComponentFactory factory,
            ISerializationManager serialization,
            IGameTiming timing,
            EntityUid grid,
            string hull,
            Report report)
        {
            var aboard = new List<EntityUid>();
            var stack = new Stack<EntityUid>();
            stack.Push(grid);

            while (stack.TryPop(out var uid))
            {
                aboard.Add(uid);

                var children = entMan.GetComponent<TransformComponent>(uid).ChildEnumerator;
                while (children.MoveNext(out var child))
                    stack.Push(child);
            }

            // The image's own reference rule: an entity aboard gets a stable id, and anything else is
            // off the image and writes as invalid. Handing out ids for the world instead would make
            // every reference resolvable and measure a hull the store will never write.
            var ids = new Dictionary<EntityUid, long>();
            var entities = new Dictionary<long, EntityUid>();
            for (var i = 0; i < aboard.Count; i++)
            {
                ids[aboard[i]] = i + 1;
                entities[i + 1] = aboard[i];
            }

            var codec = new DrydockCodec(
                serialization,
                entMan,
                timing,
                uid => ids.TryGetValue(uid, out var id) ? id : null,
                id => entities.TryGetValue(id, out var uid) ? uid : EntityUid.Invalid);

            var hullEntities = 0;
            var hullComponents = 0;
            var hullKeys = 0;
            var hullEmpty = 0;
            var hullFindings = 0;

            foreach (var uid in aboard)
            {
                if (!entMan.TryGetComponent<MetaDataComponent>(uid, out var meta))
                    continue;

                hullEntities++;
                var entity = new Entity<MetaDataComponent>(uid, meta);

                foreach (var component in entMan.GetComponents(uid))
                {
                    var type = component.GetType();

                    // The engine's own exclusion, kept: a component whose registration is marked
                    // unsaved is not part of any document and is not part of an image either.
                    if (factory.GetRegistration(type).Unsaved)
                        continue;

                    hullComponents++;

                    var (found, keys) = RoundTrip(codec, entity, component, type, hull, report);
                    hullKeys += keys;

                    if (keys == 0)
                        hullEmpty++;

                    if (found)
                        hullFindings++;
                }
            }

            report.Hulls++;
            report.Entities += hullEntities;
            report.Components += hullComponents;
            report.Keys += hullKeys;
            report.Hull(hull, hullEntities, hullComponents, hullKeys, hullEmpty, hullFindings);
        }

        /// <summary>
        /// One component through the four steps. Reports whether it had a finding, and how many keys
        /// the write carried at any depth, because a comparison that compared nothing proves nothing
        /// and a codec that wrote an empty mapping for every component would round-trip perfectly.
        /// </summary>
        private static (bool Found, int Keys) RoundTrip(
            DrydockCodec codec,
            Entity<MetaDataComponent> entity,
            IComponent component,
            Type type,
            string hull,
            Report report)
        {
            MappingDataNode first;
            try
            {
                first = codec.Write(entity, component);
            }
            catch (Exception e)
            {
                report.Add("write threw", hull, type, Reason(e));
                return (true, 0);
            }

            // Counted at any depth, and an empty write is its own reported kind rather than a
            // number hidden inside the compared count. Some components are legitimately empty; the
            // point is that the figure is on the page, because a codec that wrote nothing at all
            // would round-trip perfectly and report a clean corpus.
            var keys = CountKeys(first);
            if (keys == 0)
                report.Add("wrote no keys", hull, type, "the component wrote an empty mapping");

            MappingDataNode decoded;
            try
            {
                if (DrydockNodeJson.Decode(DrydockNodeJson.Encode(first)) is not MappingDataNode mapping)
                {
                    report.Add("json changed the kind", hull, type, "a component mapping decoded as something else");
                    return (true, keys);
                }

                decoded = mapping;
            }
            catch (Exception e)
            {
                report.Add("json threw", hull, type, Reason(e));
                return (true, keys);
            }

            var found = false;
            if (Difference(first, decoded, string.Empty) is { } changed)
            {
                report.Add("json changed the tree", hull, type, changed);
                found = true;
            }

            IComponent restored;
            try
            {
                restored = codec.Read(type, decoded);
            }
            catch (Exception e)
            {
                report.Add("read threw", hull, type, Reason(e));
                return (true, keys);
            }

            MappingDataNode second;
            try
            {
                second = codec.Write(entity, restored);
            }
            catch (Exception e)
            {
                report.Add("second write threw", hull, type, Reason(e));
                return (true, keys);
            }

            if (Difference(first, second, string.Empty) is { } drift)
            {
                // A third leg, and only when the second disagreed with the first, because the two
                // answers are worth different amounts. A value that settles after one pass has lost
                // something once, at a bounded size; one that keeps moving loses it again at every
                // store, which is the shape that ends with a ship nobody recognises.
                // The verdict is the kind rather than the detail, because the detail is only kept for
                // the first few examples and the question here is how many of all of them settle.
                string kind;
                var detail = drift;
                try
                {
                    var third = codec.Write(entity, codec.Read(type, second));
                    if (Difference(second, third, string.Empty) is { } again)
                    {
                        kind = "not idempotent, still moving";
                        detail = $"{drift}, then {again}";
                    }
                    else
                    {
                        kind = "not idempotent, settles on the next pass";
                    }
                }
                catch (Exception e)
                {
                    kind = "not idempotent, and the next pass threw";
                    detail = $"{drift}, then {Reason(e)}";
                }

                report.Add(kind, hull, type, detail);
                found = true;
            }

            return (found, keys);
        }

        /// <summary>
        /// Where two node trees first differ, or null when they do not. Order-insensitive at the
        /// mapping level and ordered within a sequence, which is the comparison the schema rules
        /// call for: jsonb sorts an object's keys, and a list field's order is its content.
        /// </summary>
        private static string? Difference(DataNode left, DataNode right, string path)
        {
            if (left.Tag != right.Tag)
                return $"{path} tag '{left.Tag}' became '{right.Tag}'";

            switch (left, right)
            {
                case (ValueDataNode a, ValueDataNode b):
                    if (a.IsNull != b.IsNull)
                        return $"{path} null {a.IsNull} became {b.IsNull}";

                    return a.Value == b.Value ? null : $"{path} '{Cut(a.Value)}' became '{Cut(b.Value)}'";

                case (SequenceDataNode a, SequenceDataNode b):
                    if (a.Count != b.Count)
                        return $"{path} held {a.Count} element(s) and holds {b.Count}";

                    for (var i = 0; i < a.Count; i++)
                    {
                        if (Difference(a[i], b[i], $"{path}[{i}]") is { } inner)
                            return inner;
                    }

                    return null;

                case (MappingDataNode a, MappingDataNode b):
                    if (a.Count != b.Count)
                    {
                        var lost = a.Keys.Where(key => !b.Has(key)).Order(StringComparer.Ordinal).Take(3).ToList();
                        var gained = b.Keys.Where(key => !a.Has(key)).Order(StringComparer.Ordinal).Take(3).ToList();
                        return $"{path} held {a.Count} key(s) and holds {b.Count}"
                               + (lost.Count > 0 ? $", lost {string.Join(", ", lost)}" : string.Empty)
                               + (gained.Count > 0 ? $", gained {string.Join(", ", gained)}" : string.Empty);
                    }

                    foreach (var (key, child) in a)
                    {
                        if (!b.TryGet(key, out var other))
                            return $"{path}.{key} is missing";

                        if (Difference(child, other, $"{path}.{key}") is { } inner)
                            return inner;
                    }

                    return null;

                default:
                    return $"{path} was {left.GetType().Name} and is {right.GetType().Name}";
            }
        }

        /// <summary>
        /// Every key a written tree carries, at any depth. The top-level count alone would call a
        /// component that wrote one key holding an empty mapping "compared".
        /// </summary>
        private static int CountKeys(DataNode node)
        {
            switch (node)
            {
                case MappingDataNode mapping:
                {
                    var keys = mapping.Count;
                    foreach (var (_, child) in mapping)
                        keys += CountKeys(child);

                    return keys;
                }

                case SequenceDataNode sequence:
                {
                    var keys = 0;
                    foreach (var child in sequence)
                        keys += CountKeys(child);

                    return keys;
                }

                default:
                    return 0;
            }
        }

        private static string Reason(Exception e)
        {
            var message = e.Message;
            var newline = message.IndexOf('\n');
            if (newline >= 0)
                message = message[..newline];

            return $"{e.GetType().Name}: {Cut(message)}";
        }

        private static string Cut(string text) =>
            text.Length <= 120 ? text : text[..120] + "...";

        /// <summary>
        /// What the run found, kept as counts with a few examples each rather than as a transcript:
        /// a finding that occurs on every hull is one fact, and printing it 154 times buries the one
        /// that occurs once.
        /// </summary>
        private sealed class Report
        {
            public int Hulls;
            public int Entities;
            public int Components;
            public int Keys;

            public readonly List<string> Unloadable = new();

            private readonly List<string> _hulls = new();
            private readonly Dictionary<string, int> _byKind = new(StringComparer.Ordinal);
            private readonly Dictionary<string, Dictionary<string, int>> _byComponent = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<string>> _examples = new(StringComparer.Ordinal);

            public void Hull(string hull, int entities, int components, int keys, int empty, int findings)
            {
                _hulls.Add($"{hull}: {entities} entit(y/ies), {components} component(s) carrying {keys} key(s), "
                           + $"{empty} that wrote none, {findings} with something to report");
            }

            public void Add(string kind, string hull, Type component, string detail)
            {
                _byKind[kind] = _byKind.GetValueOrDefault(kind) + 1;

                if (!_byComponent.TryGetValue(kind, out var components))
                    _byComponent[kind] = components = new Dictionary<string, int>(StringComparer.Ordinal);

                components[component.Name] = components.GetValueOrDefault(component.Name) + 1;

                if (!_examples.TryGetValue(kind, out var examples))
                    _examples[kind] = examples = new List<string>();

                if (examples.Count < Examples)
                    examples.Add($"{component.Name} on {hull}: {detail}");
            }

            public async Task Write(TimeSpan elapsed, int named)
            {
                var findings = _byKind.Values.Sum();

                await TestContext.Out.WriteLineAsync(
                    $"[codec-roundtrip] {named} vessel file(s) named, {Hulls} loaded, {Unloadable.Count} not; "
                    + $"{Entities} entit(y/ies) and {Components} component(s) carrying {Keys} key(s) compared in {elapsed.TotalSeconds:F1}s; "
                    + $"{findings} finding(s) across {_byKind.Count} kind(s).");

                foreach (var path in Unloadable)
                    await TestContext.Out.WriteLineAsync($"[codec-roundtrip] did not load: {path}");

                foreach (var (kind, count) in _byKind.OrderByDescending(entry => entry.Value))
                {
                    var components = _byComponent[kind]
                        .OrderByDescending(entry => entry.Value)
                        .Take(8)
                        .Select(entry => $"{entry.Key} x{entry.Value}");

                    await TestContext.Out.WriteLineAsync($"[codec-roundtrip] {kind}: {count}, led by {string.Join(", ", components)}");

                    foreach (var example in _examples[kind])
                        await TestContext.Out.WriteLineAsync($"[codec-roundtrip]     {example}");
                }

                // Per hull last and always, including the quiet ones: a clean run has to say what it
                // carried, or it cannot be told from a run that walked nothing.
                foreach (var line in _hulls)
                    await TestContext.Out.WriteLineAsync($"[codec-roundtrip] {line}");
            }
        }
    }
}
