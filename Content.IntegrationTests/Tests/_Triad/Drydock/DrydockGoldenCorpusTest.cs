#nullable enable

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The golden corpus gate: every frozen fixture still materializes on today's code, its reborn hull
    /// matches the manifest it was filed with, and storing that hull again writes the same manifest.
    /// This is the CI gate for upstream merges and engine bumps, because every durability promise the
    /// drydock makes reduces to "old images still load on new code", and a fixture that is regenerated
    /// on every run cannot measure that.
    ///
    /// <para>The fixtures are real roster hulls put through named recipes and filed by the real store,
    /// written only by <see cref="DrydockGoldenCorpusRefreshLocalTest"/>. The gate files each one into
    /// the test database under a fresh hull id through the image store, retrieves it through the whole
    /// pipeline including the dock, and reads the reborn grid before it has simulated more than a tick.</para>
    ///
    /// <para>What is compared, and against what, is in <see cref="GoldenCorpus.Verify"/>. What is
    /// deliberately not compared: the image row for row (clock offsets differ on every store of one
    /// hull), the manifest's entry order (it follows the transform child set a reload rebuilds, so the
    /// manifest is compared as a tree), and the two format versions (constants of the storing code, so a
    /// newer build moving them is a bump rather than a failure). All three are printed.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockGoldenCorpusTest
    {
        [Test]
        public async Task EveryGoldenFixtureStillMaterializesAndMatchesItsManifest()
        {
            var fixtures = GoldenCorpus.Discover();

            // The control on discovery: a corpus that stopped being embedded, or a directory somebody
            // emptied, would otherwise verify nothing and pass.
            Assert.That(fixtures, Has.Count.GreaterThanOrEqualTo(GoldenCorpus.CommittedFixtures),
                $"Found {fixtures.Count} golden fixture(s), fewer than the {GoldenCorpus.CommittedFixtures} committed: "
                + string.Join(", ", fixtures.Select(f => f.Name)));

            await using var pair = await PoolManager.GetServerClient();
            var (owner, station) = await GoldenCorpus.PrepareHarness(pair, fixtures.Count + 1);

            var failed = 0;
            var summary = new StringBuilder();

            foreach (var fixture in fixtures)
            {
                var report = await GoldenCorpus.Verify(pair, fixture, owner, station);
                await TestContext.Out.WriteLineAsync($"[golden-corpus] {report}");

                if (report.Passed)
                    continue;

                failed++;
                summary.Append(Environment.NewLine).Append(report);
            }

            Assert.That(failed, Is.Zero,
                $"{failed} of {fixtures.Count} golden fixture(s) no longer round-trip on this code:{summary}");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The gate going red, once per assertion, on corrupted copies of committed fixtures. Each
        /// corruption is aimed at one assertion and the check is that that one fails; the stack flip
        /// also checks the census stays green, so the value comparison is proven to see what the census
        /// cannot. Corrupted in memory, never on disk, and the image file's hash is recomputed over the
        /// corrupted image so the rot check does not catch it first.
        /// </summary>
        [Test]
        public async Task TheGateGoesRedOnACorruptedFixture()
        {
            var fixtures = GoldenCorpus.Discover();
            Assert.That(fixtures, Is.Not.Empty, "The control needs a fixture to corrupt.");

            // The stack flip needs the cargo recipe's stack; the smallest such fixture keeps this cheap.
            var basis = fixtures
                .Where(f => f.Recipes.Contains("cargo-into-locker"))
                .OrderBy(f => f.Revision.SizeBytes)
                .First();

            await using var pair = await PoolManager.GetServerClient();
            var (owner, station) = await GoldenCorpus.PrepareHarness(pair, 4);

            // A wall is a grid child with no children and no row names it, so taking one out cannot break
            // the load; it can only leave the reborn hull one short.
            var dropped = Corrupt(basis, image => DropOneEntity(image, "WallReinforced"));
            var droppedReport = await GoldenCorpus.Verify(pair, dropped, owner, station);
            await TestContext.Out.WriteLineAsync($"[golden-control] dropped entity: {droppedReport}");

            // The only control that is meant to log errors, so the only one run with the pair's failure
            // level lowered. What turns (a) red here is therefore the refused retrieve itself, which is
            // the stronger half of the assertion: the error lines are gone from the report with the level.
            var missing = Corrupt(basis, image => RenameOne(image, "WallReinforced", "GoldenCorpusControlNoSuchPrototype"));
            var missingReport = await DrydockTestHelpers.Quietly(pair, () => GoldenCorpus.Verify(pair, missing, owner, station));
            await TestContext.Out.WriteLineAsync($"[golden-control] missing prototype: {missingReport}");

            var flipped = Corrupt(basis, image => FlipStackCount(image, "SheetSteel", 17, 16));
            var flippedReport = await GoldenCorpus.Verify(pair, flipped, owner, station);
            await TestContext.Out.WriteLineAsync($"[golden-control] flipped stack count: {flippedReport}");

            Assert.Multiple(() =>
            {
                Assert.That(droppedReport.Census, Is.Not.Empty, "(b) must go red on a hull that comes back one entity short.");
                Assert.That(droppedReport.Restore, Is.Not.Empty, "(d) must go red too: the fresh store walks the short hull.");

                Assert.That(missingReport.Load, Is.Not.Empty, "(a) must go red on an image naming a prototype that does not exist.");

                Assert.That(flippedReport.Values, Is.Not.Empty, "(c) must go red on a stack that comes back with a different count.");
                Assert.That(flippedReport.Census, Is.Empty, "The census cannot see a stack count, so a red census here would mean the corruption hit more than the count.");
                Assert.That(flippedReport.Load, Is.Empty, "The flipped image still loads cleanly.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>A copy of <paramref name="basis"/> whose image has been edited, with its file, hash and size recomputed.</summary>
        private static GoldenFixture Corrupt(GoldenFixture basis, Func<DrydockImage, DrydockImage> edit)
        {
            var copy = basis.Clone();
            var image = GoldenCorpus.ReadImage(copy.ImageFile);
            var edited = edit(image);
            Assert.That(DrydockImageComparer.Differences(image, edited), Is.Not.Empty,
                "The corruption changed nothing, so the control would prove nothing.");

            copy.ImageFile = GoldenCorpus.WriteImage(edited);
            copy.ImageFileSha256 = GoldenCorpus.Sha256(copy.ImageFile);
            copy.ImageFileBytes = copy.ImageFile.Length;
            copy.Name = basis.Name + " (corrupted)";
            return copy;
        }

        /// <summary>Removes the first entity of <paramref name="proto"/>, which has to have a sibling of its prototype and no child.</summary>
        private static DrydockImage DropOneEntity(DrydockImage image, string proto)
        {
            var group = image.Entities.Where(e => e.Prototype == proto).ToList();
            Assert.That(group, Has.Count.GreaterThanOrEqualTo(2), $"No {proto} group with two or more entities to drop one from.");

            var victim = group[0];
            var parentRef = victim.Id.ToString(CultureInfo.InvariantCulture);
            Assert.That(image.Entities.Any(e => ParentOf(e) == parentRef), Is.False, $"The {proto} to drop has children, so dropping it would orphan them.");

            return image with { Entities = image.Entities.Where(e => e.Id != victim.Id).ToList() };
        }

        private static DrydockImage RenameOne(DrydockImage image, string proto, string replacement)
        {
            var index = image.Entities.ToList().FindIndex(e => e.Prototype == proto);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), $"No {proto} to rename.");

            var entities = image.Entities.ToList();
            entities[index] = entities[index] with { Prototype = replacement };
            return image with { Entities = entities };
        }

        /// <summary>Changes the Stack row's count on the first entity of <paramref name="proto"/> whose stack holds <paramref name="from"/>.</summary>
        private static DrydockImage FlipStackCount(DrydockImage image, string proto, int from, int to)
        {
            var entities = image.Entities.ToList();
            var fromText = from.ToString(CultureInfo.InvariantCulture);
            var index = entities.FindIndex(e => e.Prototype == proto
                                                && e.Rows.TryGetValue("Stack", out var row)
                                                && JsonNode.Parse(row)?["count"]?.ToString() == fromText);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), $"No {proto} stack of {from} in the image.");

            var rows = entities[index].Rows.ToDictionary(p => p.Key, p => p.Value);
            var stack = JsonNode.Parse(rows["Stack"])!;
            stack["count"] = to.ToString(CultureInfo.InvariantCulture);
            rows["Stack"] = stack.ToJsonString();

            entities[index] = entities[index] with { Rows = rows };
            return image with { Entities = entities };
        }

        /// <summary>The parent reference in an entity's Transform row, or null when it has none.</summary>
        private static string? ParentOf(DrydockImageEntity entity)
        {
            if (!entity.Rows.TryGetValue("Transform", out var transform))
                return null;

            using var document = JsonDocument.Parse(transform);
            return document.RootElement.TryGetProperty("parent", out var parent) ? parent.ToString() : null;
        }
    }
}
