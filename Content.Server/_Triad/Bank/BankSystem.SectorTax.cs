using Content.Server._Triad.Market;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared._Triad.CCVar;
using Robust.Shared.Configuration;

namespace Content.Server._NF.Bank;

public sealed partial class BankSystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    /// <summary>
    /// The four departments the sector purchase-tax pot divides between. Order matters only for
    /// the integer remainder, which goes to the first entry: the TFA operates the facilities the
    /// tax is collected at.
    /// </summary>
    private static readonly SectorBankAccount[] PotAccounts =
    [
        SectorBankAccount.Frontier,
        SectorBankAccount.TDF,
        SectorBankAccount.Medical,
        SectorBankAccount.Edison,
    ];

    /// <summary>
    /// Deposits a collected purchase tax into the sector pot. The pot is zero-storage rather than
    /// a sixth account: every collection splits evenly across the four departments on the spot, so
    /// no balance ever sits where nothing can spend it. The shares land in the in-game ledger as
    /// <see cref="LedgerEntryType.SectorTaxShare"/> with no standalone telemetry rows; the source
    /// transaction carries them as splits via <see cref="AddSectorTaxSplits"/> instead.
    ///
    /// <para>Call order at a purchase site: <see cref="AddSectorTaxSplits"/> on the record first
    /// (a record passed to a bank movement is enqueued for the writer thread there, so it must be
    /// complete going in), then the withdrawal, then this on success.</para>
    /// </summary>
    /// <param name="amount">The tax collected, in whole spesos. Already withdrawn from the payer.</param>
    public void DepositSectorTax(int amount)
    {
        if (amount <= 0)
            return;

        for (var i = 0; i < PotAccounts.Length; i++)
        {
            var cut = PotCut(amount, i);
            if (cut > 0)
                TrySectorDeposit(PotAccounts[i], cut, LedgerEntryType.SectorTaxShare, captureStandalone: false);
        }
    }

    /// <summary>
    /// Records the pot shares of <paramref name="amount"/> on a source transaction as
    /// <see cref="LedgerEntryType.SectorTaxShare"/> splits, one per department, mirroring exactly
    /// what <see cref="DepositSectorTax"/> will move. Per-department tax income is then the sum of
    /// these splits over a window.
    /// </summary>
    public void AddSectorTaxSplits(MarketRecord capture, int amount)
    {
        if (amount <= 0)
            return;

        for (var i = 0; i < PotAccounts.Length; i++)
        {
            var cut = PotCut(amount, i);
            if (cut > 0)
                capture.AddSplit(PotAccounts[i].ToString(), nameof(LedgerEntryType.SectorTaxShare), cut * 100L);
        }
    }

    /// <summary>
    /// One department's cut of a pot collection: an even integer share, remainder to index 0.
    /// The single source of the arithmetic, so splits and deposits cannot drift apart.
    /// </summary>
    private static int PotCut(int amount, int index)
    {
        var share = amount / PotAccounts.Length;
        if (index == 0)
            share += amount - share * PotAccounts.Length;
        return share;
    }

    /// <summary>
    /// The purchase tax due on a price at the current <c>triad.market.buy_tax</c> rate. Floored,
    /// so a tax is never charged on money that was not spent.
    /// </summary>
    public int GetSectorBuyTax(int price)
    {
        if (price <= 0)
            return 0;
        return (int)Math.Floor(price * _cfg.GetCVar(TriadCCVars.MarketBuyTax));
    }
}
