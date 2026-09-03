using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddTriadDrydockBerths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "berth_id",
                table: "drydock_ship",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "last_berth_id",
                table: "drydock_ship",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ship_guid",
                table: "drydock_audit",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "berth_id",
                table: "drydock_audit",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "drydock_berth",
                columns: table => new
                {
                    berth_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_size_class = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    price_paid = table.Column<int>(type: "integer", nullable: false),
                    purchased_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    purchased_round_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_berth", x => x.berth_id);
                    table.UniqueConstraint("ak_drydock_berth_berth_id_owner_user_id", x => new { x.berth_id, x.owner_user_id });
                    table.ForeignKey(
                        name: "FK_drydock_berth_player_owner_id",
                        column: x => x.owner_user_id,
                        principalTable: "player",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_drydock_berth_round_purchased_round_id",
                        column: x => x.purchased_round_id,
                        principalTable: "round",
                        principalColumn: "round_id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_berth_id",
                table: "drydock_ship",
                column: "berth_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_berth_id_owner_user_id",
                table: "drydock_ship",
                columns: new[] { "berth_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_last_berth_id",
                table: "drydock_ship",
                column: "last_berth_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_audit_subject_user_id",
                table: "drydock_audit",
                column: "subject_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_berth_owner_user_id",
                table: "drydock_berth",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_berth_purchased_round_id",
                table: "drydock_berth",
                column: "purchased_round_id");

            migrationBuilder.AddForeignKey(
                name: "FK_drydock_ship_drydock_berth_berth_id",
                table: "drydock_ship",
                columns: new[] { "berth_id", "owner_user_id" },
                principalTable: "drydock_berth",
                principalColumns: new[] { "berth_id", "owner_user_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_drydock_ship_drydock_berth_last_berth_id",
                table: "drydock_ship",
                column: "last_berth_id",
                principalTable: "drydock_berth",
                principalColumn: "berth_id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_drydock_ship_drydock_berth_berth_id",
                table: "drydock_ship");

            migrationBuilder.DropForeignKey(
                name: "FK_drydock_ship_drydock_berth_last_berth_id",
                table: "drydock_ship");

            migrationBuilder.DropTable(
                name: "drydock_berth");

            migrationBuilder.DropIndex(
                name: "IX_drydock_ship_berth_id",
                table: "drydock_ship");

            migrationBuilder.DropIndex(
                name: "IX_drydock_ship_berth_id_owner_user_id",
                table: "drydock_ship");

            migrationBuilder.DropIndex(
                name: "IX_drydock_ship_last_berth_id",
                table: "drydock_ship");

            migrationBuilder.DropIndex(
                name: "IX_drydock_audit_subject_user_id",
                table: "drydock_audit");

            migrationBuilder.DropColumn(
                name: "berth_id",
                table: "drydock_ship");

            migrationBuilder.DropColumn(
                name: "last_berth_id",
                table: "drydock_ship");

            migrationBuilder.DropColumn(
                name: "berth_id",
                table: "drydock_audit");

            migrationBuilder.AlterColumn<Guid>(
                name: "ship_guid",
                table: "drydock_audit",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
