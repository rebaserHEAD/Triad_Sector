using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Market;

/// <summary>
/// Every database query the persistent market inventory owns. Same seam as
/// <see cref="MarketDataStore"/>: it goes through <see cref="IServerDbManager.RunTriadDbCommand{T}"/>
/// rather than adding methods to the manager itself, so an upstream pull conflicts on nothing.
///
/// <para>The table is state, not history: one row per (poi, kind, proto), rewritten wholesale per
/// POI on save. The transaction tables are the audit trail.</para>
/// </summary>
public sealed class MarketInventoryStore
{
    [Dependency] private readonly IServerDbManager _db = default!;

    /// <summary>
    /// Reads one POI's shelf. Detached rows; the caller validates prototype ids against the
    /// running game before trusting any of them.
    /// </summary>
    public Task<List<MarketInventory>> LoadInventory(string poiKey, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
            await db.MarketInventory
                .Where(i => i.PoiKey == poiKey)
                .AsNoTracking()
                .ToListAsync(token), ct);
    }

    /// <summary>
    /// Replaces one POI's shelf with the given rows. Delete+insert in one transaction: the rows
    /// are a snapshot, and half a snapshot landing would corrupt the shelf rather than age it.
    /// </summary>
    public Task SaveInventory(string poiKey, IReadOnlyList<MarketInventory> rows, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            await db.MarketInventory
                .Where(i => i.PoiKey == poiKey)
                .ExecuteDeleteAsync(token);

            db.MarketInventory.AddRange(rows);

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return rows.Count;
        }, ct);
    }
}
