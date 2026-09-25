#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Codec;
using Content.Server._Triad.Drydock.Loader;
using NUnit.Framework;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Tests.Server._Triad.Drydock;

/// <summary>
/// What an image becomes as <c>drydock_entity</c> rows before any database sees it: the components object and its sorted
/// names, the reserved rows in their own columns, the parent read from the Transform row, and every refusal the store
/// makes rather than write a row it cannot keep faithfully.
/// </summary>
[TestFixture, TestOf(typeof(DrydockPostgresImageStore))]
[Parallelizable(ParallelScope.All)]
public sealed class DrydockPostgresImageRowsTest
{
    private static string Transform(string parent) => $"{{\"parent\":\"{parent}\",\"pos\":\"0,0\"}}";

    private static DrydockImageEntity Entity(long id, string parent, bool initialised = true, params (string Row, string Json)[] rows)
    {
        var map = new Dictionary<string, string> { ["Transform"] = Transform(parent) };
        foreach (var (row, json) in rows)
            map[row] = json;

        return new DrydockImageEntity(id, $"Proto{id}", initialised, false, map);
    }

    private static DrydockImage Image(params DrydockImageEntity[] entities) => new(1, entities, "{}", 0, 0);

    [Test]
    public void RowsSplitComponentsFromTheReservedColumns()
    {
        var rows = DrydockPostgresImageStore.EntityRows(Image(
            Entity(1, DrydockCodecContext.InvalidReference),
            Entity(2, "1", rows: new[]
            {
                ("Physics", "{\"b\":\"2\"}"),
                ("Anchorable", "{}"),
                (DrydockImageSystem.AppearanceRow, "{\"k\":\"v\"}"),
                (DrydockCodec.ManifestRow, "[]"),
                (DrydockImageSystem.CarriedRow, "{\"hands\":[]}"),
            })));

        var wall = rows[1];
        using var components = JsonDocument.Parse(wall.Components);
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].ParentId, Is.Null, "The grid's parent leaves the image.");
            Assert.That(wall.ParentId, Is.EqualTo(1));
            Assert.That(wall.PrototypeId, Is.EqualTo("Proto2"));
            Assert.That(wall.ComponentNames, Is.EqualTo(new[] { "Anchorable", "Physics", "Transform" }), "Sorted ordinally.");
            Assert.That(components.RootElement.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(wall.ComponentNames));
            Assert.That(components.RootElement.GetProperty("Physics").GetRawText(), Is.EqualTo("{\"b\":\"2\"}"));
            Assert.That(wall.Appearance, Is.EqualTo("{\"k\":\"v\"}"));
            Assert.That(wall.Manifest, Is.EqualTo("[]"));
            Assert.That(wall.Carried, Is.EqualTo("{\"hands\":[]}"));
            Assert.That(rows[0].Appearance, Is.Null);
            Assert.That(rows[0].Carried, Is.Null);
        });
    }

    [Test]
    public void AGridOnlyStartedKeepsItsFlagAndSoDoesEachEntity()
    {
        // A grid made at runtime is started but never map-initialised, while what is built on it is initialised.
        var rows = DrydockPostgresImageStore.EntityRows(Image(
            Entity(1, DrydockCodecContext.InvalidReference, initialised: false),
            Entity(2, "1")));

        Assert.That(rows.Select(r => r.MapInitialized), Is.EqualTo(new[] { false, true }));
    }

    [Test]
    public void AReservedRowWithNoColumnIsRefused()
    {
        var image = Image(Entity(1, DrydockCodecContext.InvalidReference, rows: new[] { ("~unknown", "{}") }));

        var thrown = Assert.Throws<InvalidOperationException>(() => DrydockPostgresImageStore.EntityRows(image));
        Assert.That(thrown!.Message, Does.Contain("~unknown"));
    }

    [Test]
    public void TheTopologyMustBeATreeFromTheGridInLoadOrder()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() => DrydockPostgresImageStore.EntityRows(Image(Entity(1, "2"), Entity(2, "1"))),
                "the grid has a parent inside its image");
            Assert.Throws<InvalidOperationException>(() => DrydockPostgresImageStore.EntityRows(Image(
                Entity(1, DrydockCodecContext.InvalidReference), Entity(2, DrydockCodecContext.InvalidReference))),
                "a second entity with no parent");
            Assert.Throws<InvalidOperationException>(() => DrydockPostgresImageStore.EntityRows(Image(
                Entity(1, DrydockCodecContext.InvalidReference), Entity(3, "2"), Entity(2, "1"))),
                "a child before its parent");
            Assert.Throws<InvalidOperationException>(() => DrydockPostgresImageStore.EntityRows(new DrydockImage(9, new[] { Entity(1, DrydockCodecContext.InvalidReference) }, "{}", 0, 0)),
                "a grid id no entity carries");

            // Control: the same entities, parents first.
            Assert.DoesNotThrow(() => DrydockPostgresImageStore.EntityRows(Image(
                Entity(1, DrydockCodecContext.InvalidReference), Entity(2, "1"), Entity(3, "2"))));
        });
    }

    [Test]
    public void ATransformRowWithoutAParentIsRefused()
    {
        var entity = new DrydockImageEntity(1, "Grid", true, false, new Dictionary<string, string> { ["Transform"] = "{\"pos\":\"0,0\"}" });

        Assert.Throws<InvalidOperationException>(() => DrydockPostgresImageStore.EntityRows(Image(entity)));
    }

    [Test]
    public void TheTileCountIsTheNonEmptyTiles()
    {
        var tiles = new List<(Vector2i, Tile)> { (new Vector2i(0, 0), new Tile(40)), (new Vector2i(17, -3), new Tile(77)) };
        var table = DrydockNodeJson.Encode(DrydockTileTable.Write(16, tiles, type => type switch { 0 => "Space", 40 => "FloorSteel", _ => "Lattice" }))!.ToJsonString();

        Assert.That(DrydockPostgresImageStore.TileCount(table), Is.EqualTo(2));
    }
}
