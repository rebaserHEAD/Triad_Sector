using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;

namespace Content.Server._Triad.Drydock;

/// <summary>A stored image's key: the ship and the revision it was filed as. One image per revision at most.</summary>
public readonly record struct DrydockImageKey(Guid Ship, int Revision);

/// <summary>
/// What a load has to resolve before it starts, read without the components themselves: every prototype id the image's
/// entities name and every component the image carries, each once, sorted ordinally. An entity with no prototype names
/// none, and the <c>~</c> rows are not components.
/// </summary>
public sealed record DrydockImagePreflight(IReadOnlyList<string> PrototypeIds, IReadOnlyList<string> ComponentNames);

/// <summary>
/// Where <see cref="DrydockStore"/> keeps grid images, one per revision. The implementation is picked once, where
/// <c>ServerDbManager.Init</c> picks the engine, and read as <see cref="IServerDbManager.DrydockImages"/>: rows under
/// PostgreSQL (<see cref="DrydockPostgresImageStore"/>), memory under SQLite (<see cref="DrydockMemoryImageStore"/>).
///
/// <para><b>How a caller uses it.</b> Every member takes the <see cref="ServerDbContext"/> that
/// <see cref="IServerDbManager.RunTriadDbCommand{T}"/> handed the caller, and runs on that context's connection:</para>
/// <list type="number">
/// <item>A write (<see cref="Put"/>, <see cref="Delete"/>, <see cref="Copy"/>) runs inside the transaction the caller
/// has open on that context, and commits or rolls back with it. The caller opens the transaction, and the caller
/// commits it; the store does neither. A write with no transaction open is refused by the PostgreSQL store.</item>
/// <item>The ship row is taken first (<c>DrydockStore.LockShipRow</c>), before any revision row or image is written,
/// because a pin and a prune of the same ship take it in that order and only one order can hold without a
/// deadlock.</item>
/// <item>A write that names a revision (<see cref="Put"/>, the target of <see cref="Copy"/>) needs that revision row
/// flushed in the same transaction first, with <c>SaveChangesAsync</c>: the image row refers to it by a foreign key,
/// and an unflushed row is not there to refer to.</item>
/// <item>A read (<see cref="Get"/>, <see cref="Revisions"/>, <see cref="Has"/>, <see cref="Preflight"/>) joins the
/// transaction when one is open, and sees what it has written; with none it reads committed state.</item>
/// <item>Anything a write throws leaves the transaction to be rolled back, not committed: the caller lets it
/// propagate out of the <c>RunTriadDbCommand</c> lambda, and disposing the transaction without committing rolls it
/// back.</item>
/// </list>
///
/// <para><b>Retention lives in <see cref="DrydockStore"/>, not here.</b> Which images a prune deletes depends on
/// keep-N, the floor and the pins, and the pins are revision rows, so <c>DrydockStore.PruneImages</c> decides and
/// hands this store one set of revisions to <see cref="Delete"/>.</para>
///
/// <para><b>Under SQLite the store is not transactional</b>: <see cref="DrydockMemoryImageStore"/> ignores the context,
/// and what it writes stays whether the transaction commits or not. Its own summary says where that matters.</para>
/// </summary>
public interface IDrydockImageStore
{
    /// <summary>
    /// Files <paramref name="image"/> under <paramref name="key"/>. Runs in the caller's open transaction, after the ship
    /// row is taken and the revision row for <paramref name="key"/> is flushed. The PostgreSQL store returns only once the
    /// image as it holds it has been read back and compared with <paramref name="image"/>: a mismatch throws
    /// <see cref="DrydockImageMismatchException"/>, anything it cannot hold faithfully throws
    /// <see cref="InvalidOperationException"/>, and either way the caller rolls the transaction back. One image per
    /// revision: a second <see cref="Put"/> of the same key fails on the PostgreSQL store's unique key and replaces in the
    /// memory store.
    /// </summary>
    Task Put(ServerDbContext db, DrydockImageKey key, DrydockImage image, CancellationToken ct);

    /// <summary>
    /// The image filed under <paramref name="key"/>, whole, with its entities in load order, or null when there is none,
    /// which is also what a pruned revision reads. Needs no transaction. The PostgreSQL store reads it back as filed, up
    /// to what <see cref="DrydockImageComparer"/> leaves out; the memory store hands back the image it was given.
    /// </summary>
    Task<DrydockImage?> Get(ServerDbContext db, DrydockImageKey key, CancellationToken ct);

    /// <summary>The revisions of <paramref name="ship"/> that hold an image, newest first. Empty for an unknown ship. Needs no transaction.</summary>
    Task<List<int>> Revisions(ServerDbContext db, Guid ship, CancellationToken ct);

    /// <summary>
    /// Whether an image is filed under <paramref name="key"/>. A caller that acts on the answer inside a transaction takes
    /// the ship row first, so a prune of the same ship cannot delete the image between this read and the act.
    /// </summary>
    Task<bool> Has(ServerDbContext db, DrydockImageKey key, CancellationToken ct);

    /// <summary>
    /// The pre-flight of the image filed under <paramref name="key"/>, or null when there is none. A load cannot start on
    /// part of an image, because the engine allocates every entity before any row is read (<see cref="DrydockImage"/>),
    /// so a prototype id or component name that no longer resolves has to be found before it begins. Needs no
    /// transaction; the PostgreSQL store reads no component value.
    /// </summary>
    Task<DrydockImagePreflight?> Preflight(ServerDbContext db, DrydockImageKey key, CancellationToken ct);

    /// <summary>
    /// Deletes the images of the named revisions, in one statement where the store has statements, with their entities.
    /// The revision rows stay: history is never pruned. Runs in the caller's open transaction after the ship row is taken.
    /// A revision with no image is skipped, so the caller may pass a set it did not re-read.
    /// </summary>
    Task Delete(ServerDbContext db, Guid ship, IReadOnlyCollection<int> revisions, CancellationToken ct);

    /// <summary>
    /// Files a copy of the image under <paramref name="from"/> as <paramref name="to"/>, entities, tile table and counts
    /// alike, with no read of the rows into this process. Runs in the caller's open transaction, after the ship row is
    /// taken and the revision row for <paramref name="to"/> is flushed. Throws <see cref="InvalidOperationException"/>
    /// when nothing is filed under <paramref name="from"/>.
    /// </summary>
    Task Copy(ServerDbContext db, DrydockImageKey from, DrydockImageKey to, CancellationToken ct);
}
