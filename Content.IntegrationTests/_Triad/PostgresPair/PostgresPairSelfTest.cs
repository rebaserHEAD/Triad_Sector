#nullable enable
using Content.Server.Database;
using Content.Shared.CCVar;
using Microsoft.EntityFrameworkCore;

namespace Content.IntegrationTests._Triad.PostgresPair;

/// <summary>
/// The PostgreSQL test pair against itself, then the pool after it. The first test runs a pair on a scratch database and
/// checks the database goes with it; on a machine without PostgreSQL it is Skipped with the reason. The second runs an
/// ordinary SQLite pair straight after, in the same process, and has to pass: a PostgreSQL test that cannot run must not
/// leave the pool failing every later test, which is what a throw during pair creation does
/// (<c>Robust.UnitTesting/Pool/PoolManager.cs:303-306</c>, <c>:266-276</c>). Not parallel, because the assembly runs a
/// fixture's tests in parallel (<c>AssemblyInfo.cs:1</c>) and <see cref="OrderAttribute"/> holds only for tests that are not.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("Postgres")]
[TestOf(typeof(PostgresTestPair))]
public sealed class PostgresPairSelfTest
{
    [Test]
    [Order(1)]
    public async Task APairRunsOnItsOwnScratchDatabaseAndTheDatabaseGoesWithIt()
    {
        string database;
        await using (var postgres = await PostgresTestPair.Start())
        {
            database = postgres.Database;
            var pair = postgres.Pair;
            var cfg = pair.Server.CfgMan;
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var (current, ships) = await db.RunTriadDbCommand(async (context, token) =>
            {
                await context.Database.OpenConnectionAsync(token);
                try
                {
                    await using var command = context.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT current_database()";
                    var name = (string) (await command.ExecuteScalarAsync(token))!;
                    return (name, await context.DrydockShip.CountAsync(token));
                }
                finally
                {
                    await context.Database.CloseConnectionAsync();
                }
            }, default);

            Assert.Multiple(() =>
            {
                Assert.That(cfg.GetCVar(CCVars.DatabaseEngine), Is.EqualTo("postgres"));
                Assert.That(cfg.GetCVar(CCVars.DatabasePgHost), Is.EqualTo(TriadPostgres.Host));
                Assert.That(cfg.GetCVar(CCVars.DatabasePgUsername), Is.EqualTo(TriadPostgres.Role));
                Assert.That(cfg.GetCVar(CCVars.DatabasePgDatabase), Is.EqualTo(database));
                Assert.That(current, Is.EqualTo(database), "The server has to be connected to the pair's own database.");
                Assert.That(ships, Is.Zero, "The template's migrations have to be there, and nothing else.");
                Assert.That(TriadPostgres.Holds(database), Is.True, "The run has to hold the database while the pair lives.");
            });

            await pair.CleanReturnAsync();
        }

        var exists = await TriadPostgres.Exists(database);
        Assert.Multiple(() =>
        {
            Assert.That(TriadPostgres.Holds(database), Is.False, "Disposing the handle has to release the database.");
            Assert.That(exists, Is.False, "And drop it.");
        });
    }

    [Test]
    [Order(2)]
    public async Task ASqlitePairAfterItIsNotPoisoned()
    {
        Assert.That(TriadPostgres.Probed, Is.True,
            "The PostgreSQL test has to have run first in this process, or this test proves nothing about what it leaves behind.");

        await using var pair = await PoolManager.GetServerClient();
        Assert.That(pair.Server.CfgMan.GetCVar(CCVars.DatabaseEngine), Is.EqualTo("sqlite"), "A pooled pair after a PostgreSQL one is SQLite.");
        await pair.CleanReturnAsync();
    }
}
