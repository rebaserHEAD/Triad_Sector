using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The image store under SQLite, which has no image tables: images live in this process and nowhere else.
///
/// <para>It ignores the context, so nothing here joins the caller's transaction. <see cref="Put"/> replacing is harmless,
/// because a filing attempt that rolls back and retries reuses the same revision number. <see cref="Delete"/> and
/// <see cref="Copy"/> are not: in an attempt that later rolls back, their effect stays, and a SQLite pair can lose an
/// image its committed rows still list. The berth-race retry (<c>DrydockStore.FileRevision</c>) does not reach it,
/// because the image path flushes the revision and the berth seat before it puts, copies or prunes, and the berth's
/// unique index fails that flush; what remains is a failure of the final save or of the commit after a prune. If a
/// pooled test ever meets it, the fix is a journal of pending changes applied when the context's transaction commits.</para>
///
/// <para>Nothing survives a restart: a server on a SQLite file keeps its revision rows and loses every image.</para>
/// </summary>
public sealed class DrydockMemoryImageStore : IDrydockImageStore
{
    private readonly ConcurrentDictionary<DrydockImageKey, DrydockImage> _images = new();

    public Task Put(ServerDbContext db, DrydockImageKey key, DrydockImage image, CancellationToken ct)
    {
        _images[key] = image;
        return Task.CompletedTask;
    }

    public Task<DrydockImage?> Get(ServerDbContext db, DrydockImageKey key, CancellationToken ct) =>
        Task.FromResult(_images.TryGetValue(key, out var image) ? image : null);

    public Task<List<int>> Revisions(ServerDbContext db, Guid ship, CancellationToken ct) =>
        Task.FromResult(_images.Keys.Where(k => k.Ship == ship).Select(k => k.Revision).OrderByDescending(r => r).ToList());

    public Task<bool> Has(ServerDbContext db, DrydockImageKey key, CancellationToken ct) =>
        Task.FromResult(_images.ContainsKey(key));

    public Task Delete(ServerDbContext db, Guid ship, IReadOnlyCollection<int> revisions, CancellationToken ct)
    {
        foreach (var revision in revisions)
            _images.TryRemove(new DrydockImageKey(ship, revision), out _);

        return Task.CompletedTask;
    }

    public Task Copy(ServerDbContext db, DrydockImageKey from, DrydockImageKey to, CancellationToken ct)
    {
        if (!_images.TryGetValue(from, out var image))
            throw new InvalidOperationException($"Drydock: no image under {from} to copy to {to}.");

        // Nothing changes an image after the store builds it (it is handed out as read-only views), so sharing one is a copy.
        _images[to] = image;
        return Task.CompletedTask;
    }
}
