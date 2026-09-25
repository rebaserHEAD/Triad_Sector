#nullable enable
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Content.Server.Database;
using Content.Shared.CCVar;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Robust.UnitTesting;

namespace Content.IntegrationTests._Triad.PostgresPair;

/// <summary>
/// Every statement the PostgreSQL test pair issues, and the guards around them. No other code in the test assembly builds a
/// connection string for the pair or names one of its databases.
///
/// <list type="bullet">
/// <item>The host is the constant <see cref="Host"/>; the environment supplies only the port (<c>TRIAD_TEST_PG_PORT</c>).</item>
/// <item>The role is the constant <see cref="Role"/>, which exists only on this machine's server, so a tunnel to another
/// server on a local port rejects the login. It must have <c>CREATEDB</c> and must not be a superuser unless
/// <c>TRIAD_TEST_PG_ALLOW_SUPERUSER=1</c>.</item>
/// <item>Every database name matches <see cref="NamePattern"/> and is checked before it is quoted into a statement.</item>
/// <item>A drop is refused for any name this process did not create; the sweep iterates that set and never reads the
/// server's catalogue to decide what to drop.</item>
/// <item>The fixture's own connections carry no password, so Npgsql reads the pgpass file itself, and <c>PGPASSWORD</c>
/// is refused so that file is the only source. The one copy this class reads (<see cref="TriadPgPass"/>) goes to the
/// pair's <c>database.pg_password</c> and appears in no message.</item>
/// </list>
///
/// <para>Anything that can fail because of the machine runs here, in the test's own context, before a pair is asked for.
/// A throw during pair creation is kept by the pool and fails every later test in the run
/// (<c>RobustToolbox/Robust.UnitTesting/Pool/PoolManager.cs:303-306</c>, <c>:266-276</c>).</para>
/// </summary>
public static class TriadPostgres
{
    public const string Host = "localhost";
    public const string Role = "triad_test";

    private const string MaintenanceDatabase = "postgres";
    private const int DefaultPort = 5432;

    /// <summary>The project's stated minimum, PostgreSQL 17, as <c>server_version_num</c>.</summary>
    private const int MinimumServerVersion = 170000;

    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex NamePattern = new(@"^triad_test_[0-9a-f]{8}_[a-z0-9_]{1,32}\z", RegexOptions.CultureInvariant);

    /// <summary>Eight hex characters drawn once per test process, inside every database name this process creates.</summary>
    public static readonly string RunId = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>What a Skipped or failed report adds after its reason, so the reader can set the machine up.</summary>
    private static string Setup
    {
        get
        {
            var port = TryPort(out var value, out _) ? value.ToString(CultureInfo.InvariantCulture) : "<port>";
            return $"Setup: PostgreSQL 17 or newer listening on {Host}:{port}, a role {Role} with CREATEDB and without superuser, "
                + $"and a pgpass line {Host}:{port}:*:{Role}:<password> (the database field must be *, because each pair gets "
                + "its own database).";
        }
    }

    private static readonly ConcurrentDictionary<string, byte> Created = new();
    private static readonly ConcurrentDictionary<string, string> PendingPasswords = new();
    private static readonly SemaphoreSlim TemplateLock = new(1, 1);
    private static readonly Lazy<Task<string?>> Probe = new(ProbeCore, LazyThreadSafetyMode.ExecutionAndPublication);
    private static string? _template;
    private static int _pairSequence;

    /// <summary>Whether this process has probed for the pair. The pool self-test reads it to prove its order.</summary>
    public static bool Probed => Probe.IsValueCreated;

    /// <summary>The port every connection uses. Read only after the probe has passed, which checked it.</summary>
    private static int Port => TryPort(out var port, out var fault) ? port : throw new InvalidOperationException(fault);

    private static bool TryPort(out int port, out string? fault)
    {
        fault = null;
        var text = Environment.GetEnvironmentVariable("TRIAD_TEST_PG_PORT");
        if (string.IsNullOrEmpty(text))
        {
            port = DefaultPort;
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535)
            return true;

        fault = $"TRIAD_TEST_PG_PORT is not a port number: {text}";
        return false;
    }

    /// <summary>
    /// Stops the test when the pair cannot run here: Skipped with the reason, or failed when <c>TRIAD_TEST_PG_REQUIRED=1</c>,
    /// so a machine that is meant to have PostgreSQL cannot hide a whole class of tests behind a broken service.
    /// </summary>
    public static async Task RequireAvailable()
    {
        if (await Probe.Value is not { } reason)
            return;

        var message = $"PostgreSQL test pair unavailable: {reason}. {Setup}";
        if (Environment.GetEnvironmentVariable("TRIAD_TEST_PG_REQUIRED") == "1")
            Assert.Fail(message);

        Assert.Ignore(message);
    }

    /// <summary>
    /// The once-per-run check, in the order a missing piece is most likely: the credential source, a listener, then what
    /// the role may do. Null when the pair can run; otherwise the first reason it cannot.
    /// </summary>
    private static async Task<string?> ProbeCore()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PGPASSWORD")))
            return "PGPASSWORD is set, and the pair takes its password only from the pgpass file";

        if (!TryPort(out var port, out var portFault))
            return portFault;

        using (var tcp = new TcpClient())
        using (var timeout = new CancellationTokenSource(TcpTimeout))
        {
            try
            {
                await tcp.ConnectAsync(Host, port, timeout.Token);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                return $"nothing accepted a TCP connection on {Host}:{port} within {TcpTimeout.TotalSeconds:0} s ({e.GetType().Name})";
            }
        }

        if (MissingPassword(ProbeDatabaseName) is { } missing)
            return missing;

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString(MaintenanceDatabase));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT r.rolcreatedb, r.rolsuper, current_setting('server_version_num')::int "
                + "FROM pg_roles r WHERE r.rolname = current_user",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return $"the server has no pg_roles row for {Role}";

            var createDb = reader.GetBoolean(0);
            var superuser = reader.GetBoolean(1);
            var version = reader.GetInt32(2);

            if (!createDb)
                return $"the role {Role} lacks CREATEDB";

            if (superuser && Environment.GetEnvironmentVariable("TRIAD_TEST_PG_ALLOW_SUPERUSER") != "1")
                return $"the role {Role} is a superuser, which the pair refuses unless TRIAD_TEST_PG_ALLOW_SUPERUSER=1";

            if (version < MinimumServerVersion)
                return $"the server is version {version}, below {MinimumServerVersion}";
        }
        catch (NpgsqlException e)
        {
            var detail = e is PostgresException pg ? $"{pg.SqlState} {pg.MessageText}" : e.Message;
            return $"logging in as {Role} to {Host}:{port}/{MaintenanceDatabase} failed: {detail}";
        }

        return null;
    }

    /// <summary>A name no pair takes, shaped like theirs, so the pgpass lookup succeeds only through a <c>*</c> database field.</summary>
    private static string ProbeDatabaseName => Name("probe");

    private static string? MissingPassword(string database)
    {
        var (path, source) = TriadPgPass.Locate();
        if (!File.Exists(path))
            return $"there is no pgpass file at {path} ({source})";

        return TriadPgPass.Find(File.ReadLines(path), Host, Port, database, Role) == null
            ? $"no line in the pgpass file at {path} matches {Host}:{Port}:{database}:{Role}"
            : null;
    }

    /// <summary>
    /// A new database for one pair, cloned from the run's migrated template, and the password its server will log in with,
    /// held until <see cref="ApplyServerCvars"/> takes it.
    /// </summary>
    public static async Task<string> CreatePairDatabase()
    {
        var template = await EnsureTemplate();
        var name = Name($"p{Interlocked.Increment(ref _pairSequence)}");

        var (path, _) = TriadPgPass.Locate();
        var password = (File.Exists(path) ? TriadPgPass.Find(File.ReadLines(path), Host, Port, name, Role) : null)
            ?? throw new InvalidOperationException(
                $"PostgreSQL test pair: {MissingPassword(name) ?? "the pgpass line is gone"}, although the probe found one. {Setup}");

        await Execute($"CREATE DATABASE {Quote(name)} TEMPLATE {Quote(template)}");
        Created[name] = 0;
        PendingPasswords[name] = password;
        return name;
    }

    /// <summary>
    /// The run's template, created and migrated on first use through the same <c>MigrateAsync</c> the server runs
    /// (<c>ServerDbPostgres.cs:44-47</c>), so each pair's server finds its migration history already current. The fixture's
    /// connections are unpooled, so nothing is left connected to the template when a pair's <c>CREATE DATABASE ... TEMPLATE</c>
    /// needs it to have no sessions.
    /// </summary>
    private static async Task<string> EnsureTemplate()
    {
        await TemplateLock.WaitAsync();
        try
        {
            if (_template != null)
                return _template;

            var name = Name("template");
            await Execute($"CREATE DATABASE {Quote(name)}");
            Created[name] = 0;

            try
            {
                var options = new DbContextOptionsBuilder<PostgresServerDbContext>()
                    .UseNpgsql(ConnectionString(name))
                    .Options;

                await using var context = new PostgresServerDbContext(options);
                await context.Database.MigrateAsync();
            }
            catch
            {
                // The name is fixed per run, so a half-migrated template would refuse every later attempt to make one.
                await Drop(name);
                throw;
            }

            _template = name;
            return name;
        }
        finally
        {
            TemplateLock.Release();
        }
    }

    /// <summary>
    /// Points a pair's server at its database: the engine and all five connection cvars, set here and checked, because the
    /// defaults name a database <c>ss14</c> as <c>postgres</c> (<c>CCVars.Database.cs:41-54</c>). Throws only on a code fault:
    /// every environmental step ran in <see cref="CreatePairDatabase"/>.
    /// </summary>
    public static void ApplyServerCvars(RobustIntegrationTest.ServerIntegrationOptions options, string database)
    {
        CheckName(database);
        if (!Created.ContainsKey(database))
            throw new InvalidOperationException($"The pair's database {database} was not created by this run.");

        if (!PendingPasswords.TryRemove(database, out var password))
            throw new InvalidOperationException($"The pair's database {database} has already been handed to a server.");

        var cvars = options.CVarOverrides;
        cvars[CCVars.DatabaseEngine.Name] = "postgres";
        cvars[CCVars.DatabasePgHost.Name] = Host;
        cvars[CCVars.DatabasePgPort.Name] = Port.ToString(CultureInfo.InvariantCulture);
        cvars[CCVars.DatabasePgDatabase.Name] = database;
        cvars[CCVars.DatabasePgUsername.Name] = Role;
        cvars[CCVars.DatabasePgPassword.Name] = password;

        if (cvars[CCVars.DatabasePgHost.Name] != Host
            || cvars[CCVars.DatabasePgUsername.Name] != Role
            || !NamePattern.IsMatch(cvars[CCVars.DatabasePgDatabase.Name]))
        {
            throw new InvalidOperationException("The pair's database cvars do not hold the guard's values.");
        }
    }

    /// <summary>
    /// Drops a database this run created, whoever is still connected to it: a disposed server's pooled connections and its
    /// LISTEN connection can outlive it, which is what <c>WITH (FORCE)</c> is for.
    /// </summary>
    public static async Task Drop(string name)
    {
        CheckName(name);
        if (!Created.ContainsKey(name))
            throw new InvalidOperationException($"Refused to drop {name}: this run did not create it.");

        PendingPasswords.TryRemove(name, out _);
        await Execute($"DROP DATABASE IF EXISTS {Quote(name)} WITH (FORCE)");
        Created.TryRemove(name, out _);
    }

    /// <summary>Whether a database this run created still exists. Read-only; nothing decides a drop from it.</summary>
    public static async Task<bool> Exists(string name)
    {
        CheckName(name);
        await using var connection = new NpgsqlConnection(ConnectionString(MaintenanceDatabase));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", name);
        return (long) (await command.ExecuteScalarAsync())! > 0;
    }

    /// <summary>Whether <paramref name="name"/> is still in the set this run may drop.</summary>
    public static bool Holds(string name) => Created.ContainsKey(name);

    /// <summary>Drops everything this run created and has not dropped, the template last. Throws when anything is left behind.</summary>
    public static async Task Sweep()
    {
        var failures = new List<string>();
        foreach (var name in Created.Keys.OrderBy(n => n == _template ? 1 : 0).ToList())
        {
            try
            {
                await Drop(name);
            }
            catch (Exception e)
            {
                failures.Add($"{name}: {e.Message}");
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException($"PostgreSQL test pair: databases left behind: {string.Join("; ", failures)}");
    }

    private static string Name(string suffix)
    {
        var name = $"triad_test_{RunId}_{suffix}";
        CheckName(name);
        return name;
    }

    private static void CheckName(string name)
    {
        if (!NamePattern.IsMatch(name))
            throw new ArgumentException($"Not a PostgreSQL test pair database name: {name}", nameof(name));
    }

    /// <summary>An identifier cannot be a parameter, so a name is checked against the pattern (no quote can pass it) before it is quoted.</summary>
    private static string Quote(string name)
    {
        CheckName(name);
        return $"\"{name}\"";
    }

    private static string ConnectionString(string database)
    {
        if (database != MaintenanceDatabase)
            CheckName(database);

        return new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = database,
            Username = Role,
            Pooling = false,
        }.ConnectionString;
    }

    private static async Task Execute(string statement)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(MaintenanceDatabase));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(statement, connection);
        await command.ExecuteNonQueryAsync();
    }
}
