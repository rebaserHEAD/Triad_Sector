using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddTriadDrydockLegacyImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "triad_shipyard_consumed_ships",
                columns: table => new
                {
                    triad_shipyard_consumed_ships_id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ship_hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    player_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    imported_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    imported_round_id = table.Column<int>(type: "INTEGER", nullable: true),
                    ship_guid = table.Column<Guid>(type: "TEXT", nullable: true),
                    ship_name = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triad_shipyard_consumed_ships", x => x.triad_shipyard_consumed_ships_id);
                    table.ForeignKey(
                        name: "FK_triad_shipyard_consumed_ships_round_imported_round_id",
                        column: x => x.imported_round_id,
                        principalTable: "round",
                        principalColumn: "round_id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_consumed_ships_imported_round_id",
                table: "triad_shipyard_consumed_ships",
                column: "imported_round_id");

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_consumed_ships_player_user_id",
                table: "triad_shipyard_consumed_ships",
                column: "player_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_consumed_ships_ship_hash",
                table: "triad_shipyard_consumed_ships",
                column: "ship_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "triad_shipyard_consumed_ships");
        }
    }
}
