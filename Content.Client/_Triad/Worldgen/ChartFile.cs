// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Numerics;
using System.Text;
using Content.Shared._Triad.Worldgen;
using Robust.Shared.Map;

namespace Content.Client._Triad.Worldgen;

/// <summary>
///     One charted contact as it round-trips through the reconnect file: the wire facts the
///     client cannot re-derive. Color and outline are deliberately absent; both re-derive from
///     the recipe and seed on load, exactly as they do at receipt.
/// </summary>
public readonly record struct ChartEntry(int Id, int Version, Vector2 Position, int Seed, SensedProtoRecipe Recipe);

/// <summary>
///     Serialization for the client chart's reconnect file: a line-based, culture-invariant
///     text format, chosen over a serializer dependency because the payload is a private cache
///     (never authority, never shared) and the failure mode must be "discard and move on",
///     never "throw in a load path". Any line that does not parse is skipped; a version or
///     round mismatch discards the whole file.
///
///     Only Pristine-arm contacts are persisted. Nothing produces the Explicit arm today, and
///     when blob persistence does, its bumped Version makes the stale pristine entry re-send on
///     the first poll anyway, which is the merge rule working as designed.
/// </summary>
public static class ChartFile
{
    private const string Magic = "triadchart";
    private const int FormatVersion = 1;

    public static string Serialize(int roundId, IEnumerable<(MapId Map, IEnumerable<ChartEntry> Entries)> maps)
    {
        var sb = new StringBuilder();
        sb.Append(Magic).Append(' ').Append(FormatVersion).Append('\n');
        sb.Append("round ").Append(roundId.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (var (map, entries) in maps)
        {
            sb.Append("map ").Append(((int) map).ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (var e in entries)
            {
                sb.Append("c ");
                sb.Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(e.Version.ToString(CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(e.Position.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(e.Position.Y.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(e.Seed.ToString(CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(e.Recipe.Radius.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(e.Recipe.FloorPlacements.ToString(CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(e.Recipe.BlobDrawProb.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(e.Recipe.TilesetCount.ToString(CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(e.Recipe.ProtoId).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     False when the file is unusable outright (bad magic, bad version, or a round other
    ///     than <paramref name="expectedRoundId"/>). Individual bad lines are skipped, not fatal.
    /// </summary>
    public static bool TryDeserialize(string text, int expectedRoundId,
        out List<(MapId Map, List<ChartEntry> Entries)> maps)
    {
        maps = new List<(MapId, List<ChartEntry>)>();

        var lines = text.Split('\n');
        if (lines.Length < 2)
            return false;

        var header = lines[0].Split(' ');
        if (header.Length != 2 || header[0] != Magic
            || !int.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            || version != FormatVersion)
        {
            return false;
        }

        var round = lines[1].Split(' ');
        if (round.Length != 2 || round[0] != "round"
            || !int.TryParse(round[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var roundId)
            || roundId != expectedRoundId)
        {
            return false;
        }

        List<ChartEntry>? current = null;

        for (var i = 2; i < lines.Length; i++)
        {
            var parts = lines[i].Split(' ');

            if (parts.Length == 2 && parts[0] == "map")
            {
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId))
                {
                    current = null;
                    continue;
                }

                current = new List<ChartEntry>();
                maps.Add((new MapId(mapId), current));
                continue;
            }

            if (current is null || parts.Length != 11 || parts[0] != "c")
                continue;

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var entryVersion)
                || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)
                || !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var radius)
                || !int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var placements)
                || !float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var prob)
                || !int.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tilesetCount)
                || string.IsNullOrWhiteSpace(parts[10]))
            {
                continue;
            }

            var recipe = new SensedProtoRecipe(parts[10].Trim(), radius, placements, prob, tilesetCount);
            current.Add(new ChartEntry(id, entryVersion, new Vector2(x, y), seed, recipe));
        }

        return true;
    }
}
