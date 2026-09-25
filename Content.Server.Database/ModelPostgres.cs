using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Content.Server.Database
{
    public sealed class PostgresServerDbContext : ServerDbContext
    {
        public PostgresServerDbContext(DbContextOptions<PostgresServerDbContext> options) : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            ((IDbContextOptionsBuilderInfrastructure) options).AddOrUpdateExtension(new SnakeCaseExtension());

            options.ConfigureWarnings(x =>
            {
                x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
#if DEBUG
                // for tests
                x.Ignore(CoreEventId.SensitiveDataLoggingEnabledWarning);
#endif
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
#endif
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ReSharper disable StringLiteralTypo
            // Enforce that an address cannot be IPv6-mapped IPv4.
            // So that IPv4 addresses are consistent between separate-socket and dual-stack socket modes.
            modelBuilder.Entity<BanAddress>().ToTable(t =>
                t.HasCheckConstraint("AddressNotIPv6MappedIPv4", "NOT inet '::ffff:0.0.0.0/96' >>= address"));

            modelBuilder.Entity<Player>().ToTable(t =>
                t.HasCheckConstraint("LastSeenAddressNotIPv6MappedIPv4",
                    "NOT inet '::ffff:0.0.0.0/96' >>= last_seen_address"));

            modelBuilder.Entity<ConnectionLog>().ToTable(t =>
                t.HasCheckConstraint("AddressNotIPv6MappedIPv4",
                    "NOT inet '::ffff:0.0.0.0/96' >>= address"));

            // ReSharper restore StringLiteralTypo

            modelBuilder.Entity<AdminLog>()
                .HasIndex(l => l.Message)
                .HasMethod("GIN")
                .IsTsVectorExpressionIndex("english");

            // Triad: market data, the parts of the schema that only Postgres can express.
            // Declared on the model rather than as migration SQL, so the snapshot carries them and
            // a later migration cannot silently drop them. Filters name the physical column, which
            // is why these cannot live in the shared model: SQLite spells them differently.

            // The payout trace. Queryable and indexable here, plain text on SQLite.
            modelBuilder.Entity<MarketTransaction>()
                .Property(t => t.Calc)
                .HasColumnType("jsonb");

            // The table is append-only in timestamp order, which is the case BRIN exists for, and
            // every Grafana panel over it is a range scan. On tens of millions of rows this is
            // kilobytes of index where a BTREE would be hundreds of megabytes. Nothing does a point
            // lookup on a timestamp here.
            modelBuilder.Entity<MarketTransaction>()
                .HasIndex(t => t.OccurredAt)
                .HasMethod("BRIN");

            modelBuilder.Entity<MarketTransactionLine>()
                .HasIndex(l => l.OccurredAt)
                .HasMethod("BRIN");

            // Machine-driven income has no actor and is the majority of rows, so keep it out.
            modelBuilder.Entity<MarketTransaction>()
                .HasIndex(t => new { t.ActorUserId, t.OccurredAt })
                .HasFilter("actor_user_id IS NOT NULL");

            // The pricing lookup and the rollup's driving scan, made covering so the rollup never
            // touches the heap. No partial filter: a refused transaction writes a header and no
            // lines, so this index cannot contain one by construction. Filtering on the header's
            // succeeded column from here is not possible anyway, it lives on another table.
            modelBuilder.Entity<MarketTransactionLine>()
                .HasIndex(l => new { l.EntityProto, l.Direction, l.OccurredAt })
                .IncludeProperties(l => new { l.UnitPrice, l.Quantity });
            // End Triad

            // Triad: the drydock's grid images, PostgreSQL only; SQLite keeps images in memory. No index beyond the two
            // keys and the one unique (ship_guid, revision) the foreign key to the revision makes. Column compression
            // and the per-table autovacuum parameters are migration SQL, because no EF expression covers them.
            modelBuilder.Entity<DrydockImageRow>(image =>
            {
                image.ToTable("drydock_image", t =>
                {
                    t.HasCheckConstraint("CK_drydock_image_entity_count", "entity_count >= 1");
                    t.HasCheckConstraint("CK_drydock_image_tile_count", "tile_count >= 0");
                });

                image.HasKey(i => i.ImageId);
                image.Property(i => i.ImageId).UseIdentityAlwaysColumn();
                image.Property(i => i.Tiles).HasColumnType("jsonb");
                image.Property(i => i.LeftOut).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");

                image.HasOne(i => i.RevisionRow)
                    .WithOne()
                    .HasForeignKey<DrydockImageRow>(i => new { i.ShipGuid, i.Revision })
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<DrydockEntityRow>(entity =>
            {
                entity.ToTable("drydock_entity");
                entity.HasKey(e => new { e.ImageId, e.EntityId });

                entity.HasOne<DrydockImageRow>()
                    .WithMany()
                    .HasForeignKey(e => e.ImageId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.Property(e => e.Components).HasColumnType("jsonb");
                entity.Property(e => e.Appearance).HasColumnType("jsonb");
                entity.Property(e => e.Manifest).HasColumnType("jsonb");
                entity.Property(e => e.Carried).HasColumnType("jsonb");
            });
            // End Triad

            foreach(var entity in modelBuilder.Model.GetEntityTypes())
            {
                foreach(var property in entity.GetProperties())
                {
                    if (property.FieldInfo?.FieldType == typeof(DateTime) || property.FieldInfo?.FieldType == typeof(DateTime?))
                        property.SetColumnType("timestamp with time zone");
                }
            }
        }

        public override IQueryable<AdminLog> SearchLogs(IQueryable<AdminLog> query, string searchText)
        {
            return query.Where(log => EF.Functions.ToTsVector("english", log.Message).Matches(searchText));
        }

        public override int CountAdminLogs()
        {
            using var command = new NpgsqlCommand("SELECT reltuples FROM pg_class WHERE relname = 'admin_log';", (NpgsqlConnection?) Database.GetDbConnection());

            Database.GetDbConnection().Open();
            var count = Convert.ToInt32((float) (command.ExecuteScalar() ?? 0));
            Database.GetDbConnection().Close();
            return count;
        }
    }
}
