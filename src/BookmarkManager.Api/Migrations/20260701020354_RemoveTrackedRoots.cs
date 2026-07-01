using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTrackedRoots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FolderCatalogBatches");

            migrationBuilder.DropTable(
                name: "FolderCatalogEntries");

            migrationBuilder.DropTable(
                name: "TrackedRoots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FolderCatalogBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CatalogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtensionClientId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderCatalogBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FolderCatalogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BrowserNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExtensionClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsProtected = table.Column<bool>(type: "INTEGER", nullable: false),
                    ParentBrowserNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderCatalogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackedRoots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BrowserNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedRoots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FolderCatalogBatches_ExtensionClientId_CatalogId",
                table: "FolderCatalogBatches",
                columns: new[] { "ExtensionClientId", "CatalogId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FolderCatalogEntries_ExtensionClientId",
                table: "FolderCatalogEntries",
                column: "ExtensionClientId");
        }
    }
}
