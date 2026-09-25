using Robust.Shared.GameObjects;

namespace Content.Server._Triad.Drydock.Loader;

/// <summary>
/// Raised by ref at each entity a store writes, after its component, appearance and manifest rows, so a system that owns
/// state no component member holds can carry it: <see cref="Carry{T}"/> encodes the value at that instant into the
/// entity's <see cref="DrydockImageSystem.CarriedRow"/>, and the same system reads it back from
/// <see cref="GridRestoringEvent"/> or the directed <see cref="GridRestoredEvent"/>.
///
/// <para>A subscriber only reads: it never adds, removes or dirties anything, because the store may be refused or sliced
/// and a live ship must come out of it unchanged. Keys are <c>Owner.What</c>, unique per entity, compared ordinally. An
/// entity the walk leaves out, and everything under it, is never raised at.</para>
/// </summary>
/// <param name="Grid">The grid being stored.</param>
/// <param name="Store">The same object for every entity of one store and a new one per store, so an owner that needs the
/// whole grid read at one instant can key a snapshot on it.</param>
[ByRefEvent]
public readonly record struct GridStoringEvent(EntityUid Grid, DrydockStoreToken Store, DrydockCarrySink Sink)
{
    /// <summary>
    /// Carries <paramref name="value"/> for this entity under <paramref name="key"/>, round-tripped as <typeparamref name="T"/>
    /// by the serialization manager. A type holding an entity reference, coordinates, a component or a
    /// <see cref="System.TimeSpan"/> is refused into the store result's
    /// <see cref="DrydockImageStoreResult.UnwritableCarried"/>, which refuses the store; a duration travels as seconds.
    /// A key carried twice on one entity throws.
    /// </summary>
    public void Carry<T>(string key, T value) where T : notnull => Sink.Add(key, typeof(T), value);
}

/// <summary>One store's identity, held by its session and handed to every <see cref="GridStoringEvent"/> it raises.</summary>
public sealed class DrydockStoreToken
{
    internal DrydockStoreToken()
    {
    }
}
