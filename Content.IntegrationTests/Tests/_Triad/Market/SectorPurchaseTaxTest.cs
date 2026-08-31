#nullable enable
using System.Linq;
using Content.Server._NF.Bank;
using Content.Server._Triad.Market;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Shared._Triad.CCVar;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo.Events;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Market;

/// <summary>
/// The money-model invariants of the persistent-economy rework: the buy-tax arithmetic, the pot
/// split (even four ways, integer remainder to the TFA), and the seller-paid ItemTax on a real
/// pallet sale.
/// </summary>
[TestOf(typeof(BankSystem))]
public sealed class SectorPurchaseTaxTest
{
    [Test]
    public async Task BuyTaxAndPotSplitArithmetic()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var bank = server.EntMan.System<BankSystem>();

        await server.WaitPost(() => server.CfgMan.SetCVar(TriadCCVars.MarketBuyTax, 0.15f));

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                // Floored, so a tax is never charged on money that was not spent.
                Assert.That(bank.GetSectorBuyTax(1000), Is.EqualTo(150));
                Assert.That(bank.GetSectorBuyTax(999), Is.EqualTo(149));
                Assert.That(bank.GetSectorBuyTax(1), Is.EqualTo(0));
                Assert.That(bank.GetSectorBuyTax(0), Is.EqualTo(0));
            });

            // The pot splits evenly four ways with the integer remainder to the TFA (Frontier),
            // and the splits mirror the deposits exactly - they come from the same arithmetic.
            var record = new MarketRecord();
            bank.AddSectorTaxSplits(record, 103);

            Assert.Multiple(() =>
            {
                Assert.That(record.Splits, Has.Count.EqualTo(4));
                Assert.That(record.Splits.All(s => s.EntryType == "SectorTaxShare"), Is.True);
                Assert.That(record.Splits.Sum(s => s.Amount), Is.EqualTo(103 * 100L),
                    "the split sum is the whole tax; nothing rounds away");
                Assert.That(record.Splits.Single(s => s.Account == "Frontier").Amount, Is.EqualTo(28 * 100L),
                    "the remainder lands on the TFA");
                Assert.That(record.Splits.Where(s => s.Account != "Frontier").All(s => s.Amount == 25 * 100L),
                    Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Sells a taxed weapon at a pallet and asserts the ItemTax is deducted from the payout
    /// (seller-paid) and attached as a direct department split, with the seller's share closing
    /// the sum. Uses the same direct-invoke rig as <see cref="PalletSaleCaptureTest"/>; the test
    /// map has no owning station, so no market modifier applies.
    /// </summary>
    [Test]
    public async Task ItemTaxIsSellerPaidAndSplitsClose()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;

        await server.WaitPost(() => server.CfgMan.SetCVar(TriadCCVars.MarketDataEnabled, true));

        var market = server.ResolveDependency<IMarketDataManager>();
        var db = server.ResolveDependency<IServerDbManager>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var mapSys = entMan.System<SharedMapSystem>();

        long baseline = 0;
        await db.RunTriadDbCommand(async (ctx, ct) =>
        {
            baseline = await ctx.MarketTransaction.MaxAsync(t => (long?)t.Id, ct) ?? 0;
        }, default);

        var testMap = await pair.CreateTestMap();
        var grid = testMap.Grid.Owner;

        EntityUid console = default;
        EntityUid gunUid = default;

        await server.WaitPost(() =>
        {
            mapSys.SetTile(grid, testMap.Grid.Comp, new Vector2i(0, 0), new Tile(1));
            mapSys.SetTile(grid, testMap.Grid.Comp, new Vector2i(1, 0), new Tile(1));

            var palletCoords = new EntityCoordinates(grid, 0.5f, 0.5f);
            var pallet = entMan.SpawnAtPosition("CargoPalletSell", palletCoords);
            if (!entMan.GetComponent<TransformComponent>(pallet).Anchored)
                xformSys.AnchorEntity(pallet);

            console = entMan.SpawnAtPosition("ComputerPalletConsole", new EntityCoordinates(grid, 1.5f, 0.5f));
            if (!entMan.GetComponent<TransformComponent>(console).Anchored)
                xformSys.AnchorEntity(console);

            // ItemTax TDF 0.2 rides on the rifle base; no Contraband, so the tax is charged.
            gunUid = entMan.SpawnAtPosition("WeaponRifleAk", palletCoords);
        });

        await pair.RunTicksSync(5);

        var cargo = entMan.System<CargoSystem>();
        await server.WaitPost(() =>
        {
            var handler = typeof(CargoSystem).GetMethod("OnPalletSale",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(handler, Is.Not.Null, "OnPalletSale moved or was renamed; update this test");

            var ev = new CargoPalletSellMessage { Actor = console };
            handler!.Invoke(cargo, [console, entMan.GetComponent<CargoPalletConsoleComponent>(console), ev]);

            Assert.That(entMan.Deleted(gunUid), Is.True, "the rifle was sold");
        });

        await pair.RunTicksSync(5);
        await market.Flush();

        await db.RunTriadDbCommand(async (ctx, ct) =>
        {
            var tx = await ctx.MarketTransaction
                .Include(t => t.Splits)
                .Where(t => t.Id > baseline && t.Kind == MarketTransactionKind.PalletSale)
                .SingleAsync(ct);

            var tdf = tx.Splits.Single(s => s.Account == "TDF");
            var player = tx.Splits.Single(s => s.Account == "Player");

            Assert.Multiple(() =>
            {
                Assert.That(tx.Gross, Is.GreaterThan(0));
                Assert.That(tx.Tax, Is.GreaterThan(0), "the weapon tax was charged");

                // Seller-paid: the payout is the appraisal minus the tax, up to the integer cast
                // on the cash stack plus per-column rounding (at most a speso of jitter).
                Assert.That(tx.Gross - tx.Tax - tx.Net, Is.InRange(-100, 200),
                    "the tax came out of the seller's payout");

                Assert.That(tdf.EntryType, Is.EqualTo("TSFMCSales"));
                Assert.That(tdf.Amount, Is.EqualTo(tx.Tax), "the weapon tax goes direct to TDF");
                Assert.That(player.Amount, Is.EqualTo(tx.Net), "and the seller's share closes the sum");
            });
        }, default);

        await pair.CleanReturnAsync();
    }
}
