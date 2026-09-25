#nullable enable
using Content.IntegrationTests.Pair;

namespace Content.IntegrationTests._Triad.PostgresPair;

/// <summary>
/// A test pair on a scratch PostgreSQL database of its own. <see cref="Start"/> probes the machine, reporting the test
/// Skipped with the reason when the pair cannot run here, creates the database from the run's migrated template, and only
/// then asks the pool for a new pair. Return the pair with <c>CleanReturnAsync</c> as usual; disposing this drops the
/// database whether the test passed or not.
/// <code>
/// await using var postgres = await PostgresTestPair.Start();
/// var pair = postgres.Pair;
/// ...
/// await pair.CleanReturnAsync();
/// </code>
/// </summary>
public sealed class PostgresTestPair : IAsyncDisposable
{
    public TestPair Pair { get; }

    /// <summary>The database the pair's server runs on.</summary>
    public string Database { get; }

    private PostgresTestPair(TestPair pair, string database)
    {
        Pair = pair;
        Database = database;
    }

    public static async Task<PostgresTestPair> Start()
    {
        await TriadPostgres.RequireAvailable();
        var database = await TriadPostgres.CreatePairDatabase();
        try
        {
            var pair = await PoolManager.GetServerClient(new PoolSettings { Postgres = true, PostgresDatabase = database });
            return new PostgresTestPair(pair, database);
        }
        catch
        {
            await TriadPostgres.Drop(database);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Nothing after CleanReturnAsync, which kills a pair that must not be reused; a dirty return otherwise.
            await Pair.DisposeAsync();
        }
        finally
        {
            await TriadPostgres.Drop(Database);
        }
    }
}
