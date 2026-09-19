using System;
using System.Threading.Tasks;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// Where an image rests between a store and a load. The unit is the whole image: the load allocates every entity
/// before it reads a row, so it needs the full set in hand, and a stored revision is immutable by then. A database
/// implementation assembles the image off the main thread and hands it back through the same call.
/// </summary>
public interface IDrydockRowStore
{
    /// <summary>Files <paramref name="image"/> under <paramref name="imageId"/>, replacing any image already there.</summary>
    Task Put(Guid imageId, DrydockImage image);

    /// <summary>The image filed under <paramref name="imageId"/>, or null when there is none.</summary>
    Task<DrydockImage?> Get(Guid imageId);
}
