#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests._Triad.PostgresPair;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Drydock.Loader;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The image store against a real database. The PostgreSQL cases run on a scratch database of their own and report
    /// Skipped with the reason on a machine without one; the contract runs on both stores, so the memory store under
    /// SQLite is held to the same reads and writes.
    ///
    /// <para>The image is a real one: a small grid (a wall, a powered APC, a locker with a crowbar in it, and a mob the
    /// walk leaves out) stored by <see cref="DrydockImageSystem"/>, the grid <c>DrydockImageRoundTripCommandTest</c>
    /// builds. Equality is <see cref="DrydockImageComparer"/>.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockPostgresImageStore))]
    public sealed class DrydockRowStoreTest
    {
        [Test]
        [Category("Postgres")]
        public async Task ARealImageRoundTripsThroughPostgresAndLoads()
        {
            await using var postgres = await PostgresTestPair.Start();
            var pair = postgres.Pair;
            var server = pair.Server;
            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            Assert.That(db.DrydockImages, Is.InstanceOf<DrydockPostgresImageStore>(), "A PostgreSQL pair keeps images as rows.");

            var image = await StoreSmallGrid(pair);
            var (ship, owner) = await NewShip(db, store);

            var filed = await store.FileRevision(Request(ship, owner, image), image, keepBlobs: 2);
            var loaded = await store.LoadCurrentImage(ship);

            Assert.Multiple(() =>
            {
                Assert.That(filed.Outcome, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(image.Unsaved, Is.EqualTo(1), "The control: the mob is left out, so the unsaved count has something to carry.");
                Assert.That(loaded, Is.Not.Null);
                Assert.That(DrydockImageComparer.Differences(image, loaded!.Image), Is.Empty);
            });

            var restored = 0;
            await server.WaitPost(() =>
            {
                var mapUid = server.System<SharedMapSystem>().CreateMap(out _);
                restored = server.System<DrydockImageSystem>().Load(loaded!.Image, mapUid).Ids.Count;
            });

            Assert.That(restored, Is.EqualTo(image.Entities.Count), "The image read back from rows loads as a grid.");
            await pair.CleanReturnAsync();
        }

        [Test]
        [Category("Postgres")]
        public async Task AnImporterDisposedWithoutCompleteLeavesNoRows()
        {
            await using var postgres = await PostgresTestPair.Start();
            var pair = postgres.Pair;
            var db = pair.Server.ResolveDependency<IServerDbManager>();
            var store = pair.Server.ResolveDependency<DrydockStore>();

            var image = await StoreSmallGrid(pair);
            var (ship, owner) = await NewShip(db, store);
            await store.FileRevision(Request(ship, owner, image), image, keepBlobs: 0);
            var rows = DrydockPostgresImageStore.EntityRows(image);

            async Task<(long Inside, string How, long After)> Copy(bool complete, int revision)
            {
                long imageId = 0;
                var (inside, how) = await db.RunTriadDbCommand(async (context, token) =>
                {
                    await using var tx = await context.Database.BeginTransactionAsync(token);
                    AddRevision(context, ship, revision);
                    await context.SaveChangesAsync(token);

                    var connection = (NpgsqlConnection) context.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction) tx.GetDbTransaction();
                    imageId = await DrydockPostgresImageStore.InsertImage(connection, transaction, new DrydockImageKey(ship, revision), image, token);
                    await DrydockPostgresImageStore.CopyEntities(connection, imageId, rows, complete, token);

                    (long, string) seen;
                    try
                    {
                        seen = (await CountEntities(connection, imageId, token), "counted");
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InFailedSqlTransaction)
                    {
                        seen = (0, "the transaction was aborted");
                    }

                    await tx.RollbackAsync(token);
                    return seen;
                }, CancellationToken.None);

                var after = await db.RunTriadDbCommand(async (context, token) =>
                {
                    await context.Database.OpenConnectionAsync(token);
                    try
                    {
                        return await CountEntities((NpgsqlConnection) context.Database.GetDbConnection(), imageId, token);
                    }
                    finally
                    {
                        await context.Database.CloseConnectionAsync();
                    }
                }, CancellationToken.None);

                return (inside, how, after);
            }

            var control = await Copy(complete: true, revision: 50);
            var disposed = await Copy(complete: false, revision: 51);
            await TestContext.Out.WriteLineAsync($"completed: {control}; disposed without Complete: {disposed}");

            Assert.Multiple(() =>
            {
                Assert.That(control.Inside, Is.EqualTo(rows.Count), "The control: a completed import is there inside its transaction.");
                Assert.That(disposed.Inside, Is.Zero, "An importer disposed without Complete leaves no rows in its transaction.");
                Assert.That(disposed.After, Is.Zero, "And none after it.");
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        [Category("Postgres")]
        public async Task AnImageWhoseRevisionIsNotFlushedIsRefusedByTheForeignKey()
        {
            await using var postgres = await PostgresTestPair.Start();
            var pair = postgres.Pair;
            var db = pair.Server.ResolveDependency<IServerDbManager>();
            var image = await StoreSmallGrid(pair);

            var thrown = Assert.ThrowsAsync<PostgresException>(async () => await db.RunTriadDbCommand(async (context, token) =>
            {
                await using var tx = await context.Database.BeginTransactionAsync(token);
                await db.DrydockImages.Put(context, new DrydockImageKey(Guid.NewGuid(), 1), image, token);
            }, CancellationToken.None));

            Assert.That(thrown!.SqlState, Is.EqualTo(PostgresErrorCodes.ForeignKeyViolation));
            await pair.CleanReturnAsync();
        }

        [Test]
        [Category("Postgres")]
        public async Task PruningAnImageTakesItsEntityRowsAndKeepsTheRevision()
        {
            await using var postgres = await PostgresTestPair.Start();
            var pair = postgres.Pair;
            var db = pair.Server.ResolveDependency<IServerDbManager>();
            var store = pair.Server.ResolveDependency<DrydockStore>();

            var image = await StoreSmallGrid(pair);
            var (ship, owner) = await NewShip(db, store);
            await store.FileRevision(Request(ship, owner, image), image, keepBlobs: 2);
            var first = await ImageIdOf(db, ship, 1);
            var before = await EntityRows(db, first);

            await store.FileRevision(Request(ship, owner, image), image, keepBlobs: 2);
            await store.FileRevision(Request(ship, owner, image), image, keepBlobs: 2);

            var after = await EntityRows(db, first);
            var revisions = await db.RunTriadDbCommand(async (context, token) =>
                await context.DrydockRevision.AsNoTracking().Where(r => r.ShipGuid == ship).Select(r => r.Revision).OrderBy(r => r).ToListAsync(token),
                CancellationToken.None);

            var retrievable = await store.ListRetrievableRevisions(ship);
            Assert.Multiple(() =>
            {
                Assert.That(before, Is.EqualTo(image.Entities.Count), "The control: revision 1's rows were there.");
                Assert.That(retrievable, Is.EqualTo(new[] { 3, 2 }));
                Assert.That(after, Is.Zero, "The prune's cascade took revision 1's entity rows.");
                Assert.That(revisions, Is.EqualTo(new[] { 1, 2, 3 }), "And kept every revision row.");
            });

            await pair.CleanReturnAsync();
        }

        [TestCase(false, TestName = "TheStoreContract(memory, SQLite)")]
        [TestCase(true, TestName = "TheStoreContract(rows, PostgreSQL)", Category = "Postgres")]
        public async Task TheStoreContract(bool postgres)
        {
            await using var postgresPair = postgres ? await PostgresTestPair.Start() : null;
            await using var sqlitePair = postgres ? null : await PoolManager.GetServerClient();
            var pair = postgresPair?.Pair ?? sqlitePair!;
            var db = pair.Server.ResolveDependency<IServerDbManager>();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var images = db.DrydockImages;

            var image = await StoreSmallGrid(pair);
            var (ship, owner) = await NewShip(db, store);
            await store.FileRevision(Request(ship, owner, image), image, keepBlobs: 0);
            await store.FileRevision(Request(ship, owner, image), image, keepBlobs: 0);

            var one = new DrydockImageKey(ship, 1);
            var copied = new DrydockImageKey(ship, 3);
            var missing = new DrydockImageKey(ship, 99);

            var reads = await db.RunTriadDbCommand(async (context, token) => (
                Got: await images.Get(context, one, token),
                GotMissing: await images.Get(context, missing, token),
                Has: await images.Has(context, one, token),
                HasMissing: await images.Has(context, missing, token),
                Revisions: await images.Revisions(context, ship, token),
                Unknown: await images.Revisions(context, Guid.NewGuid(), token)), CancellationToken.None);

            await db.RunTriadDbCommand(async (context, token) =>
            {
                await using var tx = await context.Database.BeginTransactionAsync(token);
                AddRevision(context, ship, copied.Revision);
                await context.SaveChangesAsync(token);
                await images.Copy(context, one, copied, token);
                await images.Delete(context, ship, new[] { 2 }, token);
                await tx.CommitAsync(token);
            }, CancellationToken.None);

            var afterWrites = await db.RunTriadDbCommand(async (context, token) => (
                Revisions: await images.Revisions(context, ship, token),
                Copy: await images.Get(context, copied, token)), CancellationToken.None);

            var copyMissing = Assert.ThrowsAsync<InvalidOperationException>(async () => await db.RunTriadDbCommand(async (context, token) =>
            {
                await using var tx = await context.Database.BeginTransactionAsync(token);
                await images.Copy(context, missing, new DrydockImageKey(ship, 4), token);
            }, CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(reads.Got, Is.Not.Null);
                Assert.That(DrydockImageComparer.Differences(image, reads.Got!), Is.Empty, "An image reads back as it was filed.");
                Assert.That(reads.GotMissing, Is.Null);
                Assert.That(reads.Has, Is.True);
                Assert.That(reads.HasMissing, Is.False);
                Assert.That(reads.Revisions, Is.EqualTo(new[] { 2, 1 }), "Newest first.");
                Assert.That(reads.Unknown, Is.Empty);
                Assert.That(afterWrites.Revisions, Is.EqualTo(new[] { 3, 1 }), "Copy adds 3; the set delete takes 2.");
                Assert.That(DrydockImageComparer.Differences(image, afterWrites.Copy!), Is.Empty, "A copy reads back as its source.");
                Assert.That(copyMissing, Is.Not.Null, "Copying nothing is refused.");
            });

            await pair.CleanReturnAsync();
        }

        [TestCase(false, TestName = "ThePreflightNamesEachPrototypeAndComponentOnce(memory, SQLite)")]
        [TestCase(true, TestName = "ThePreflightNamesEachPrototypeAndComponentOnce(rows, PostgreSQL)", Category = "Postgres")]
        public async Task ThePreflightNamesEachPrototypeAndComponentOnce(bool postgres)
        {
            await using var postgresPair = postgres ? await PostgresTestPair.Start() : null;
            await using var sqlitePair = postgres ? null : await PoolManager.GetServerClient();
            var pair = postgresPair?.Pair ?? sqlitePair!;
            var db = pair.Server.ResolveDependency<IServerDbManager>();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var images = db.DrydockImages;

            var image = await StoreSmallGrid(pair);
            var (ship, owner) = await NewShip(db, store);
            await store.FileRevision(Request(ship, owner, image), image, keepBlobs: 0);

            var result = await db.RunTriadDbCommand(async (context, token) => (
                Filed: await images.Preflight(context, new DrydockImageKey(ship, 1), token),
                Missing: await images.Preflight(context, new DrydockImageKey(ship, 99), token)), CancellationToken.None);

            // Derived here from the image as filed, not by the store's own code.
            var prototypes = image.Entities.Where(e => e.Prototype != null).Select(e => e.Prototype!).Distinct().Order(StringComparer.Ordinal).ToList();
            var components = image.Entities.SelectMany(e => e.Rows.Keys).Where(name => !name.StartsWith('~')).Distinct().Order(StringComparer.Ordinal).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(image.Entities.Any(e => e.Rows.Keys.Any(name => name.StartsWith('~'))), Is.True,
                    "The control: the image carries a ~ row for the pre-flight to leave out.");
                Assert.That(result.Filed, Is.Not.Null);
                Assert.That(result.Filed!.PrototypeIds, Is.EqualTo(prototypes), "Every prototype the entities name, once, sorted.");
                Assert.That(result.Filed.ComponentNames, Is.EqualTo(components), "Every component the image carries, once, sorted.");
                Assert.That(result.Filed.PrototypeIds, Does.Contain("WallSolid").And.Contain("APCBasic").And.Contain("LockerSteel").And.Contain("Crowbar"),
                    "What the grid was built from.");
                Assert.That(result.Filed.PrototypeIds, Does.Not.Contain("MobMoth"), "The walk leaves the mob out, so nothing names it.");
                Assert.That(result.Filed.ComponentNames, Does.Contain("Transform").And.Contain("EntityStorage"));
                Assert.That(result.Filed.ComponentNames.Any(name => name.StartsWith('~')), Is.False, "A ~ row is not a component.");
                Assert.That(result.Missing, Is.Null, "No image, no pre-flight.");
            });

            await pair.CleanReturnAsync();
        }

        private static async Task<DrydockImage> StoreSmallGrid(TestPair pair)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();
            var containers = server.System<SharedContainerSystem>();
            DrydockImage image = default!;

            await server.WaitPost(() =>
            {
                var grid = map.Grid.Owner;
                server.System<SharedMapSystem>().SetTile(grid, entMan.GetComponent<MapGridComponent>(grid), new Vector2i(1, 0), map.Tile.Tile);
                var wallTile = new EntityCoordinates(grid, 0.5f, 0.5f);
                var lockerTile = new EntityCoordinates(grid, 1.5f, 0.5f);
                entMan.SpawnEntity("WallSolid", wallTile);
                entMan.SpawnEntity("APCBasic", wallTile);
                var locker = entMan.SpawnEntity("LockerSteel", lockerTile);
                var crowbar = entMan.SpawnEntity("Crowbar", lockerTile);
                entMan.SpawnEntity("MobMoth", wallTile);
                Assert.That(containers.Insert(crowbar, containers.GetContainer(locker, "entity_storage")), Is.True);

                image = server.System<DrydockImageSystem>().Store(grid).Image;
            });

            return image;
        }

        private static async Task<(Guid Ship, Guid Owner)> NewShip(IServerDbManager db, DrydockStore store)
        {
            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            return (Guid.NewGuid(), owner);
        }

        private static DrydockRevisionRequest Request(Guid ship, Guid owner, DrydockImage image) => new()
        {
            ShipGuid = ship,
            OwnerUserId = owner,
            ShipName = "Imaged",
            VesselProto = "TestVessel",
            SizeClass = nameof(ShipSizeClass.Cutter),
            Kind = DrydockRevisionKind.PlayerStore,
            MarkStored = true,
            ActorUserId = owner,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1 },
            SizeBytes = image.Bytes,
            Manifest = "[]",
        };

        /// <summary>A revision row with nothing filed under it, for a write to hang an image on inside its own transaction.</summary>
        private static void AddRevision(ServerDbContext context, Guid ship, int revision)
        {
            context.DrydockRevision.Add(new DrydockRevision
            {
                ShipGuid = ship,
                Revision = revision,
                Kind = DrydockRevisionKind.PlayerStore,
                CreatedAt = DateTime.UtcNow,
                EngineFormatVer = 7,
                DrydockFormatVer = DrydockFormat.Current,
                Manifest = "[]",
            });
        }

        private static async Task<long> CountEntities(NpgsqlConnection connection, long imageId, CancellationToken token)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM drydock_entity WHERE image_id = @id", connection);
            command.Parameters.AddWithValue("id", imageId);
            return (long) (await command.ExecuteScalarAsync(token))!;
        }

        private static Task<long> ImageIdOf(IServerDbManager db, Guid ship, int revision) =>
            db.RunTriadDbCommand(async (context, token) =>
            {
                await context.Database.OpenConnectionAsync(token);
                try
                {
                    await using var command = new NpgsqlCommand(
                        "SELECT image_id FROM drydock_image WHERE ship_guid = @ship AND revision = @revision",
                        (NpgsqlConnection) context.Database.GetDbConnection());
                    command.Parameters.AddWithValue("ship", ship);
                    command.Parameters.AddWithValue("revision", revision);
                    return (long) (await command.ExecuteScalarAsync(token))!;
                }
                finally
                {
                    await context.Database.CloseConnectionAsync();
                }
            }, CancellationToken.None);

        private static Task<long> EntityRows(IServerDbManager db, long imageId) =>
            db.RunTriadDbCommand(async (context, token) =>
            {
                await context.Database.OpenConnectionAsync(token);
                try
                {
                    return await CountEntities((NpgsqlConnection) context.Database.GetDbConnection(), imageId, token);
                }
                finally
                {
                    await context.Database.CloseConnectionAsync();
                }
            }, CancellationToken.None);
    }
}
