#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock.Codec;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.Serialization;
using Content.Server.StationRecords;
using Content.Shared.Damage;
using Content.Shared.Doors.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.Definition;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// <see cref="DrydockRetiredKeys"/> and the strictness that makes it mean something: every entry is audited against the
    /// live code, the keys a component row may hold are nameable for every registered component, and each of the three
    /// places a retired key sits is dropped and counted when retired and refused when not.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockRetiredKeys))]
    public sealed class DrydockRetiredKeysTest
    {
        private static readonly Regex Commit = new("^[0-9a-f]{7,40}$");

        private static readonly Dictionary<DrydockRetiredKind, Regex> Shapes = new()
        {
            [DrydockRetiredKind.ManifestMember] = new(@"^\w+\.\w+(\[[^\]]+\])?$"),
            [DrydockRetiredKind.Component] = new(@"^\w+$"),
            [DrydockRetiredKind.DataField] = new(@"^\w+\.\S+$"),
        };

        /// <summary>
        /// The audit: no retired manifest key is a live member, no retired component is registered, no retired data field is
        /// a key its component still declares; and every entry is unique, shaped as its kind's key is, has a reason, and
        /// names its commit as 7 to 40 hex characters. The hash is checked for its form only, since the CI checkout is
        /// shallow and has no history to find it in.
        /// </summary>
        [Test]
        public async Task EveryRetirementIsRetired()
        {
            await using var pair = await PoolManager.GetServerClient();
            var factory = pair.Server.ResolveDependency<IComponentFactory>();

            var wrong = Wrong(DrydockRetiredKeys.All, factory);
            await TestContext.Out.WriteLineAsync($"[retired] {DrydockRetiredKeys.All.Length} retirement(s) audited, {wrong.Count} wrong.");
            foreach (var line in wrong)
                await TestContext.Out.WriteLineAsync($"[retired]   {line}");

            Assert.That(wrong, Is.Empty, "Every retirement has to be retired and well formed; the list above names each that is not.");

            await pair.CleanReturnAsync();
        }

        /// <summary>The control: the audit pointed at entries broken one way each, and at good ones that must pass.</summary>
        [Test]
        public async Task TheRetirementAuditCatchesABadList()
        {
            await using var pair = await PoolManager.GetServerClient();
            var factory = pair.Server.ResolveDependency<IComponentFactory>();
            var doorTag = DrydockDeclaredKeys.Of(typeof(DoorComponent)).Order(StringComparer.Ordinal).First();

            var good = new[]
            {
                new DrydockRetiredKey(DrydockRetiredKind.ManifestMember, "DrydockRetiredControl.Member[Key.Value]", "a control", "0123abc"),
                new DrydockRetiredKey(DrydockRetiredKind.Component, "DrydockRetiredControl", "a control", "0123456789abcdef0123456789abcdef01234567"),
                new DrydockRetiredKey(DrydockRetiredKind.DataField, "Door.drydockRetiredControl", "a control", "0123abc"),
            };

            var bad = new Dictionary<string, DrydockRetiredKey>
            {
                ["a live manifest member"] = good[0] with { Key = DrydockCodecManifestMembers.Members[0].Key },
                ["a registered component"] = good[1] with { Key = "Physics" },
                ["a declared data field"] = good[2] with { Key = $"Door.{doorTag}" },
                ["an empty reason"] = good[0] with { Reason = " " },
                ["a commit that is not hex"] = good[0] with { Commit = "0123abz" },
                ["a commit too short"] = good[0] with { Commit = "0123ab" },
                ["a commit too long"] = good[0] with { Commit = new string('a', 41) },
                ["a manifest key with no member"] = good[0] with { Key = "DrydockRetiredControl" },
                ["a component key with a member"] = good[1] with { Key = "DrydockRetiredControl.Member" },
                ["a data field key with no tag"] = good[2] with { Key = "Door" },
            };

            var goodWrong = Wrong(good, factory);
            var duplicate = Wrong(new[] { good[0], good[0] }, factory);
            var missed = bad.Where(entry => Wrong(new[] { entry.Value }, factory).Count == 0).Select(entry => entry.Key).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(goodWrong, Is.Empty, "The control's control: entries that retire nothing live pass the audit.");
                Assert.That(duplicate, Is.Not.Empty, "A key listed twice went unreported.");
                Assert.That(missed, Is.Empty, "The audit let these through: " + string.Join(", ", missed));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Every registered component's row keys can be named, so no component carries an include whose keys only its
        /// serializer knows without a <see cref="DrydockDeclaredKeys.SerializerIncludes"/> entry; and every entry there
        /// names an include that exists and is written by a serializer, so the table cannot outlive what it describes.
        /// </summary>
        [Test]
        public async Task EveryRegisteredComponentsRowKeysCanBeNamed()
        {
            await using var pair = await PoolManager.GetServerClient();
            var factory = pair.Server.ResolveDependency<IComponentFactory>();

            var unnamed = new List<string>();
            var components = 0;
            foreach (var type in factory.AllRegisteredTypes)
            {
                components++;
                try
                {
                    DrydockDeclaredKeys.Of(type);
                }
                catch (InvalidOperationException e)
                {
                    unnamed.Add($"{type.Name}: {e.Message}");
                }
            }

            var stale = new List<string>();
            foreach (var entry in DrydockDeclaredKeys.SerializerIncludes)
            {
                var member = (MemberInfo?) entry.Owner.GetField(entry.Member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? entry.Owner.GetProperty(entry.Member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (member?.GetCustomAttribute<IncludeDataFieldAttribute>() is not { CustomTypeSerializer: not null })
                    stale.Add($"{entry.Owner.Name}.{entry.Member} is not an include written by a serializer");
            }

            await TestContext.Out.WriteLineAsync($"[retired] {components} registered components walked, {unnamed.Count} with keys that cannot be named; "
                                                 + $"{DrydockDeclaredKeys.SerializerIncludes.Length} serializer include(s), {stale.Count} stale.");
            foreach (var line in unnamed.Concat(stale))
                await TestContext.Out.WriteLineAsync($"[retired]   {line}");

            Assert.Multiple(() =>
            {
                Assert.That(components, Is.GreaterThan(100), "The control: the walk has to have walked the registry.");
                Assert.That(unnamed, Is.Empty, "Every registered component's row keys have to be nameable; the list above names each that is not.");
                Assert.That(stale, Is.Empty, "Every serializer include entry has to name one that exists.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The keys the field pass writes itself are in the set, each where it lands: a damage specifier's key and its twins'
        /// side keys, which an include of one would put in its owner's row; a door's backing key, which the pass writes in
        /// place of the computed field; a grid's gas, whose serializer writes keys of its own; and a station's records, an
        /// include of a data definition whose pairs are merged in. The control: an include written by a serializer the table
        /// does not list throws rather than guessing.
        /// </summary>
        [Test]
        public void TheDeclaredKeysHoldWhatThePassWrites()
        {
            var damage = DrydockDeclaredKeys.Of(typeof(DamageSpecifier));
            var door = DrydockDeclaredKeys.Of(typeof(DoorComponent));
            var atmosphere = DrydockDeclaredKeys.Of(typeof(GridAtmosphereComponent));
            var records = DrydockDeclaredKeys.Of(typeof(StationRecordsComponent));
            var recordSet = DrydockDeclaredKeys.Of(typeof(StationRecordSet));
            var unlisted = typeof(TileAtmosCollectionSerializer).GetNestedType("TileAtmosChunk", BindingFlags.NonPublic);

            Assert.Multiple(() =>
            {
                Assert.That(damage, Is.SupersetOf(new[] { "types", "groups", "~types", "~groups" }),
                    "A damage specifier's key and its twins' side keys.");
                Assert.That(door, Does.Contain(DataDefinitionUtility.AutoGenerateTag(nameof(DoorComponent.NextStateChange))),
                    "A door's backing key, which the pass writes in the computed field's place.");
                Assert.That(door, Does.Contain("secondsUntilStateChange"),
                    "The computed field is still a declared tag, so a row carrying it reaches the pass, which refuses it by name.");
                Assert.That(atmosphere, Is.SupersetOf(new[] { "version", "data" }), "A grid's gas, as its serializer writes it.");
                Assert.That(recordSet, Is.Not.Empty, "The control: a station's record set declares something.");
                Assert.That(records, Is.SupersetOf(recordSet), "An include of a data definition merges its keys into its owner's.");
                Assert.That(unlisted, Is.Not.Null, "The control needs the atmosphere chunk, an include written by a serializer nobody lists.");
                Assert.That(() => DrydockDeclaredKeys.Of(unlisted!), Throws.InvalidOperationException,
                    "An include whose serializer's keys nobody lists has to throw, not guess.");
            });
        }

        /// <summary>
        /// A manifest row's key the manifest does not list throws unless a retirement names it; a retired one is read as
        /// nothing and counted once across the row's three reads. The control's control: the same row under the real list,
        /// which does not name the control's key, throws, so it is the retirement that lets it through.
        /// </summary>
        [Test]
        public async Task AManifestKeyIsDroppedWhenRetiredAndRefusedWhenNot()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var factory = server.ResolveDependency<IComponentFactory>();
            var retired = new DrydockRetiredKey(DrydockRetiredKind.ManifestMember, "Wires.DrydockRetiredControl", "a control", "0123abc");

            var read = new List<(DrydockManifestMember, object?)>();
            var dropped = -1;
            Exception? unlisted = null, unretired = null;

            await server.WaitPost(() =>
            {
                var codec = Codec(pair, ImmutableArray.Create(retired));
                var row = Row(retired.Key);
                foreach (var moment in new[] { DrydockApplyMoment.BeforeInit, DrydockApplyMoment.Seam, DrydockApplyMoment.AfterStart })
                    read.AddRange(codec.ReadManifest(row, moment, factory));
                dropped = codec.RetiredDropped.GetValueOrDefault(retired);

                unlisted = Catch(() => codec.ReadManifest(Row("Wires.DrydockUnlistedControl"), DrydockApplyMoment.BeforeInit, factory).ToList());
                unretired = Catch(() => Codec(pair, DrydockRetiredKeys.All).ReadManifest(row, DrydockApplyMoment.BeforeInit, factory).ToList());
            });

            Assert.Multiple(() =>
            {
                Assert.That(read, Is.Empty, "A retired key sets nothing.");
                Assert.That(dropped, Is.EqualTo(1), "And is counted once, though the row is read at three moments.");
                Assert.That(unlisted, Is.InstanceOf<FormatException>(), "A key neither listed nor retired has to throw.");
                Assert.That(unretired, Is.InstanceOf<FormatException>(), "The control's control: without the retirement the same row throws.");
            });

            await pair.CleanReturnAsync();

            static MappingDataNode Row(string key)
            {
                var row = new MappingDataNode();
                row[key] = new ValueDataNode("1");
                return row;
            }
        }

        /// <summary>
        /// A component row's key its component does not declare throws unless a retirement names it; a retired one is taken
        /// out before the read and counted, and the caller's row is left as it was. The control: the row as written reads.
        /// </summary>
        [Test]
        public async Task ADataFieldIsDroppedWhenRetiredAndRefusedWhenNot()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var retired = new DrydockRetiredKey(DrydockRetiredKind.DataField, "Door.drydockRetiredControl", "a control", "0123abc");

            Exception? written = new(), undeclared = null, dropped = new();
            var count = -1;
            var callerKept = false;

            await server.WaitPost(() =>
            {
                // On the grid: an airlock anchors itself at spawn.
                var uid = entMan.SpawnEntity("Airlock", map.GridCoords);
                var codec = Codec(pair, ImmutableArray.Create(retired));
                var row = codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), entMan.GetComponent<DoorComponent>(uid));

                written = Catch(() => codec.Read<DoorComponent>(row));

                var stray = row.Copy();
                stray["drydockUndeclaredControl"] = new ValueDataNode("1");
                undeclared = Catch(() => codec.Read<DoorComponent>(stray));

                var old = row.Copy();
                old["drydockRetiredControl"] = new ValueDataNode("1");
                dropped = Catch(() => codec.Read<DoorComponent>(old));
                count = codec.RetiredDropped.GetValueOrDefault(retired);
                callerKept = old.Has("drydockRetiredControl");
            });

            Assert.Multiple(() =>
            {
                Assert.That(written, Is.Null, "The control: the row as the codec wrote it reads.");
                Assert.That(undeclared, Is.InstanceOf<FormatException>(), "A key the door does not declare and nothing retires has to throw.");
                Assert.That(undeclared?.Message, Does.Contain("drydockUndeclaredControl"), "And name the key.");
                Assert.That(dropped, Is.Null, "A retired key is dropped, not refused.");
                Assert.That(count, Is.EqualTo(1), "And counted.");
                Assert.That(callerKept, Is.True, "The caller's row is not the one it is taken out of.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A tagged value never puts <see cref="DrydockNodeJson.TagKey"/> among a row's keys: the stored form wraps it and
        /// its decode unwraps it into the node's tag. A container manager's containers are tagged by their type, so its row
        /// goes through the stored form and reads.
        /// </summary>
        [Test]
        public async Task ATaggedValueReadsThroughTheStoredForm()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var tagged = false;
            var sameKeys = false;
            var encodedTag = false;
            Exception? read = new();

            await server.WaitPost(() =>
            {
                var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                entMan.System<SharedContainerSystem>().EnsureContainer<ContainerSlot>(uid, "drydock-control");
                var codec = Codec(pair, DrydockRetiredKeys.All);
                var row = codec.Write((uid, entMan.GetComponent<MetaDataComponent>(uid)), entMan.GetComponent<ContainerManagerComponent>(uid));

                tagged = HasTag(row);
                var json = DrydockNodeJson.Encode(row)!.ToJsonString();
                encodedTag = json.Contains(DrydockNodeJson.TagKey);
                var decoded = (MappingDataNode) DrydockNodeJson.Decode(JsonNode.Parse(json));
                sameKeys = decoded.Keys.Order().SequenceEqual(row.Keys.Order()) && !decoded.Has(DrydockNodeJson.TagKey);
                read = Catch(() => codec.Read<ContainerManagerComponent>(decoded));
            });

            Assert.Multiple(() =>
            {
                Assert.That(tagged, Is.True, "The control: the container manager's row carries a tagged value.");
                Assert.That(encodedTag, Is.True, "The control: the stored form carries the tag as its reserved member.");
                Assert.That(sameKeys, Is.True, "The decoded row's keys are the written row's, with no reserved member among them.");
                Assert.That(read, Is.Null, "And the row reads.");
            });

            await pair.CleanReturnAsync();

            static bool HasTag(DataNode node) => node.Tag != null || node switch
            {
                MappingDataNode mapping => mapping.Values.Any(HasTag),
                SequenceDataNode sequence => sequence.Sequence.Any(HasTag),
                _ => false,
            };
        }

        /// <summary>
        /// A load drops each of the three kinds of retired key and counts it on its result: a manifest member, a component
        /// row, and a data field in a row. The control: each one alone, under the real list that does not retire it, fails
        /// the load.
        /// </summary>
        [Test]
        public async Task ALoadDropsEachKindOfRetiredKeyAndCountsIt()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var mapSys = server.System<SharedMapSystem>();
            var images = server.System<DrydockImageSystem>();
            var view = await pair.CreateTestMap();

            var member = new DrydockRetiredKey(DrydockRetiredKind.ManifestMember, "Wires.DrydockRetiredControl", "a control", "0123abc");
            var component = new DrydockRetiredKey(DrydockRetiredKind.Component, "DrydockRetiredControl", "a control", "0123abc");
            var field = new DrydockRetiredKey(DrydockRetiredKind.DataField, "Transform.drydockRetiredControl", "a control", "0123abc");
            var retirements = ImmutableArray.Create(member, component, field);

            IReadOnlyDictionary<DrydockRetiredKey, int> counted = new Dictionary<DrydockRetiredKey, int>();
            var refused = new Dictionary<DrydockRetiredKind, Exception?>();

            await server.WaitPost(() =>
            {
                var grid = mapSys.CreateGridEntity(view.MapId);
                mapSys.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, view.Tile.Tile);
                entMan.SpawnEntity(null, new EntityCoordinates(grid.Owner, 0.5f, 0.5f));

                var stored = images.Store(grid.Owner);
                Assert.That(stored.Whole, Is.True, "The control: the hull stores whole.");
                entMan.DeleteEntity(grid.Owner);

                var all = Inject(stored.Image, member, component, field);
                counted = images.Load(all, mapSys.CreateMap(out _), new DrydockLoadOptions { Retirements = retirements }).RetiredDropped;

                foreach (var one in retirements)
                {
                    var image = Inject(stored.Image, one);
                    refused[one.Kind] = Catch(() => images.Load(image, mapSys.CreateMap(out _)));
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(counted.GetValueOrDefault(member), Is.EqualTo(1), "The retired manifest key is dropped and counted.");
                Assert.That(counted.GetValueOrDefault(component), Is.EqualTo(1), "The retired component's row is dropped and counted.");
                Assert.That(counted.GetValueOrDefault(field), Is.EqualTo(1), "The retired data field is dropped and counted.");
                foreach (var one in retirements)
                    Assert.That(refused[one.Kind], Is.Not.Null, $"The control: an unretired {one.Kind} fails the load.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The image with each key put on its first entity that is not the grid, where a stored image would hold it: a
        /// manifest key in that entity's manifest row, a component as a row of its own, a data field in its Transform row.
        /// </summary>
        private static DrydockImage Inject(DrydockImage image, params DrydockRetiredKey[] keys)
        {
            var entities = image.Entities.ToList();
            var index = entities.FindIndex(e => e.Id != image.GridId);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "The control needs an entity besides the grid.");

            var rows = entities[index].Rows.ToDictionary(p => p.Key, p => p.Value);
            foreach (var key in keys)
            {
                switch (key.Kind)
                {
                    case DrydockRetiredKind.ManifestMember:
                        var manifest = rows.TryGetValue(DrydockCodec.ManifestRow, out var existing) ? JsonNode.Parse(existing)!.AsObject() : new JsonObject();
                        manifest[key.Key] = "1";
                        rows[DrydockCodec.ManifestRow] = manifest.ToJsonString();
                        break;

                    case DrydockRetiredKind.Component:
                        rows[key.Key] = "{}";
                        break;

                    case DrydockRetiredKind.DataField:
                        var (name, tag) = (key.Key[..key.Key.IndexOf('.')], key.Key[(key.Key.IndexOf('.') + 1)..]);
                        var row = JsonNode.Parse(rows[name])!.AsObject();
                        row[tag] = "1";
                        rows[name] = row.ToJsonString();
                        break;
                }
            }

            entities[index] = entities[index] with { Rows = rows };
            return image with { Entities = entities };
        }

        /// <summary>What is wrong with each entry of a retirement list, one line each; empty when nothing is.</summary>
        private static List<string> Wrong(IReadOnlyList<DrydockRetiredKey> list, IComponentFactory factory)
        {
            var wrong = new List<string>();
            var live = DrydockCodecManifestMembers.Members.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var group in list.GroupBy(entry => (entry.Kind, entry.Key)).Where(group => group.Count() > 1))
                wrong.Add($"{group.Key.Kind} {group.Key.Key}: listed {group.Count()} times");

            foreach (var entry in list)
            {
                var what = $"{entry.Kind} {entry.Key}";
                if (!Shapes[entry.Kind].IsMatch(entry.Key))
                    wrong.Add($"{what}: not shaped as a {entry.Kind} key");
                if (string.IsNullOrWhiteSpace(entry.Reason))
                    wrong.Add($"{what}: no reason");
                if (!Commit.IsMatch(entry.Commit))
                    wrong.Add($"{what}: commit '{entry.Commit}' is not 7 to 40 hex characters");

                switch (entry.Kind)
                {
                    case DrydockRetiredKind.ManifestMember when live.Contains(entry.Key):
                        wrong.Add($"{what}: still a live manifest member");
                        break;

                    case DrydockRetiredKind.Component when factory.TryGetRegistration(entry.Key, out _):
                        wrong.Add($"{what}: still a registered component");
                        break;

                    case DrydockRetiredKind.DataField when entry.Key.IndexOf('.') is > 0 and var dot
                                                           && factory.TryGetRegistration(entry.Key[..dot], out var registration)
                                                           && DrydockDeclaredKeys.Of(registration.Type).Contains(entry.Key[(dot + 1)..]):
                        wrong.Add($"{what}: still a key {registration.Type.Name} declares");
                        break;
                }
            }

            return wrong;
        }

        /// <summary>A codec honouring <paramref name="retirements"/>, with no image behind it: nothing here reads a reference.</summary>
        private static DrydockCodec Codec(TestPair pair, ImmutableArray<DrydockRetiredKey> retirements) =>
            new(pair.Server.ResolveDependency<ISerializationManager>(), pair.Server.EntMan, pair.Server.ResolveDependency<IGameTiming>(),
                _ => null, _ => EntityUid.Invalid)
            {
                Retirements = retirements,
            };

        private static Exception? Catch(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }
}
