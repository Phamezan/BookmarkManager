using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PasswordSalt = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConfigVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupManifests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BookmarkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupManifests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BookmarkNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    IsProtected = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncState = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CurrentProgress = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalProgress = table.Column<int>(type: "INTEGER", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    CoverImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PurgeAfter = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookmarkNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookmarkNodes_BookmarkNodes_ParentId",
                        column: x => x.ParentId,
                        principalTable: "BookmarkNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExtensionClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExtensionVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BraveVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LocalConfigVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingEventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSuccessfulSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FolderCatalogBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CatalogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtensionClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    ExtensionClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BrowserNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ParentBrowserNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    IsProtected = table.Column<bool>(type: "INTEGER", nullable: false)
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
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedRoots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExtensionTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExtensionClientId = table.Column<Guid>(type: "TEXT", nullable: true)
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
                name: "IX_ActivityLog_Timestamp",
                table: "ActivityLog",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_BookmarkNodes_IsDeleted",
                table: "BookmarkNodes",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_BookmarkNodes_ParentId",
                table: "BookmarkNodes",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_BookmarkNodes_PurgeAfter",
                table: "BookmarkNodes",
                column: "PurgeAfter");

            migrationBuilder.CreateIndex(
                name: "IX_BookmarkNodes_SyncState",
                table: "BookmarkNodes",
                column: "SyncState");

            migrationBuilder.CreateIndex(
                name: "IX_BookmarkNodes_Type",
                table: "BookmarkNodes",
                column: "Type");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityLog");

            migrationBuilder.DropTable(
                name: "AdminAccounts");

            migrationBuilder.DropTable(
                name: "AppConfig");

            migrationBuilder.DropTable(
                name: "BackupManifests");

            migrationBuilder.DropTable(
                name: "BookmarkNodes");

            migrationBuilder.DropTable(
                name: "ExtensionTokens");

            migrationBuilder.DropTable(
                name: "FolderCatalogBatches");

            migrationBuilder.DropTable(
                name: "FolderCatalogEntries");

            migrationBuilder.DropTable(
                name: "TrackedRoots");

            migrationBuilder.DropTable(
                name: "ExtensionClients");
        }
    }
}
