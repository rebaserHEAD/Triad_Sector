using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The image store on PostgreSQL. Its tables are not in the schema yet, so it holds nothing: every read answers empty,
/// which is what the document path's readers (<see cref="DrydockStore.GetShipDetail"/>,
/// <see cref="DrydockStore.ListRetrievableRevisions"/>, a pin) need, and every write is refused.
/// </summary>
public sealed class DrydockPostgresImageStore : IDrydockImageStore
{
    private const string NoTables = "Drydock: the PostgreSQL image tables are not in the schema yet.";

    public Task Put(ServerDbContext db, DrydockImageKey key, DrydockImage image, CancellationToken ct) =>
        throw new NotSupportedException(NoTables);

    public Task<DrydockImage?> Get(ServerDbContext db, DrydockImageKey key, CancellationToken ct) =>
        Task.FromResult<DrydockImage?>(null);

    public Task<List<int>> Revisions(ServerDbContext db, Guid ship, CancellationToken ct) =>
        Task.FromResult(new List<int>());

    public Task<bool> Has(ServerDbContext db, DrydockImageKey key, CancellationToken ct) =>
        Task.FromResult(false);

    public Task Delete(ServerDbContext db, Guid ship, IReadOnlyCollection<int> revisions, CancellationToken ct) =>
        revisions.Count == 0 ? Task.CompletedTask : throw new NotSupportedException(NoTables);

    public Task Copy(ServerDbContext db, DrydockImageKey from, DrydockImageKey to, CancellationToken ct) =>
        throw new NotSupportedException(NoTables);
}
