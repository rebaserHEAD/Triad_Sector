#nullable enable
using System.Collections.Generic;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using NUnit.Framework;

namespace Content.Tests.Server._Triad.Drydock;

/// <summary>
/// The read-back compare on hand-built images: what a <c>jsonb</c> round trip changes and must pass (key order, spacing),
/// and what it must not change and so fails (a value, a row, an entity, the order entities load in).
/// </summary>
[TestFixture, TestOf(typeof(DrydockImageComparer))]
[Parallelizable(ParallelScope.All)]
public sealed class DrydockImageComparerTest
{
    private static DrydockImageEntity Entity(long id, string? prototype, params (string Row, string Json)[] rows)
    {
        var map = new Dictionary<string, string>();
        foreach (var (row, json) in rows)
            map[row] = json;

        return new DrydockImageEntity(id, prototype, true, map);
    }

    private static DrydockImage Image(params DrydockImageEntity[] entities) =>
        new(1, entities, "{\"size\":\"16\",\"tilemap\":{\"0\":\"Space\"},\"chunks\":{}}", 0, 0);

    private static readonly DrydockImage Filed = Image(
        Entity(1, "Grid", ("Transform", "{\"parent\":\"invalid\",\"pos\":\"0,0\"}")),
        Entity(2, "Wall", ("Transform", "{\"parent\":\"1\",\"pos\":\"1,0\"}"), ("~appearance", "{\"a\":[\"1\",\"2\"]}")));

    [Test]
    public void KeyOrderAndSpacingAreNotDifferences()
    {
        var read = Image(
            Entity(1, "Grid", ("Transform", "{\"pos\": \"0,0\", \"parent\": \"invalid\"}")),
            Entity(2, "Wall", ("Transform", "{\"pos\": \"1,0\", \"parent\": \"1\"}"), ("~appearance", "{\"a\": [\"1\", \"2\"]}")));

        Assert.That(DrydockImageComparer.Differences(Filed, read), Is.Empty);
        Assert.DoesNotThrow(() => DrydockImageComparer.AssertSame(Filed, read));
    }

    [Test]
    public void AChangedValueIsADifference()
    {
        var read = Image(
            Entity(1, "Grid", ("Transform", "{\"parent\":\"invalid\",\"pos\":\"0,0\"}")),
            Entity(2, "Wall", ("Transform", "{\"parent\":\"1\",\"pos\":\"2,0\"}"), ("~appearance", "{\"a\":[\"1\",\"2\"]}")));

        Assert.That(DrydockImageComparer.Differences(Filed, read), Is.EqualTo(new[] { "entity 2: row Transform differs" }));
    }

    [Test]
    public void AListIsComparedInOrder()
    {
        var read = Image(
            Entity(1, "Grid", ("Transform", "{\"parent\":\"invalid\",\"pos\":\"0,0\"}")),
            Entity(2, "Wall", ("Transform", "{\"parent\":\"1\",\"pos\":\"1,0\"}"), ("~appearance", "{\"a\":[\"2\",\"1\"]}")));

        Assert.That(DrydockImageComparer.Differences(Filed, read), Is.EqualTo(new[] { "entity 2: row ~appearance differs" }));
    }

    [Test]
    public void MissingAndExtraRowsAndEntitiesAreDifferences()
    {
        var read = Image(
            Entity(1, "Grid", ("Transform", "{\"parent\":\"invalid\",\"pos\":\"0,0\"}"), ("Physics", "{}")),
            Entity(3, "Wall", ("Transform", "{\"parent\":\"1\",\"pos\":\"1,0\"}")));

        Assert.That(DrydockImageComparer.Differences(Filed, read), Is.EquivalentTo(new[]
        {
            "entity 1: row Physics appeared",
            "entity 2 (Wall) is missing",
            "entity 3 appeared",
        }));
    }

    [Test]
    public void TheOrderEntitiesLoadInIsContent()
    {
        var read = Image(Filed.Entities[1], Filed.Entities[0]);

        Assert.That(DrydockImageComparer.Differences(Filed, read), Is.EqualTo(new[] { "the entities are the same but in another order" }));
    }

    [Test]
    public void AMismatchThrowsTheTypedException()
    {
        var read = Image(Filed.Entities[0]);

        var thrown = Assert.Throws<DrydockImageMismatchException>(() => DrydockImageComparer.AssertSame(Filed, read));
        Assert.That(thrown!.Differences, Is.EqualTo(new[] { "entity 2 (Wall) is missing" }));
    }

    [Test]
    public void MapInitialisationIsADifference()
    {
        var started = new DrydockImageEntity(1, "Grid", false, Filed.Entities[0].Rows);
        var read = new DrydockImage(1, new[] { started, Filed.Entities[1] }, Filed.Tiles, 0, 0);

        Assert.That(DrydockImageComparer.Differences(Filed, read), Is.EqualTo(new[] { "entity 1: map-initialised True became False" }));
    }

    [Test]
    public void BytesAreNotCompared()
    {
        var read = new DrydockImage(1, Filed.Entities, Filed.Tiles, 0, 999);

        Assert.That(DrydockImageComparer.Differences(Filed, read), Is.Empty);
    }
}
