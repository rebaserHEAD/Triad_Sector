#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The drift detector against what only a running server has: the real migration files, the
    /// table the engine's loader is handed, and the real prototype registry. The drift matrix itself
    /// lives in <c>Content.Tests</c> on synthetic inputs.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockDrift))]
    public sealed class DrydockDriftRealTableTest
    {
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

            var ids = new[] { known, rename.Key, deleted, phantom };
            var verdict = DrydockDrift.Detect(ids, table, Known, Array.Empty<string>(), _ => true, DrydockSystem.ImageEngineFormat,
                DrydockDrift.EngineWindow, DrydockFormat.Current, DrydockDrift.DrydockWindow);

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

        /// <summary>
        /// The live prototypes the table deletes on purpose: the faction purge of #504 (triad_migration.yml), whose
        /// prototypes stay in upstream files. A map or a stored ship that carries one loses it on load, by design.
        /// </summary>
        private static readonly string[] DeliberatelyPurged =
        {
            "PiratePDA", "SeniorOfficerPDA", "SpawnPointSecurityCadet", "SpawnPointSecurityOfficer",
        };

        private const string UnusedBase = "a concrete base of its family that nothing spawns bare";
        private const string NoProducer = "nothing makes one: declared only, or made only by a spawner nothing places";
        private const string MapPlaced =
            "no prototype makes one, and the shipped maps that place it convert it on every load, so a retrieve converts what a purchase does";
        private const string Rebrand = "the Triad rebrand: no prototype makes one, the maps that place it convert it on load, and the target extends it";

        /// <summary>
        /// The live, concrete, savable prototypes the table renames on purpose, each with the reason a stored ship carrying one
        /// loses nothing when it comes back as the target.
        /// </summary>
        private static readonly Dictionary<string, string> RenamedWhileLive = new()
        {
            ["Intercom"] = UnusedBase,
            ["CrateBaseWeldable"] = UnusedBase,
            ["AsteroidRockCoalCrab"] = NoProducer,
            ["AsteroidRockGoldCrab"] = NoProducer,
            ["AsteroidRockSilverCrab"] = NoProducer,
            ["WallSpawnAsteroidCoalCrab"] = NoProducer,
            ["WallSpawnAsteroidGoldCrab"] = NoProducer,
            ["WallSpawnAsteroidIronCrab"] = NoProducer,
            ["WallSpawnAsteroidQuartzCrab"] = NoProducer,
            ["WallSpawnAsteroidSilverCrab"] = NoProducer,
            ["WallSpawnAsteroidUraniumCrab"] = NoProducer,
            ["ClothingOuterHardsuitViperGroupStandard"] = NoProducer,
            ["ClothingOuterHardsuitViperGroupMedic"] = NoProducer,
            ["ClothingOuterHardsuitViperGroupJuggernaut"] = NoProducer,
            ["ClothingHeadHelmetHardsuitViperGroupStandard"] = NoProducer,
            ["ClothingHeadHelmetHardsuitViperGroupMedic"] = NoProducer,
            ["ClothingHeadHelmetHardsuitViperGroupJuggernaut"] = NoProducer,
            ["Ashtray"] = MapPlaced,
            ["OreBox"] = MapPlaced,
            ["ConstructionBox"] = MapPlaced,
            ["PlantBox"] = MapPlaced,
            ["AsteroidRockTinCrab"] = MapPlaced,
            ["AsteroidRockQuartzCrab"] = MapPlaced,
            ["AsteroidRockUraniumCrab"] = MapPlaced,
            ["ThrusterNfsd"] = Rebrand,
            ["GyroscopeNfsd"] = Rebrand,
            ["SmallGyroscopeNfsd"] = Rebrand,
            ["ThrusterLargeNfsd"] = Rebrand,
            ["RadioHandheld"] = "the salvage vendor and a starting-gear pocket make it, and its target extends it, so a converted radio works the same",
            ["ClothingBackpackDuffelClothingOuterHardsuitAshenBundle"] =
                "a copy (#547): the target is the same bundle under its Triad id, and every producer makes the target",
        };

        /// <summary>
        /// A retrieve hands the table to the load (<c>DrydockLoadOptions.Migrations</c>), so a deletion of a live prototype
        /// drops that entity and everything under it from every stored ship, as it does from every map, and a rename turns it
        /// into the target. Every id the table deletes is absent from the entity index but the named purge; every live
        /// rename source is abstract, unsavable (no image holds one), or named with its reason in
        /// <see cref="RenamedWhileLive"/>; and every rename target is present. Controls: the purge is in the real table and
        /// its ids are live, and the table renames live abstract or unsavable prototypes too, so neither exception is vacuous.
        /// </summary>
        [Test]
        public async Task EveryRetiredLiveIdIsNamedAndEveryRenameTargetIsLive()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var resources = server.ResolveDependency<IResourceManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            DrydockMigrationTable table = null!;
            await server.WaitPost(() => table = DrydockMigrationTable.Load(resources));

            bool Known(string id) => protoMan.HasIndex<EntityPrototype>(id);

            var liveDeleted = table.Deleted.Where(Known).Order(StringComparer.Ordinal).ToList();
            var liveSources = table.Renamed.Keys.Where(Known).ToList();
            var namedSources = liveSources
                .Where(id => protoMan.Index<EntityPrototype>(id) is { Abstract: false, MapSavable: true })
                .Order(StringComparer.Ordinal)
                .ToList();
            var missingTargets = table.Renamed
                .Where(kv => !Known(kv.Value))
                .Select(kv => $"{kv.Key} -> {kv.Value}")
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(table.Deleted, Is.SupersetOf(DeliberatelyPurged), "The control: the purge is in the real table.");
                Assert.That(DeliberatelyPurged.Where(id => !Known(id)), Is.Empty, "The control: the purged ids are live prototypes.");
                Assert.That(liveDeleted, Is.EquivalentTo(DeliberatelyPurged),
                    "The table deletes a live prototype, so every stored ship carrying one loses it and its subtree on retrieve.");
                Assert.That(liveSources.Count, Is.GreaterThan(namedSources.Count),
                    "The control: the table renames live abstract or unsavable prototypes too, which pass by what they are.");
                Assert.That(namedSources, Is.EquivalentTo(RenamedWhileLive.Keys),
                    "The table renames a live, concrete, savable prototype not named with its reason, so a stored ship carrying one "
                    + "comes back with the target instead, or a named one is no longer renamed while live.");
                Assert.That(missingTargets, Is.Empty, "The table renames an id to one no prototype has.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
