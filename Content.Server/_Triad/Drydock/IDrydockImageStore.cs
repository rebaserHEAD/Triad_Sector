using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;

namespace Content.Server._Triad.Drydock;

/// <summary>A stored image's key: the ship and the revision it was filed as.</summary>
public readonly record struct DrydockImageKey(Guid Ship, int Revision);

/// <summary>
/// Where <see cref="DrydockStore"/> keeps grid images, one per revision. Every member runs on the caller's context, so a
/// write joins the transaction the caller has open and commits or rolls back with the revision it belongs to. The
/// implementation is picked once, where <c>ServerDbManager.Init</c> picks the engine: PostgreSQL rows, or memory under
/// SQLite.
/// </summary>
public interface IDrydockImageStore
{
    /// <summary>
    /// Files <paramref name="image"/> under <paramref name="key"/>. The revision row must already be flushed in the open
    /// transaction, because the image row refers to it.
    /// </summary>
    Task Put(ServerDbContext db, DrydockImageKey key, DrydockImage image, CancellationToken ct);

    /// <summary>The image filed under <paramref name="key"/>, or null when there is none, which is also what a pruned revision reads.</summary>
    Task<DrydockImage?> Get(ServerDbContext db, DrydockImageKey key, CancellationToken ct);

    /// <summary>The revisions of <paramref name="ship"/> that still hold an image, newest first.</summary>
    Task<List<int>> Revisions(ServerDbContext db, Guid ship, CancellationToken ct);

    Task<bool> Has(ServerDbContext db, DrydockImageKey key, CancellationToken ct);

    /// <summary>Deletes the images of the named revisions, in one statement where the store has statements. The revision rows stay.</summary>
    Task Delete(ServerDbContext db, Guid ship, IReadOnlyCollection<int> revisions, CancellationToken ct);

    /// <summary>Files a copy of the image under <paramref name="from"/> as <paramref name="to"/>. Throws when there is none to copy.</summary>
    Task Copy(ServerDbContext db, DrydockImageKey from, DrydockImageKey to, CancellationToken ct);
}
