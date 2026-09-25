using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Loader;

namespace Content.Server._Triad.Drydock.Admin;

/// <summary>
/// Where <c>drydock_image_roundtrip</c> keeps the images it files, in memory until the process ends, by the command's
/// own image id. Not a store of record: the drydock files images through <see cref="IDrydockImageStore"/>. Its tasks are
/// always complete.
/// </summary>
public sealed class DrydockRoundTripImages
{
    private readonly ConcurrentDictionary<Guid, DrydockImage> _images = new();

    /// <summary>Files <paramref name="image"/> under <paramref name="imageId"/>, replacing any image already there.</summary>
    public Task Put(Guid imageId, DrydockImage image)
    {
        _images[imageId] = image;
        return Task.CompletedTask;
    }

    /// <summary>The image filed under <paramref name="imageId"/>, or null when there is none.</summary>
    public Task<DrydockImage?> Get(Guid imageId) =>
        Task.FromResult(_images.TryGetValue(imageId, out var image) ? image : null);
}
