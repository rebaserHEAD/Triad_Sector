#nullable enable

namespace Content.IntegrationTests;

public sealed partial class PoolSettings
{
    /// <summary>
    /// A pair whose server runs on its own scratch PostgreSQL database. Start one through
    /// <see cref="_Triad.PostgresPair.PostgresTestPair.Start"/>, which creates the database and sets
    /// <see cref="PostgresDatabase"/>. Such a pair is always new and never goes back to the pool, so a free pair the pool
    /// could hand to a SQLite request is never a PostgreSQL one (<c>Robust.UnitTesting/Pool/PoolManager.cs:220-250</c>),
    /// and recycling never has to change a pair's engine.
    /// </summary>
    public bool Postgres { get; init; }

    /// <summary>The scratch database a <see cref="Postgres"/> pair's server runs on, created before the pair is asked for.</summary>
    internal string? PostgresDatabase { get; init; }

    public override bool MustBeNew => base.MustBeNew || Postgres;

    public override bool MustNotBeReused => base.MustNotBeReused || Postgres;
}
