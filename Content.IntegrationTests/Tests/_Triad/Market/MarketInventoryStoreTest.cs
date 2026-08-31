#nullable enable
using System;
using System.Linq;
using Content.Server._Triad.Market;
using Content.Server.Database;

namespace Content.IntegrationTests.Tests._Triad.Market;

/// <summary>
/// Round-trips persistent market inventory through the real database layer.
///
/// <para>The interesting failures are the same family the transaction store guards against: kinds
/// must persist as names, x100 fixed-point quantities must survive unscaled, and a save must be a
/// wholesale replacement per POI - the table is state, not history, and stale rows surviving a
/// second save would age the shelf instead of replacing it.</para>
///
/// <para>Every key here is test-unique because the pooled pair shares one database across tests.</para>
/// </summary>
[TestOf(typeof(MarketInventoryStore))]
public sealed class MarketInventoryStoreTest
{
    private const string RoundTripKey = "test-inv-round-trip";
    private const string ReplaceKey = "test-inv-replace";

    [Test]
    public async Task InventoryRoundTripsAllKindsUnscaled()
    {
        await using var pair = await PoolManager.GetServerClient();
        var store = pair.Server.ResolveDependency<MarketInventoryStore>();

        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        await store.SaveInventory(RoundTripKey, new[]
        {
            new MarketInventory
            {
                PoiKey = RoundTripKey,
                Kind = MarketInventoryKind.Item,
                ProtoId = "SheetSteel1",
                StackProto = "Steel",
                Quantity = 40 * 100,
                UnitPrice = 750, // 7.50 in minor units
                UpdatedAt = now,
            },
            new MarketInventory
            {
                PoiKey = RoundTripKey,
                Kind = MarketInventoryKind.Reagent,
                ProtoId = "Water",
                Quantity = 73_450, // 734.5u of a pool - the fraction is the point
                UnitPrice = 0,
                UpdatedAt = now,
            },
            new MarketInventory
            {
                PoiKey = RoundTripKey,
                Kind = MarketInventoryKind.Gas,
                ProtoId = "Oxygen",
                Quantity = 123_456,
                UnitPrice = 0,
                UpdatedAt = now,
            },
            new MarketInventory
            {
                PoiKey = RoundTripKey,
                Kind = MarketInventoryKind.Material,
                ProtoId = "Steel",
                Quantity = 55, // sub-stack remainder
                UnitPrice = 0,
                UpdatedAt = now,
            },
        });

        var rows = await store.LoadInventory(RoundTripKey);

        Assert.That(rows, Has.Count.EqualTo(4));
        Assert.Multiple(() =>
        {
            var item = rows.Single(r => r.Kind == MarketInventoryKind.Item);
            Assert.That(item.ProtoId, Is.EqualTo("SheetSteel1"));
            Assert.That(item.StackProto, Is.EqualTo("Steel"));
            Assert.That(item.Quantity, Is.EqualTo(4000), "x100 fixed-point survives the write unscaled");
            Assert.That(item.UnitPrice, Is.EqualTo(750));

            // Kinds come back as their names; a "1" here means the column type changed and the
            // next mid-list enum insertion silently relabels every stored shelf.
            Assert.That(rows.Single(r => r.ProtoId == "Water").Kind, Is.EqualTo(MarketInventoryKind.Reagent));
            Assert.That(rows.Single(r => r.ProtoId == "Oxygen").Kind, Is.EqualTo(MarketInventoryKind.Gas));

            var material = rows.Single(r => r.Kind == MarketInventoryKind.Material);
            Assert.That(material.Quantity, Is.EqualTo(55), "pool remainders persist exactly");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SaveReplacesTheShelfWholesale()
    {
        await using var pair = await PoolManager.GetServerClient();
        var store = pair.Server.ResolveDependency<MarketInventoryStore>();

        var now = DateTime.UtcNow;

        MarketInventory Row(string proto, long quantity) => new()
        {
            PoiKey = ReplaceKey,
            Kind = MarketInventoryKind.Item,
            ProtoId = proto,
            Quantity = quantity,
            UnitPrice = 100,
            UpdatedAt = now,
        };

        await store.SaveInventory(ReplaceKey, new[] { Row("SheetSteel1", 1000), Row("SheetGlass1", 2000) });
        await store.SaveInventory(ReplaceKey, new[] { Row("SheetSteel1", 500) });

        var rows = await store.LoadInventory(ReplaceKey);

        Assert.Multiple(() =>
        {
            // Delete+insert, not merge: the second snapshot is the whole truth.
            Assert.That(rows, Has.Count.EqualTo(1), "the save replaced the shelf rather than merging into it");
            Assert.That(rows[0].ProtoId, Is.EqualTo("SheetSteel1"));
            Assert.That(rows[0].Quantity, Is.EqualTo(500));
        });

        await pair.CleanReturnAsync();
    }
}
