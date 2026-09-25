#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Content.IntegrationTests._Triad.PostgresPair;

/// <summary>
/// Reads a password from the standard PostgreSQL password file as libpq does: one <c>host:port:database:user:password</c>
/// entry per line, a first-four field of exactly <c>*</c> matching anything, <c>\</c> escaping the next character (so
/// <c>\:</c> is a colon and <c>\*</c> a literal star), <c>#</c> opening a comment line, a trailing CR ignored, and the
/// first matching line winning.
///
/// <para>The pair's server takes its password only from <c>database.pg_password</c>
/// (<c>ServerDbManager.cs:1235,1244</c>), and Npgsql sends an empty one as empty rather than falling back to this file,
/// while Npgsql's own reader (<c>Npgsql.PgPassFile</c>) is internal. The value this returns is handed to that cvar and to
/// nothing else.</para>
/// </summary>
public static class TriadPgPass
{
    /// <summary>
    /// The file to read and where its path came from: <c>PGPASSFILE</c> when set, otherwise the per-user default, which is
    /// where Npgsql looks too (<c>%APPDATA%\postgresql\pgpass.conf</c> on Windows, <c>~/.pgpass</c> elsewhere).
    /// </summary>
    public static (string Path, string Source) Locate()
    {
        var env = Environment.GetEnvironmentVariable("PGPASSFILE");
        if (!string.IsNullOrEmpty(env))
            return (env, "PGPASSFILE");

        var path = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "postgresql", "pgpass.conf")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pgpass");
        return (path, "the default location");
    }

    /// <summary>The password of the first entry that matches, or null when none does.</summary>
    public static string? Find(IEnumerable<string> lines, string host, int port, string database, string user)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (Split(line) is not { } fields)
                continue;

            if (Matches(fields[0], host)
                && Matches(fields[1], portText)
                && Matches(fields[2], database)
                && Matches(fields[3], user))
            {
                return fields[4].Value;
            }
        }

        return null;
    }

    private static bool Matches((string Value, bool Wildcard) field, string value) =>
        field.Wildcard || field.Value == value;

    /// <summary>
    /// The five fields of one entry, unescaped, or null when the line has fewer than four unescaped colons. The password is
    /// the fifth field and ends at the next unescaped colon, as libpq reads it.
    /// </summary>
    private static (string Value, bool Wildcard)[]? Split(string line)
    {
        var fields = new List<(string, bool)>(5);
        var current = new StringBuilder();
        var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                current.Append(line[++i]);
                escaped = true;
                continue;
            }

            if (c == ':')
            {
                if (fields.Count == 4)
                    break;

                fields.Add(Close(current, ref escaped));
                continue;
            }

            current.Append(c);
        }

        if (fields.Count < 4)
            return null;

        fields.Add(Close(current, ref escaped));
        return fields.ToArray();
    }

    private static (string, bool) Close(StringBuilder current, ref bool escaped)
    {
        var value = current.ToString();
        var field = (value, !escaped && value == "*");
        current.Clear();
        escaped = false;
        return field;
    }
}
