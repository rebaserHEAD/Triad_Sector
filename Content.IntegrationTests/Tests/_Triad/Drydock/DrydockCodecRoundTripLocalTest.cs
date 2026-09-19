#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Codec;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using ForceSay = Content.Shared.Damage.ForceSay.DamageForceSayComponent;
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
    /// <para>Per component of every entity on every vessel file, five steps. Write it through
    /// <see cref="DrydockCodec"/>; encode the node tree to JSON and decode it back, which is what a row
    /// is; read the decoded tree back to a component; copy that into what the entity's prototype would
    /// add, as the load does; write the read component again. The questions are whether the write
    /// throws at all, whether the JSON round trip changes the tree, whether the read and then the copy
    /// hold what the live component held, whether the copy throws, and whether the second write equals
    /// the first. That last one is the assertion the design page calls idempotence, and it is the one
    /// that catches a field that writes but does not read back, which a single write cannot see.</para>
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
    /// ship that has been lived in reaches states no file contains, and some failures need one: the
    /// engine copies a list element by element, so a copy that throws per element is only met by a
    /// list that has one, and no shuttle file queues a lathe (the ladder's lived-in pass does). And a component excluded from
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
            Assert.That(FirstRead.Compared, Is.GreaterThan(0),
                "The control: the live comparison compared no scalar, so a clean result from it would mean nothing.");
            Assert.That(report.Copies, Is.GreaterThan(0),
                "The control: no component was copied the load's way, so a copy that never threw would mean nothing.");
            Assert.That(Copied.Compared, Is.GreaterThan(0),
                "The control: the copy's live comparison compared no scalar.");
            Assert.That(ReadNulls.Compared, Is.GreaterThan(0),
                "The control: F37's count compared no object-valued member, so a zero from it would mean nothing.");
            Assert.That(CopyNulls.Compared, Is.GreaterThan(0),
                "The control: F37's count compared no object-valued member on the copy.");
            Assert.That(report.Keys, Is.GreaterThan(0),
                "The control: every component compared wrote an empty mapping, so a clean corpus here would mean the codec wrote nothing rather than that it wrote everything.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The control for F37's count, and one real case of F37. A force-say component's damage groups nulled at runtime
        /// is a live null over a non-null default (the member is nullable and initialised to a set). A member declared
        /// non-nullable is no case of F37: a null there makes the write throw instead (a tag set, NullNotAllowedException).
        /// Counted against a fresh component, the count has to see it,
        /// or the corpus's zero means nothing. Sent through the codec's write and read, and through the load's copy onto a
        /// fresh component, it answers the finding itself for this one member, printed rather than asserted.
        /// </summary>
        [Test]
        public async Task F37TheCountSeesARuntimeNullOverANonNullDefault()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var factory = server.ResolveDependency<IComponentFactory>();
            var serialization = server.ResolveDependency<ISerializationManager>();

            var control = new NullTally();
            var read = new NullTally();
            var copied = new NullTally();
            await server.WaitPost(() =>
            {
                var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                var live = entMan.EnsureComponent<ForceSay>(uid);
                typeof(ForceSay).GetField(nameof(ForceSay.ValidDamageGroups))!.SetValue(live, null);

                var fresh = factory.GetComponent<ForceSay>();
                CountNullness(live, fresh, nameof(ForceSay), control);

                var codec = new DrydockCodec(serialization, entMan, server.ResolveDependency<IGameTiming>(), _ => null, _ => EntityUid.Invalid);
                var restored = codec.Read(typeof(ForceSay), codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), live));
                CountNullness(live, restored, nameof(ForceSay), read);

                IComponent target = factory.GetComponent<ForceSay>();
                serialization.CopyTo(restored, ref target, codec.Context, notNullableOverride: true);
                CountNullness(live, target, nameof(ForceSay), copied);
            });

            await TestContext.Out.WriteLineAsync($"[f37-control] counter against a fresh component: {control.NullLost.Values.Sum()} null lost of {control.Compared} compared.");
            await TestContext.Out.WriteLineAsync($"[f37-control] the codec's read: {read.NullLost.Values.Sum()} null lost of {read.Compared} compared.");
            await TestContext.Out.WriteLineAsync($"[f37-control] the load's copy: {copied.NullLost.Values.Sum()} null lost of {copied.Compared} compared.");

            Assert.That(control.NullLost.GetValueOrDefault($"{nameof(ForceSay)}.{nameof(ForceSay.ValidDamageGroups)}"), Is.EqualTo(1),
                "The control: a live null over a non-null default has to be counted, or the corpus's zero means nothing.");

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

            OnImage = ids.Keys.ToHashSet();
            var writeBefore = report.WriteClock.Elapsed;
            var jsonBefore = report.JsonClock.Elapsed;
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

                if (meta.EntityLifeStage < EntityLifeStage.MapInitialized)
                {
                    var proto = meta.EntityPrototype?.ID ?? "(no prototype)";
                    report.PreMapInit[proto] = report.PreMapInit.GetValueOrDefault(proto) + 1;
                }

                foreach (var component in entMan.GetComponents(uid))
                {
                    var type = component.GetType();

                    // The engine's own exclusion, kept: a component whose registration is marked
                    // unsaved is not part of any document and is not part of an image either.
                    if (factory.GetRegistration(type).Unsaved)
                        continue;

                    hullComponents++;

                    var (found, keys) = RoundTrip(serialization, factory, codec, entity, component, type, hull, report);
                    hullKeys += keys;

                    if (keys == 0)
                        hullEmpty++;

                    if (found)
                        hullFindings++;
                }
            }

            report.HullWrite(hull, hullEntities, report.WriteClock.Elapsed - writeBefore, report.JsonClock.Elapsed - jsonBefore);
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
            ISerializationManager serialization,
            IComponentFactory factory,
            DrydockCodec codec,
            Entity<MetaDataComponent> entity,
            IComponent component,
            Type type,
            string hull,
            Report report)
        {
            MappingDataNode first;
            report.WriteClock.Start();
            try
            {
                first = codec.Write(entity, component);
            }
            catch (Exception e)
            {
                report.Add("write threw", hull, type, Reason(e));
                return (true, 0);
            }
            finally
            {
                report.WriteClock.Stop();
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
                report.JsonClock.Start();
                DataNode transported;
                try
                {
                    transported = DrydockNodeJson.Decode(DrydockNodeJson.Encode(first));
                }
                finally
                {
                    report.JsonClock.Stop();
                }

                if (transported is not MappingDataNode mapping)
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
            report.ReadClock.Start();
            try
            {
                restored = codec.Read(type, decoded);
            }
            catch (Exception e)
            {
                report.Add("read threw", hull, type, Reason(e));
                return (true, keys);
            }
            finally
            {
                report.ReadClock.Stop();
            }

            // Against the live component, not against another write: a value the first write loses writes the
            // same again, so a comparison of writes cannot see it (F35's airtight 15 wrote as 14 and 14 again).
            // Nothing runs between the write and this read, so the clock has not moved and a time reads back
            // at the same moment.
            var nullLostByRead = CountNullness(component, restored, type.Name, ReadNulls);
            CountTimeSentinels(component, type);

            var lostByWrite = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (member, detail) in LiveDifferences(component, restored, type.Name, FirstRead))
            {
                report.Add("the first write lost it", hull, type, detail);
                report.LostMember(member);
                lostByWrite.Add(member);
                found = true;
            }

            if (Copy(serialization, factory, codec, entity, restored, type, hull, report) is { } target)
            {
                CountNullness(component, target, type.Name, CopyNulls, beyond: nullLostByRead);

                // Against the live component again, with what the write already lost left out: a member lost there
                // reads the same wrong value into the copy, and it is one loss, not two.
                foreach (var (member, detail) in LiveDifferences(component, target, type.Name, Copied))
                {
                    if (lostByWrite.Contains(member))
                        continue;

                    report.Add("the copy lost it", hull, type, detail);
                    report.LostByCopy(member);
                    found = true;
                }
            }
            else
            {
                found = true;
            }

            MappingDataNode second;
            report.RewriteClock.Start();
            try
            {
                second = codec.Write(entity, restored);
            }
            catch (Exception e)
            {
                report.Add("second write threw", hull, type, Reason(e));
                return (true, keys);
            }
            finally
            {
                report.RewriteClock.Stop();
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
        /// The load's own copy (<c>DrydockFidelityLadderLocalTest.CodecLoad</c>): the read component copied under the
        /// codec's context into what the entity's prototype would add, or into a fresh one when the prototype has none,
        /// which is the loader's path for a hooked component it adds. The read never copies, so a half of the pipeline
        /// only the load runs had nothing measuring it until a ladder rung happened to carry the case (the lathe
        /// queue, 2026-09-18). Null when the copy threw, reported with the member it threw on.
        /// </summary>
        private static IComponent? Copy(
            ISerializationManager serialization,
            IComponentFactory factory,
            DrydockCodec codec,
            Entity<MetaDataComponent> entity,
            IComponent restored,
            Type type,
            string hull,
            Report report)
        {
            var registration = factory.GetRegistration(type);
            var target = factory.GetComponent(registration);

            if (entity.Comp.EntityPrototype?.Components.TryGetValue(registration.Name, out var prototype) == true)
            {
                // What the entity manager does with a prototype's component, which is the engine's business: a throw
                // here is reported apart, so it is not taken for ours.
                try
                {
                    serialization.CopyTo(prototype.Component, ref target, notNullableOverride: true);
                }
                catch (Exception e)
                {
                    report.Add("the prototype's own copy threw", hull, type, Reason(e));
                    return null;
                }
            }

            report.CopyClock.Start();
            try
            {
                serialization.CopyTo(restored, ref target, codec.Context, notNullableOverride: true);
                report.Copies++;
                return target;
            }
            catch (Exception e)
            {
                report.Add("copy threw", hull, type, $"{CopyCulprit(serialization, factory, registration, restored, codec)}: {Reason(e)}");
                return null;
            }
            finally
            {
                report.CopyClock.Stop();
            }
        }

        /// <summary>
        /// Which top-level data field a failed copy threw on, found by copying each one alone: a fresh component with
        /// only that member taken from the read one, copied the same way. Runs only after a throw, so its cost is the
        /// failure's.
        /// </summary>
        private static string CopyCulprit(
            ISerializationManager serialization,
            IComponentFactory factory,
            ComponentRegistration registration,
            IComponent restored,
            DrydockCodec codec)
        {
            foreach (var member in DataFields(registration.Type))
            {
                object source = factory.GetComponent(registration);
                switch (member)
                {
                    case FieldInfo field:
                        field.SetValue(source, field.GetValue(restored));
                        break;
                    case PropertyInfo { CanWrite: true } property:
                        property.SetValue(source, property.GetValue(restored));
                        break;
                    default:
                        continue;
                }

                object? probe = factory.GetComponent(registration);
                try
                {
                    serialization.CopyTo(source, ref probe, codec.Context, notNullableOverride: true);
                }
                catch (Exception)
                {
                    return $"{registration.Type.Name}.{member.Name}";
                }
            }

            return $"{registration.Type.Name}, no single member alone";
        }

        private static IEnumerable<MemberInfo> DataFields(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var declaring = type; declaring != null && declaring != typeof(object); declaring = declaring.BaseType)
            {
                foreach (var member in declaring.GetFields(flags).Cast<MemberInfo>().Concat(declaring.GetProperties(flags)))
                {
                    if (member.GetCustomAttribute<Robust.Shared.Serialization.Manager.Attributes.DataFieldBaseAttribute>() != null)
                        yield return member;
                }
            }
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

        /// <summary>
        /// The scalar data-field members of a component, and of each data definition one level below it, that the
        /// restored component holds differently from the live one. Scalars only, because equality means what it
        /// says for them; a collection or a definition deeper down is left to the detector.
        /// </summary>
        private static IEnumerable<(string Member, string Detail)> LiveDifferences(object live, object restored, string path, LiveTally tally, int depth = 0)
        {
            foreach (var member in ScalarAndNested(live.GetType()))
            {
                var before = Get(member, live);
                var after = Get(member, restored);
                var name = $"{path}.{member.Name}";

                if (IsScalar(MemberType(member)))
                {
                    tally.Compared++;

                    // A reference to an entity off the image reads back invalid by rule (F32, carve-out e): the grid's
                    // own parent, its map, on every hull. Severed, counted apart, not lost.
                    if (before is EntityUid target && target.IsValid() && !OnImage.Contains(target)
                        && after is EntityUid restoredTarget && !restoredTarget.IsValid())
                    {
                        tally.Severed++;
                        continue;
                    }

                    if (!SameScalar(before, after))
                        yield return (name, $"{name}: {Cut($"{before}")} -> {Cut($"{after}")}");

                    continue;
                }

                if (depth == 0 && before != null && after != null && before.GetType() == after.GetType())
                {
                    foreach (var nested in LiveDifferences(before, after, name, tally, depth + 1))
                        yield return nested;
                }
            }
        }

        /// <summary>
        /// F37's count, taken before any fix: the object-valued data-field members (collections, definitions, any
        /// reference) whose nullness differs from the live component's, top level and one definition down. A live null
        /// that comes back non-null is the case F37 names, a runtime null over a non-null default; the other direction is
        /// counted beside it. Nullness only, because equality means nothing reliable for these types; a counted member
        /// adds nothing to the findings. Returns the members the comparison found null-lost, so the copy can count what it
        /// adds beyond the read.
        /// </summary>
        private static HashSet<string> CountNullness(object live, object other, string path, NullTally tally, HashSet<string>? beyond = null, int depth = 0)
        {
            var lost = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in ObjectValued(live.GetType()))
            {
                var before = Get(member, live);
                var after = Get(member, other);
                var name = $"{path}.{member.Name}";
                tally.Compared++;

                if (before == null && after != null)
                {
                    lost.Add(name);
                    tally.NullLost[name] = tally.NullLost.GetValueOrDefault(name) + 1;
                    if (beyond != null && !beyond.Contains(name))
                        tally.BeyondRead[name] = tally.BeyondRead.GetValueOrDefault(name) + 1;
                }
                else if (before != null && after == null)
                {
                    tally.NullGained[name] = tally.NullGained.GetValueOrDefault(name) + 1;
                }
                else if (depth == 0 && before != null && after != null && before.GetType() == after.GetType()
                         && before is Robust.Shared.Serialization.ISerializationGenerated)
                {
                    lost.UnionWith(CountNullness(before, after, name, tally, beyond, depth + 1));
                }
            }

            return lost;
        }

        /// <summary>
        /// The size of what the codec rebased before the sentinel tokens: the time-offset data fields holding exactly zero,
        /// the maximum or the minimum at store, which the codec used to write as a distance from the clock and so turned
        /// from "never" into a deadline. Top-level fields only. A count, not a finding.
        /// </summary>
        private static void CountTimeSentinels(IComponent component, Type type)
        {
            if (!TimeOffsetMembers.TryGetValue(type, out var members))
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                members = new List<MemberInfo>();
                for (var declaring = type; declaring != null && declaring != typeof(object); declaring = declaring.BaseType)
                {
                    foreach (var member in declaring.GetFields(flags).Cast<MemberInfo>().Concat(declaring.GetProperties(flags)))
                    {
                        if (member.GetCustomAttribute<Robust.Shared.Serialization.Manager.Attributes.DataFieldBaseAttribute>()?.CustomTypeSerializer
                            == typeof(Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.TimeOffsetSerializer))
                        {
                            members.Add(member);
                        }
                    }
                }

                TimeOffsetMembers[type] = members;
            }

            foreach (var member in members)
            {
                TimeOffsetValues++;
                var sentinel = Get(member, component) switch
                {
                    TimeSpan span when span == TimeSpan.Zero => "zero",
                    TimeSpan span when span == TimeSpan.MaxValue => "max",
                    TimeSpan span when span == TimeSpan.MinValue => "min",
                    _ => null,
                };

                if (sentinel != null)
                {
                    var key = $"{type.Name}.{member.Name} ({sentinel})";
                    TimeSentinels[key] = TimeSentinels.GetValueOrDefault(key) + 1;
                }
            }
        }

        private static readonly Dictionary<Type, List<MemberInfo>> TimeOffsetMembers = new();

        private static readonly Dictionary<string, int> TimeSentinels = new(StringComparer.Ordinal);

        private static long TimeOffsetValues;

        /// <summary>F37's count over one side of the comparison.</summary>
        private sealed class NullTally
        {
            public long Compared;
            public readonly Dictionary<string, int> NullLost = new(StringComparer.Ordinal);
            public readonly Dictionary<string, int> NullGained = new(StringComparer.Ordinal);
            public readonly Dictionary<string, int> BeyondRead = new(StringComparer.Ordinal);
        }

        private static readonly NullTally ReadNulls = new();

        private static readonly NullTally CopyNulls = new();

        private static readonly Dictionary<Type, List<MemberInfo>> ObjectValuedCache = new();

        /// <summary>The data-field members that can hold null and are not scalars: what the scalar comparison leaves out.</summary>
        private static List<MemberInfo> ObjectValued(Type type)
        {
            if (ObjectValuedCache.TryGetValue(type, out var cached))
                return cached;

            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                                                         | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            var members = new List<MemberInfo>();
            for (var declaring = type; declaring != null && declaring != typeof(object); declaring = declaring.BaseType)
            {
                foreach (var member in declaring.GetFields(flags).Cast<MemberInfo>().Concat(declaring.GetProperties(flags)))
                {
                    if (member.GetCustomAttribute<Robust.Shared.Serialization.Manager.Attributes.DataFieldBaseAttribute>() == null)
                        continue;

                    var memberType = MemberType(member);
                    if (IsScalar(memberType) || memberType.IsValueType && Nullable.GetUnderlyingType(memberType) == null)
                        continue;

                    members.Add(member);
                }
            }

            ObjectValuedCache[type] = members;
            return members;
        }

        private static readonly Dictionary<Type, List<MemberInfo>> MemberCache = new();

        /// <summary>A live comparison's own coverage: a clean result over no comparisons would read as clean.</summary>
        private sealed class LiveTally
        {
            public long Compared;
            public long Severed;
        }

        /// <summary>The read component against the live one.</summary>
        private static readonly LiveTally FirstRead = new();

        /// <summary>The load's copy target against the live one.</summary>
        private static readonly LiveTally Copied = new();

        /// <summary>The entities on the hull being measured, so a reference off it is told from a lost one.</summary>
        private static HashSet<EntityUid> OnImage = new();

        private static List<MemberInfo> ScalarAndNested(Type type)
        {
            if (MemberCache.TryGetValue(type, out var cached))
                return cached;

            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                                                         | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            var members = new List<MemberInfo>();
            for (var declaring = type; declaring != null && declaring != typeof(object); declaring = declaring.BaseType)
            {
                foreach (var member in declaring.GetFields(flags).Cast<MemberInfo>().Concat(declaring.GetProperties(flags)))
                {
                    if (member.GetCustomAttribute<Robust.Shared.Serialization.Manager.Attributes.DataFieldBaseAttribute>() == null)
                        continue;

                    var memberType = MemberType(member);
                    if (IsScalar(memberType) || typeof(Robust.Shared.Serialization.ISerializationGenerated).IsAssignableFrom(memberType))
                        members.Add(member);
                }
            }

            MemberCache[type] = members;
            return members;
        }

        private static Type MemberType(MemberInfo member) =>
            member is FieldInfo field ? field.FieldType : ((PropertyInfo) member).PropertyType;

        private static object? Get(MemberInfo member, object target) =>
            member is FieldInfo field ? field.GetValue(target) : ((PropertyInfo) member).GetValue(target);

        private static bool IsScalar(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
                   || type == typeof(TimeSpan) || type == typeof(Content.Shared.FixedPoint.FixedPoint2)
                   || type == typeof(System.Numerics.Vector2) || type == typeof(Robust.Shared.Maths.Angle)
                   || type == typeof(Robust.Shared.Maths.Color) || type == typeof(EntityUid) || type == typeof(NetEntity);
        }

        /// <summary>
        /// A time is written as seconds with a fraction, so it is compared to the millisecond. A colour is written at
        /// 8 bits a channel, and that loss is accepted precision (ruled 2026-09-18), so it is compared to 1/255.
        /// </summary>
        private static bool SameScalar(object? before, object? after) => (before, after) switch
        {
            (TimeSpan a, TimeSpan b) => Math.Abs((a - b).TotalMilliseconds) < 1,
            (Robust.Shared.Maths.Color a, Robust.Shared.Maths.Color b) =>
                Math.Abs(a.R - b.R) <= 1f / 255 && Math.Abs(a.G - b.G) <= 1f / 255
                && Math.Abs(a.B - b.B) <= 1f / 255 && Math.Abs(a.A - b.A) <= 1f / 255,
            _ => Equals(before, after),
        };

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

            /// <summary>
            /// Entities met before their map init, by prototype. The store's owed rule is to refuse a hull holding one,
            /// because an entity whose init has not run yet is not the entity the image describes, and the loader stamps
            /// map-init on everything it makes. A hull from its file has none: the walk runs after the map loader's init.
            /// </summary>
            public readonly Dictionary<string, int> PreMapInit = new(StringComparer.Ordinal);

            // The four codec steps alone, apart from loading hulls, walking them and comparing trees,
            // because the wall time moves with whatever else the machine is doing and says nothing
            // about the codec. The third leg runs only on drift and is left out.
            public readonly System.Diagnostics.Stopwatch WriteClock = new();
            public readonly System.Diagnostics.Stopwatch JsonClock = new();
            public readonly System.Diagnostics.Stopwatch ReadClock = new();
            public readonly System.Diagnostics.Stopwatch RewriteClock = new();
            public readonly System.Diagnostics.Stopwatch CopyClock = new();

            /// <summary>Components the load's copy was made for and did not throw on.</summary>
            public int Copies;

            public readonly List<string> Unloadable = new();

            // The write of one whole hull, the number the no-bystander-tick rule turns on: a store writes on the main
            // thread, so a multi-second hull means slicing the write.
            private readonly List<(string Hull, int Entities, TimeSpan Write, TimeSpan Json)> _hullWrites = new();
            private readonly Dictionary<string, int> _lostMembers = new(StringComparer.Ordinal);

            public void HullWrite(string hull, int entities, TimeSpan write, TimeSpan json) =>
                _hullWrites.Add((hull, entities, write, json));

            public void LostMember(string member) =>
                _lostMembers[member] = _lostMembers.GetValueOrDefault(member) + 1;

            private readonly Dictionary<string, int> _lostByCopy = new(StringComparer.Ordinal);

            public void LostByCopy(string member) =>
                _lostByCopy[member] = _lostByCopy.GetValueOrDefault(member) + 1;

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

                var codec = WriteClock.Elapsed + JsonClock.Elapsed + ReadClock.Elapsed + CopyClock.Elapsed + RewriteClock.Elapsed;
                await TestContext.Out.WriteLineAsync(
                    $"[codec-roundtrip] codec steps {codec.TotalSeconds:F1}s of the {elapsed.TotalSeconds:F1}s: "
                    + $"write {WriteClock.Elapsed.TotalSeconds:F1}s, json {JsonClock.Elapsed.TotalSeconds:F1}s, "
                    + $"read {ReadClock.Elapsed.TotalSeconds:F1}s, copy {CopyClock.Elapsed.TotalSeconds:F1}s, "
                    + $"second write {RewriteClock.Elapsed.TotalSeconds:F1}s; "
                    + "the third leg, the prototype copies, loading, walking and comparing are the rest.");

                foreach (var (hull, entities, write, json) in _hullWrites.OrderByDescending(h => h.Write).Take(5))
                {
                    await TestContext.Out.WriteLineAsync(
                        $"[codec-roundtrip] hull write, slowest first: {write.TotalMilliseconds:F0} ms to write {entities} entit(y/ies), "
                        + $"{json.TotalMilliseconds:F0} ms more to JSON, {hull}");
                }

                await TestContext.Out.WriteLineAsync(
                    $"[codec-roundtrip] live comparison: {FirstRead.Compared} scalar value(s) compared against the live component, "
                    + $"{_lostMembers.Values.Sum()} lost by the first write, {FirstRead.Severed} reference(s) off the image severed by rule.");

                if (_lostMembers.Count > 0)
                {
                    await TestContext.Out.WriteLineAsync($"[codec-roundtrip] the first write lost it, by member ({_lostMembers.Count}):");
                    foreach (var (member, count) in _lostMembers.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                        await TestContext.Out.WriteLineAsync($"[codec-roundtrip]     {member} x{count}");
                }

                await TestContext.Out.WriteLineAsync(
                    $"[codec-roundtrip] the load's copy: {Copies} component(s) copied, {_byKind.GetValueOrDefault("copy threw")} threw; "
                    + $"{Copied.Compared} scalar value(s) on the copy target compared against the live component, "
                    + $"{_lostByCopy.Values.Sum()} lost by the copy beyond what the write lost, {Copied.Severed} severed by rule.");

                if (_lostByCopy.Count > 0)
                {
                    await TestContext.Out.WriteLineAsync($"[codec-roundtrip] the copy lost it, by member ({_lostByCopy.Count}):");
                    foreach (var (member, count) in _lostByCopy.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                        await TestContext.Out.WriteLineAsync($"[codec-roundtrip]     {member} x{count}");
                }

                foreach (var (side, tally) in new[] { ("read", ReadNulls), ("copy", CopyNulls) })
                {
                    await TestContext.Out.WriteLineAsync(
                        $"[codec-roundtrip] F37 nullness, {side}: {tally.Compared} object-valued member(s) compared over {Hulls} hull(s); "
                        + $"{tally.NullLost.Values.Sum()} live null came back non-null across {tally.NullLost.Count} member(s), "
                        + $"{tally.NullGained.Values.Sum()} live non-null came back null across {tally.NullGained.Count} member(s)"
                        + (side == "copy" ? $"; {tally.BeyondRead.Values.Sum()} null-lost by the copy that the read kept null." : "."));

                    foreach (var (label, members) in new[] { ("null lost", tally.NullLost), ("null gained", tally.NullGained), ("null lost beyond the read", tally.BeyondRead) })
                    {
                        if (members.Count == 0 || side == "read" && label == "null lost beyond the read")
                            continue;

                        await TestContext.Out.WriteLineAsync($"[codec-roundtrip] F37 {side}, {label}, by member ({members.Count}):");
                        foreach (var (member, count) in members.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                            await TestContext.Out.WriteLineAsync($"[codec-roundtrip]     {member} x{count}");
                    }
                }

                await TestContext.Out.WriteLineAsync(
                    $"[codec-roundtrip] entities met before their map init: {PreMapInit.Values.Sum()} of {Entities} over {Hulls} hull(s)"
                    + (PreMapInit.Count == 0
                        ? ", as a hull from its file should have; the store's owed rule is to refuse a hull holding one."
                        : ": " + string.Join(", ", PreMapInit.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} x{kv.Value}"))
                          + ". The store's owed rule is to refuse a hull holding one."));

                await TestContext.Out.WriteLineAsync(
                    $"[codec-roundtrip] time sentinels: {TimeSentinels.Values.Sum()} of {TimeOffsetValues} time-offset value(s) over {Hulls} hull(s) held exactly "
                    + $"zero, the maximum or the minimum at store, across {TimeSentinels.Count} member(s); before the sentinel tokens each was written as a distance from the clock.");
                foreach (var (member, count) in TimeSentinels.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                    await TestContext.Out.WriteLineAsync($"[codec-roundtrip]     {member} x{count}");

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
