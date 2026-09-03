using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddTriadDrydockEscrowAndSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "appraised_value",
                table: "drydock_revision",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "drydock_transfer",
                columns: table => new
                {
                    drydock_transfer_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ship_guid = table.Column<Guid>(type: "TEXT", nullable: false),
                    from_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    to_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    resolution = table.Column<int>(type: "INTEGER", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    round_id = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_transfer", x => x.drydock_transfer_id);
                    table.ForeignKey(
                        name: "FK_drydock_transfer_drydock_ship_ship_temp_id1",
                        column: x => x.ship_guid,
                        principalTable: "drydock_ship",
                        principalColumn: "ship_guid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_transfer_resolution_expires_at",
                table: "drydock_transfer",
                columns: new[] { "resolution", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_transfer_ship_guid",
                table: "drydock_transfer",
                column: "ship_guid",
                unique: true,
                filter: "resolution = 0");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_transfer_to_user_id_resolution",
                table: "drydock_transfer",
                columns: new[] { "to_user_id", "resolution" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drydock_transfer");

            migrationBuilder.DropColumn(
                name: "appraised_value",
                table: "drydock_revision");
        }
    }
}
