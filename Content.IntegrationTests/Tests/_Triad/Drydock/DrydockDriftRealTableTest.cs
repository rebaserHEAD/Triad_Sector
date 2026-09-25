#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The drift detector against what only a running server has: the real migration files, the real
    /// prototype registry, the engine's own documents, and the loader that reads them. The drift
    /// matrix itself lives in <c>Content.Tests</c> on synthetic inputs.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockDrift))]
    public sealed class DrydockDriftRealTableTest
    {
        private static string Group(string proto, int uid) =>
            $"- proto: {proto}\n  entities:\n  - uid: {uid}\n    components:\n    - type: Transform\n      pos: 0.5,0.5\n      parent: 1\n";

        [Test]
        public async Task TheRealMappingFilesBuildTheLoadersTableAndDetectOnIt()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var resources = server.ResolveDependency<IResourceManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            DrydockMigrationTable table = null!;
            var ev = new BeforeEntityReadEvent();

            await server.WaitPost(() =>
            {
                table = DrydockMigrationTable.Load(resources);
                server.EntMan.EventBus.RaiseEvent(EventSource.Local, ev);
            });

            Assert.That(table.Renamed.Count + table.Deleted.Count, Is.GreaterThan(500),
                "The control: the real files hold roughly a thousand entries, so a near-empty table means they were not read.");

            // The table must be the one MapMigrationSystem hands the loader. Holiday replacements can
            // add renames of their own to the event, so the event may hold more, never less or other.
            Assert.Multiple(() =>
            {
                Assert.That(table.Deleted, Is.EquivalentTo(ev.DeletedPrototypes));
                foreach (var (from, to) in table.Renamed)
                {
                    Assert.That(ev.RenamedPrototypes.GetValueOrDefault(from), Is.EqualTo(to), $"rename of {from}");
                }
            });

            bool Known(string id) => protoMan.HasIndex<EntityPrototype>(id);

            var rename = table.Renamed
                .Where(kv => !table.Deleted.Contains(kv.Key) && !Known(kv.Key) && Known(kv.Value))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .First();
            var deleted = table.Deleted.Where(id => !Known(id)).Order(StringComparer.Ordinal).First();
            var known = protoMan.EnumeratePrototypes<EntityPrototype>()
                .Where(p => !p.Abstract && !table.Renamed.ContainsKey(p.ID) && !table.Deleted.Contains(p.ID))
                .Select(p => p.ID)
                .Order(StringComparer.Ordinal)
                .First();
            const string phantom = "DrydockDriftTestPhantomPrototype";

            var yaml = "meta:\n  format: 7\n  category: Grid\nentities:\n"
                + "- proto: \"\"\n  entities:\n  - uid: 1\n    components:\n    - type: Transform\n      parent: invalid\n"
                + Group(known, 2) + Group(rename.Key, 3) + Group(deleted, 4) + Group(phantom, 5);

            var (ids, format) = DrydockSystem.ReadDriftIds(yaml);
            var verdict = DrydockDrift.Detect(ids, table, Known, format, DrydockDrift.EngineWindow,
                DrydockFormat.Current, DrydockDrift.DrydockWindow);

            Assert.Multiple(() =>
            {
                Assert.That(verdict.Renamed, Is.EqualTo(new[] { new DrydockRename(rename.Key, rename.Value) }));
                Assert.That(verdict.Deleted, Is.EqualTo(new[] { deleted }));
                Assert.That(verdict.Unresolved, Is.EqualTo(new[] { phantom }));
                Assert.That(verdict.IsRefusal, Is.True);
                Assert.That(verdict.EngineFormatOutOfWindow, Is.False);
            });

            // The shape of the real table's rough edges. Dangling targets and ids both renamed and
            // deleted are reported only. Chains are asserted: a load applies one rename per id, so a
            // chain (A to B, B to C) leaves a ship on B, which is itself renamed, and a cycle never
            // settles. Collapse a chain in the mapping files (A straight to C) rather than loosening this.
            var chains = table.Renamed.Where(kv => table.Renamed.ContainsKey(kv.Value)).Select(kv => kv.Key).ToList();
            var dangling = table.Renamed.Count(kv => !Known(kv.Value) && !table.Deleted.Contains(kv.Value));
            var both = table.Renamed.Keys.Count(table.Deleted.Contains);
            TestContext.Out.WriteLine(
                $"renamed={table.Renamed.Count} deleted={table.Deleted.Count} chains={chains.Count} danglingTargets={dangling} renamedAndDeleted={both} eventExtraRenames={ev.RenamedPrototypes.Count - table.Renamed.Count}");

            Assert.That(chains, Is.Empty,
                "A migration rename points at an id that is itself renamed; one hop per load leaves those ships on a renamed id.");

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task RealShipsReEmitByteForByte()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapLoader = server.System<MapLoaderSystem>();
            var map = await pair.CreateTestMap();

            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .OrderBy(v => v.Price)
                .Where((_, i) => i % 15 == 0)
                .Take(3)
                .ToList();

            var compared = 0;

            foreach (var vessel in vessels)
            {
                string? yaml = null;

                await server.WaitPost(() =>
                {
                    if (!mapLoader.TryLoadGrid(map.MapId, vessel.ShuttlePath, out var grid))
                        return;

                    using var writer = new StringWriter();
                    var options = new SerializationOptions { MissingEntityBehaviour = MissingEntityBehaviour.Ignore };
                    if (mapLoader.TrySaveGrid(grid!.Value.Owner, writer, options))
                        yaml = writer.ToString();

                    server.EntMan.DeleteEntity(grid!.Value.Owner);
                });

                await pair.RunTicksSync(2);

                if (yaml == null)
                    continue;

                using var reader = new StringReader(yaml);
                var root = (MappingDataNode) DataNodeParser.ParseYamlStream(reader).Single().Root;

                Assert.That(DrydockSystem.EmitDocument(root), Is.EqualTo(yaml),
                    $"{vessel.ID}: the engine's emitter does not reproduce its own document.");

                compared++;
            }

            Assert.That(compared, Is.GreaterThan(0), "The control: no vessel loaded and saved, so nothing was compared.");
            await pair.CleanReturnAsync();
        }
    }
}
