using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
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
                    market_inventory_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    poi_key = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    proto_id = table.Column<string>(type: "TEXT", nullable: false),
                    stack_proto = table.Column<string>(type: "TEXT", nullable: true),
                    quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    unit_price = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
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
