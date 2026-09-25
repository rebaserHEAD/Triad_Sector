using System;
using System.Buffers;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock.Codec;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Robust.Shared.Map;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The image store on PostgreSQL: one <c>drydock_image</c> row per revision and one <c>drydock_entity</c> row per
/// entity.
///
/// <list type="bullet">
/// <item>Writes (<see cref="Put"/>, <see cref="Delete"/>, <see cref="Copy"/>) run only inside the caller's transaction,
/// on its own connection, so the binary <c>COPY</c> of entity rows commits or rolls back with the revision it belongs
/// to. A <c>COPY</c> that is not completed leaves no rows.</item>
/// <item><see cref="Put"/> reads the image back inside that transaction and compares it with
/// <see cref="DrydockImageComparer"/>, so a store the database changed is refused before it commits.</item>
/// <item>An entity's <c>components</c> is one JSON object keyed by component name; <c>~appearance</c>,
/// <c>~manifest</c> and <c>~carried</c> have columns of their own; a reserved row the schema has no column for is
/// refused rather than dropped.</item>
/// <item><c>parent_id</c> is read from the entity's Transform row, and the store refuses an image whose topology is not
/// a tree from the grid in load order: null only for the grid, every other parent an earlier entity of the image.</item>
/// <item>Whether an entity is map-initialised is kept per entity in <c>map_initialized</c>: a grid made at runtime (a
/// floor tile placed in space, a split) is started but never map-initialised (<c>SharedMapSystem.Grid.cs:63-65</c>),
/// and the load hands each entity's flag on (<c>DrydockLoadSession.cs:114</c>). Pause is not kept: the target map
/// decides it on a load, so a read gives false.</item>
/// </list>
///
/// <para><b>How a write here is built</b>, which <see cref="Put"/>, <see cref="Delete"/> and <see cref="Copy"/> all
/// follow and an operation added later follows too:</para>
/// <list type="number">
/// <item>Take the caller's connection and open transaction with <see cref="Enlisted"/>, which refuses a write with no
/// transaction. Every statement runs on that connection with that transaction; nothing here opens, commits or rolls
/// back a transaction, and nothing opens a second connection, because a second connection is outside the
/// transaction.</item>
/// <item>Change rows with set statements on the database side (<c>INSERT ... SELECT</c>, <c>DELETE ... ANY</c>, a
/// binary <c>COPY</c>), and read rows into this process only to rebuild an image for a caller.</item>
/// <item>Throw on anything the tables cannot hold as asked, before the first statement where that can be known, and
/// otherwise let a failed statement's exception propagate: PostgreSQL has aborted the transaction by then, and the
/// caller's only move is to roll back.</item>
/// <item>A write that changes what an entity holds reads the result back on the same transaction and compares it
/// (<see cref="DrydockImageComparer"/>), as <see cref="Put"/> does, so a change the database did not keep as given is
/// refused before it commits.</item>
/// </list>
/// </summary>
public sealed class DrydockPostgresImageStore : IDrydockImageStore
{
    private const string TransformRow = "Transform";
    private const string ParentKey = "parent";

    internal const string CopyEntitiesStatement =
        "COPY drydock_entity (image_id, entity_id, parent_id, prototype_id, map_initialized, components, component_names, appearance, manifest, carried) "
        + "FROM STDIN (FORMAT BINARY)";

    /// <summary>One <c>drydock_entity</c> row as written.</summary>
    internal sealed record EntityRow(
        long EntityId,
        long? ParentId,
        string? PrototypeId,
        bool MapInitialized,
        string Components,
        string[] ComponentNames,
        string? Appearance,
        string? Manifest,
        string? Carried);

    /// <summary>
    /// Files an image in four steps on the caller's transaction: checks every entity can be stored (<see cref="EntityRows"/>,
    /// which throws before any statement), inserts the image row, copies the entity rows in by binary <c>COPY</c>, then
    /// reads the image back and compares it. The image row's foreign key needs the revision row flushed before this is
    /// called; without it the insert fails with <c>23503</c>. On return the image is in the transaction and nothing is
    /// committed: the caller commits.
    /// </summary>
    public async Task Put(ServerDbContext db, DrydockImageKey key, DrydockImage image, CancellationToken ct)
    {
        var (connection, transaction) = Enlisted(db);
        var rows = EntityRows(image);
        var imageId = await InsertImage(connection, transaction, key, image, ct);
        await CopyEntities(connection, imageId, rows, complete: true, ct);

        var back = await Read(connection, transaction, imageId, ct)
            ?? throw new InvalidOperationException($"Drydock: image {imageId} was not there to read back inside its own transaction.");

        DrydockImageComparer.AssertSame(image, back);
    }

    /// <summary>
    /// Two reads on the caller's connection, opening it for the read when the caller has not: the image row by its key,
    /// then every entity row in id order (<see cref="Read"/>). Inside a transaction it sees that transaction's writes.
    /// </summary>
    public async Task<DrydockImage?> Get(ServerDbContext db, DrydockImageKey key, CancellationToken ct)
    {
        return await WithConnection(db, async (connection, transaction) =>
        {
            var imageId = await ImageId(connection, transaction, key, ct);
            return imageId == null ? null : await Read(connection, transaction, imageId.Value, ct);
        }, ct);
    }

    /// <summary>One read of the image rows' keys for the ship, touching no entity row and no JSON.</summary>
    public async Task<List<int>> Revisions(ServerDbContext db, Guid ship, CancellationToken ct)
    {
        return await WithConnection(db, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT revision FROM drydock_image WHERE ship_guid = @ship ORDER BY revision DESC", connection, transaction);
            command.Parameters.AddWithValue("ship", ship);

            var revisions = new List<int>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                revisions.Add(reader.GetInt32(0));

            return revisions;
        }, ct);
    }

    /// <summary>One read of the image row's key. The answer holds inside the caller's transaction only while it holds the ship row.</summary>
    public async Task<bool> Has(ServerDbContext db, DrydockImageKey key, CancellationToken ct)
    {
        return await WithConnection(db, async (connection, transaction) =>
            await ImageId(connection, transaction, key, ct) != null, ct);
    }

    /// <summary>
    /// One <c>DELETE</c> of the image rows for the named revisions on the caller's transaction; the entity rows go with
    /// them by the foreign key's <c>ON DELETE CASCADE</c>, and the revision rows stay. An empty set issues no statement.
    /// </summary>
    public async Task Delete(ServerDbContext db, Guid ship, IReadOnlyCollection<int> revisions, CancellationToken ct)
    {
        if (revisions.Count == 0)
            return;

        var (connection, transaction) = Enlisted(db);

        // The entity rows go with their image by the foreign key's cascade; the revision rows stay.
        await using var command = new NpgsqlCommand(
            "DELETE FROM drydock_image WHERE ship_guid = @ship AND revision = ANY(@revisions)", connection, transaction);
        command.Parameters.AddWithValue("ship", ship);
        command.Parameters.AddWithValue("revisions", revisions.ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Three statements on the caller's transaction: the source image's id, an <c>INSERT ... SELECT</c> of its row under
    /// the new key, and an <c>INSERT ... SELECT</c> of its entity rows under the new id, so no row passes through this
    /// process and entity ids are kept. The new key's revision row must be flushed first, as for <see cref="Put"/>. No
    /// read-back: nothing is re-encoded, so there is nothing a compare could catch.
    /// </summary>
    public async Task Copy(ServerDbContext db, DrydockImageKey from, DrydockImageKey to, CancellationToken ct)
    {
        var (connection, transaction) = Enlisted(db);

        var source = await ImageId(connection, transaction, from, ct)
            ?? throw new InvalidOperationException($"Drydock: no image under {from} to copy to {to}.");

        long copy;
        await using (var image = new NpgsqlCommand(
            "INSERT INTO drydock_image (ship_guid, revision, grid_entity_id, codec_version, entity_count, tile_count, tiles, left_out) "
            + "SELECT @ship, @revision, grid_entity_id, codec_version, entity_count, tile_count, tiles, left_out "
            + "FROM drydock_image WHERE image_id = @source RETURNING image_id",
            connection, transaction))
        {
            image.Parameters.AddWithValue("ship", to.Ship);
            image.Parameters.AddWithValue("revision", to.Revision);
            image.Parameters.AddWithValue("source", source);
            copy = (long) (await image.ExecuteScalarAsync(ct))!;
        }

        await using var entities = new NpgsqlCommand(
            "INSERT INTO drydock_entity (image_id, entity_id, parent_id, prototype_id, map_initialized, components, component_names, appearance, manifest, carried) "
            + "SELECT @copy, entity_id, parent_id, prototype_id, map_initialized, components, component_names, appearance, manifest, carried "
            + "FROM drydock_entity WHERE image_id = @source",
            connection, transaction);
        entities.Parameters.AddWithValue("copy", copy);
        entities.Parameters.AddWithValue("source", source);
        await entities.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The image's own row, and the id its entity rows hang off.</summary>
    internal static async Task<long> InsertImage(NpgsqlConnection connection, NpgsqlTransaction transaction, DrydockImageKey key, DrydockImage image, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO drydock_image (ship_guid, revision, grid_entity_id, codec_version, entity_count, tile_count, tiles, left_out) "
            + "VALUES (@ship, @revision, @grid, @codec, @count, @tileCount, @tiles, @leftOut) RETURNING image_id",
            connection, transaction);
        insert.Parameters.AddWithValue("ship", key.Ship);
        insert.Parameters.AddWithValue("revision", key.Revision);
        insert.Parameters.AddWithValue("grid", image.GridId);
        insert.Parameters.AddWithValue("codec", DrydockFormat.Current);
        insert.Parameters.AddWithValue("count", image.Entities.Count);
        insert.Parameters.AddWithValue("tileCount", TileCount(image.Tiles));
        insert.Parameters.AddWithValue("tiles", NpgsqlDbType.Jsonb, image.Tiles);
        insert.Parameters.AddWithValue("leftOut", NpgsqlDbType.Jsonb, LeftOut(image));
        return (long) (await insert.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Writes entity rows by binary <c>COPY</c> on <paramref name="connection"/>, which joins whatever transaction is open
    /// there. With <paramref name="complete"/> false the importer is disposed without <c>Complete</c>, which cancels the
    /// copy; only a test asks for that, as the control that such an importer leaves nothing.
    /// </summary>
    internal static async Task<ulong> CopyEntities(NpgsqlConnection connection, long imageId, IReadOnlyList<EntityRow> rows, bool complete, CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync(CopyEntitiesStatement, ct);
        foreach (var row in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(imageId, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(row.EntityId, NpgsqlDbType.Bigint, ct);

            if (row.ParentId is { } parent)
                await writer.WriteAsync(parent, NpgsqlDbType.Bigint, ct);
            else
                await writer.WriteNullAsync(ct);

            await WriteNullable(writer, row.PrototypeId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(row.MapInitialized, NpgsqlDbType.Boolean, ct);
            await writer.WriteAsync(row.Components, NpgsqlDbType.Jsonb, ct);
            await writer.WriteAsync(row.ComponentNames, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
            await WriteNullable(writer, row.Appearance, NpgsqlDbType.Jsonb, ct);
            await WriteNullable(writer, row.Manifest, NpgsqlDbType.Jsonb, ct);
            await WriteNullable(writer, row.Carried, NpgsqlDbType.Jsonb, ct);
        }

        return complete ? await writer.CompleteAsync(ct) : 0;
    }

    private static async Task WriteNullable(NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken ct)
    {
        if (value == null)
            await writer.WriteNullAsync(ct);
        else
            await writer.WriteAsync(value, type, ct);
    }

    /// <summary>The rows <paramref name="image"/> becomes, or a refusal naming the entity that cannot be stored as it is.</summary>
    internal static List<EntityRow> EntityRows(DrydockImage image)
    {
        var rows = new List<EntityRow>(image.Entities.Count);
        var seen = new HashSet<long>();
        var gridSeen = false;

        foreach (var entity in image.Entities)
        {
            var name = $"entity {entity.Id} ({entity.Prototype ?? "no prototype"})";
            var parent = ParentOf(entity, name);
            var isGrid = entity.Id == image.GridId;
            if (isGrid != (parent == null))
            {
                throw new InvalidOperationException(isGrid
                    ? $"Drydock: the grid, {name}, has a parent inside its own image."
                    : $"Drydock: {name} has no parent inside the image, and only the grid may have none.");
            }

            if (parent is { } p && !seen.Contains(p))
                throw new InvalidOperationException($"Drydock: {name} comes before its parent {p}, and the load takes parents first.");

            if (!seen.Add(entity.Id))
                throw new InvalidOperationException($"Drydock: {name} appears twice in the image.");

            gridSeen |= isGrid;

            string? appearance = null, manifest = null, carried = null;
            var components = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (row, value) in entity.Rows)
            {
                switch (row)
                {
                    case DrydockImageSystem.AppearanceRow:
                        appearance = value;
                        break;
                    case DrydockCodec.ManifestRow:
                        manifest = value;
                        break;
                    case DrydockImageSystem.CarriedRow:
                        carried = value;
                        break;
                    default:
                        if (row.StartsWith('~'))
                            throw new InvalidOperationException($"Drydock: {name} carries the reserved row {row}, which the image tables have no column for.");

                        components[row] = value;
                        break;
                }
            }

            rows.Add(new EntityRow(entity.Id, parent, entity.Prototype, entity.MapInitialized, ComponentsObject(components), components.Keys.ToArray(),
                appearance, manifest, carried));
        }

        if (!gridSeen)
            throw new InvalidOperationException($"Drydock: the image's grid, entity {image.GridId}, is not among its entities.");

        return rows;
    }

    /// <summary>The entity's parent inside the image, from its Transform row: null for a reference the codec wrote as leaving the image.</summary>
    private static long? ParentOf(DrydockImageEntity entity, string name)
    {
        if (!entity.Rows.TryGetValue(TransformRow, out var transform))
            throw new InvalidOperationException($"Drydock: {name} has no Transform row.");

        using var document = JsonDocument.Parse(transform);
        if (!document.RootElement.TryGetProperty(ParentKey, out var parent) || parent.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Drydock: {name}'s Transform row has no parent reference.");

        var text = parent.GetString()!;
        if (text == DrydockCodecContext.InvalidReference)
            return null;

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new InvalidOperationException($"Drydock: {name}'s parent reference is '{text}', which is neither an id nor '{DrydockCodecContext.InvalidReference}'.");
    }

    /// <summary>One JSON object of the component rows, each value written as it is after checking it is JSON.</summary>
    private static string ComponentsObject(SortedDictionary<string, string> components)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in components)
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The non-empty tiles in the table, counted by the tile table's own reader, which also refuses a table it did not
    /// write. Id 0 is always the empty tile (<see cref="DrydockTileTable"/>), so every other name counts.
    /// </summary>
    internal static int TileCount(string tiles)
    {
        if (DrydockNodeJson.Decode(JsonNode.Parse(tiles)) is not MappingDataNode table
            || !table.TryGet<MappingDataNode>(DrydockTileTable.TileMapKey, out var tileMap)
            || !tileMap.TryGet<ValueDataNode>("0", out var empty))
        {
            throw new InvalidOperationException("Drydock: the image's tile table has no tilemap with the empty tile at id 0.");
        }

        return DrydockTileTable.Read(table, definition => definition == empty.Value ? Tile.Empty.TypeId : Tile.Empty.TypeId + 1).Count;
    }

    /// <summary>What left the image, as counts. Only the unsaved count travels on an image today.</summary>
    private static string LeftOut(DrydockImage image) =>
        $"{{\"unsaved\":{image.Unsaved.ToString(CultureInfo.InvariantCulture)}}}";

    private static async Task<long?> ImageId(NpgsqlConnection connection, NpgsqlTransaction? transaction, DrydockImageKey key, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT image_id FROM drydock_image WHERE ship_guid = @ship AND revision = @revision", connection, transaction);
        command.Parameters.AddWithValue("ship", key.Ship);
        command.Parameters.AddWithValue("revision", key.Revision);
        return await command.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    /// <summary>One image rebuilt from its rows: the header, then every entity in id order, which is load order.</summary>
    internal static async Task<DrydockImage?> Read(NpgsqlConnection connection, NpgsqlTransaction? transaction, long imageId, CancellationToken ct)
    {
        long gridId;
        string tiles;
        int unsaved;
        await using (var header = new NpgsqlCommand(
            "SELECT grid_entity_id, tiles::text, left_out::text FROM drydock_image WHERE image_id = @id", connection, transaction))
        {
            header.Parameters.AddWithValue("id", imageId);
            await using var reader = await header.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            gridId = reader.GetInt64(0);
            tiles = reader.GetString(1);
            using var leftOut = JsonDocument.Parse(reader.GetString(2));
            unsaved = leftOut.RootElement.TryGetProperty("unsaved", out var count) ? count.GetInt32() : 0;
        }

        var entities = new List<DrydockImageEntity>();
        var bytes = tiles.Length;
        await using (var rows = new NpgsqlCommand(
            "SELECT entity_id, prototype_id, map_initialized, components::text, appearance::text, manifest::text, carried::text "
            + "FROM drydock_entity WHERE image_id = @id ORDER BY entity_id",
            connection, transaction))
        {
            rows.Parameters.AddWithValue("id", imageId);
            await using var reader = await rows.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var prototype = await reader.IsDBNullAsync(1, ct) ? null : reader.GetString(1);
                var mapInitialized = reader.GetBoolean(2);
                var entityRows = new Dictionary<string, string>();

                using (var components = JsonDocument.Parse(reader.GetString(3)))
                {
                    foreach (var property in components.RootElement.EnumerateObject())
                        entityRows[property.Name] = property.Value.GetRawText();
                }

                await AddReserved(reader, 4, DrydockImageSystem.AppearanceRow, entityRows, ct);
                await AddReserved(reader, 5, DrydockCodec.ManifestRow, entityRows, ct);
                await AddReserved(reader, 6, DrydockImageSystem.CarriedRow, entityRows, ct);

                bytes += entityRows.Values.Sum(v => v.Length);
                entities.Add(new DrydockImageEntity(id, prototype, mapInitialized, Paused: false, entityRows));
            }
        }

        return new DrydockImage(gridId, entities, tiles, unsaved, bytes);
    }

    private static async Task AddReserved(NpgsqlDataReader reader, int column, string row, Dictionary<string, string> rows, CancellationToken ct)
    {
        if (!await reader.IsDBNullAsync(column, ct))
            rows[row] = reader.GetString(column);
    }

    /// <summary>The caller's connection and open transaction. A write outside one would not roll back with its revision.</summary>
    private static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) Enlisted(ServerDbContext db)
    {
        if (db.Database.CurrentTransaction?.GetDbTransaction() is not NpgsqlTransaction transaction)
            throw new InvalidOperationException("Drydock: the PostgreSQL image store writes only inside the caller's transaction.");

        return ((NpgsqlConnection) db.Database.GetDbConnection(), transaction);
    }

    /// <summary>Runs a read on the caller's connection, opening it for the read when the caller has not.</summary>
    private static async Task<T> WithConnection<T>(ServerDbContext db, Func<NpgsqlConnection, NpgsqlTransaction?, Task<T>> read, CancellationToken ct)
    {
        var connection = (NpgsqlConnection) db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        var opened = connection.State != ConnectionState.Open;
        if (opened)
            await db.Database.OpenConnectionAsync(ct);

        try
        {
            return await read(connection, transaction);
        }
        finally
        {
            if (opened)
                await db.Database.CloseConnectionAsync();
        }
    }
}
