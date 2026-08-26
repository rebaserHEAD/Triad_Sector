using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddTriadDrydock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "drydock_audit",
                columns: table => new
                {
                    drydock_audit_id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ship_guid = table.Column<Guid>(type: "TEXT", nullable: false),
                    ship_name = table.Column<string>(type: "TEXT", nullable: true),
                    action = table.Column<int>(type: "INTEGER", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    subject_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    revision = table.Column<int>(type: "INTEGER", nullable: true),
                    round_id = table.Column<int>(type: "INTEGER", nullable: true),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_audit", x => x.drydock_audit_id);
                });

            migrationBuilder.CreateTable(
                name: "drydock_ship",
                columns: table => new
                {
                    ship_guid = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ship_name = table.Column<string>(type: "TEXT", nullable: false),
                    vessel_proto = table.Column<string>(type: "TEXT", nullable: true),
                    size_class = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<int>(type: "INTEGER", nullable: false),
                    state_changed_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    checked_out_round_id = table.Column<int>(type: "INTEGER", nullable: true),
                    investigating = table.Column<bool>(type: "INTEGER", nullable: false),
                    admin_notes = table.Column<string>(type: "TEXT", nullable: true),
                    current_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_ship", x => x.ship_guid);
                    table.ForeignKey(
                        name: "FK_drydock_ship_player_owner_id",
                        column: x => x.owner_user_id,
                        principalTable: "player",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_drydock_ship_round_checked_out_round_id",
                        column: x => x.checked_out_round_id,
                        principalTable: "round",
                        principalColumn: "round_id");
                });

            migrationBuilder.CreateTable(
                name: "drydock_revision",
                columns: table => new
                {
                    ship_guid = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    derived_from_revision = table.Column<int>(type: "INTEGER", nullable: true),
                    rebake_version = table.Column<int>(type: "INTEGER", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_round_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    engine_format_ver = table.Column<int>(type: "INTEGER", nullable: false),
                    drydock_format_ver = table.Column<int>(type: "INTEGER", nullable: false),
                    proto_fingerprint = table.Column<byte[]>(type: "BLOB", nullable: false),
                    captured_key_hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    checksum = table.Column<byte[]>(type: "BLOB", nullable: false),
                    size_bytes = table.Column<int>(type: "INTEGER", nullable: false),
                    manifest = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_revision", x => new { x.ship_guid, x.revision });
                    table.ForeignKey(
                        name: "FK_drydock_revision_drydock_ship_ship_temp_id",
                        column: x => x.ship_guid,
                        principalTable: "drydock_ship",
                        principalColumn: "ship_guid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_drydock_revision_player_actor_id",
                        column: x => x.actor_user_id,
                        principalTable: "player",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_drydock_revision_round_created_round_id",
                        column: x => x.created_round_id,
                        principalTable: "round",
                        principalColumn: "round_id");
                });

            migrationBuilder.CreateTable(
                name: "drydock_blob",
                columns: table => new
                {
                    ship_guid = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    blob = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_blob", x => new { x.ship_guid, x.revision });
                    table.ForeignKey(
                        name: "FK_drydock_blob_drydock_revision_revision_row_temp_id",
                        columns: x => new { x.ship_guid, x.revision },
                        principalTable: "drydock_revision",
                        principalColumns: new[] { "ship_guid", "revision" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_audit_actor_user_id",
                table: "drydock_audit",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_audit_ship_guid_created_at",
                table: "drydock_audit",
                columns: new[] { "ship_guid", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_revision_actor_user_id",
                table: "drydock_revision",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_revision_created_round_id",
                table: "drydock_revision",
                column: "created_round_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_checked_out_round_id",
                table: "drydock_ship",
                column: "checked_out_round_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_owner_user_id",
                table: "drydock_ship",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_state_state_changed_at",
                table: "drydock_ship",
                columns: new[] { "state", "state_changed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drydock_audit");

            migrationBuilder.DropTable(
                name: "drydock_blob");

            migrationBuilder.DropTable(
                name: "drydock_revision");

            migrationBuilder.DropTable(
                name: "drydock_ship");
        }
    }
}
