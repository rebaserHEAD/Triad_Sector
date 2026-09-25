#nullable enable
using Content.IntegrationTests._Triad.PostgresPair;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Pair;

public sealed partial class TestPair
{
    /// <summary>
    /// Points a <see cref="PoolSettings.Postgres"/> pair's server at the database its settings name. Only writes and checks
    /// cvars: a throw here is kept by the pool and fails every later test in the run
    /// (<c>Robust.UnitTesting/Pool/PoolManager.cs:303-306</c>), so it is reserved for a code fault, and everything that can
    /// fail because of the machine has already run in <see cref="PostgresTestPair.Start"/>.
    /// </summary>
    private void ApplyTriadPostgres(RobustIntegrationTest.ServerIntegrationOptions options)
    {
        if (Settings is not PoolSettings { Postgres: true } settings)
            return;

        if (settings.PostgresDatabase is not { } database)
            throw new InvalidOperationException($"A Postgres pair is started through {nameof(PostgresTestPair)}.{nameof(PostgresTestPair.Start)}, which creates its database first.");

        TriadPostgres.ApplyServerCvars(options, database);
    }
}
