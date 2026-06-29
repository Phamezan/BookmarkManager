using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExtensionEventAndSnapshotEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExtensionCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CommandType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BookmarkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BrowserNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExpectedVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClaimedByClientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExtensionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtensionClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BrowserNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TrackedRootBrowserNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CausedByOperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConfigVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtensionClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotNodeMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotBatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookmarkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BrowserNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotNodeMappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionCommands_OperationId",
                table: "ExtensionCommands",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionCommands_Status",
                table: "ExtensionCommands",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionEvents_EventId",
                table: "ExtensionEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtensionEvents_ExtensionClientId",
                table: "ExtensionEvents",
                column: "ExtensionClientId");

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotBatches_ExtensionClientId",
                table: "SnapshotBatches",
                column: "ExtensionClientId");

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotBatches_RequestId",
                table: "SnapshotBatches",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotNodeMappings_SnapshotBatchId",
                table: "SnapshotNodeMappings",
                column: "SnapshotBatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtensionCommands");

            migrationBuilder.DropTable(
                name: "ExtensionEvents");

            migrationBuilder.DropTable(
                name: "SnapshotBatches");

            migrationBuilder.DropTable(
                name: "SnapshotNodeMappings");
        }
    }
}
