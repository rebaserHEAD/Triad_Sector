using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>An <see cref="IDrydockRowStore"/> that keeps images in memory until the process ends. Its tasks are always complete.</summary>
public sealed class DrydockMemoryRowStore : IDrydockRowStore
{
    private readonly ConcurrentDictionary<Guid, DrydockImage> _images = new();

    public Task Put(Guid imageId, DrydockImage image)
    {
        _images[imageId] = image;
        return Task.CompletedTask;
    }

    public Task<DrydockImage?> Get(Guid imageId) =>
        Task.FromResult(_images.TryGetValue(imageId, out var image) ? image : null);
}
