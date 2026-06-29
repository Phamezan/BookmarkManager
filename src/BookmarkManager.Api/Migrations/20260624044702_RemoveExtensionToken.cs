using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveExtensionToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtensionTokens");

            migrationBuilder.DropIndex(
                name: "IX_ExtensionClients_TokenId",
                table: "ExtensionClients");

            migrationBuilder.DropColumn(
                name: "TokenId",
                table: "ExtensionClients");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TokenId",
                table: "ExtensionClients",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "ExtensionTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtensionClientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExtensionTokens_ExtensionClients_ExtensionClientId",
                        column: x => x.ExtensionClientId,
                        principalTable: "ExtensionClients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionClients_TokenId",
                table: "ExtensionClients",
                column: "TokenId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionTokens_ExtensionClientId",
                table: "ExtensionTokens",
                column: "ExtensionClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionTokens_TokenHash",
                table: "ExtensionTokens",
                column: "TokenHash",
                unique: true);
        }
    }
}
