using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class MarketInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_inventory",
                columns: table => new
                {
                    market_inventory_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    poi_key = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    proto_id = table.Column<string>(type: "text", nullable: false),
                    stack_proto = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    unit_price = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_inventory", x => x.market_inventory_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_market_inventory_poi_key_kind_proto_id",
                table: "market_inventory",
                columns: new[] { "poi_key", "kind", "proto_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_inventory");
        }
    }
}
