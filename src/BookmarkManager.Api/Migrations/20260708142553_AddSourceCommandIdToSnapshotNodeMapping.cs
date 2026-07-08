using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceCommandIdToSnapshotNodeMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceCommandId",
                table: "SnapshotNodeMappings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotNodeMappings_SourceCommandId",
                table: "SnapshotNodeMappings",
                column: "SourceCommandId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SnapshotNodeMappings_SourceCommandId",
                table: "SnapshotNodeMappings");

            migrationBuilder.DropColumn(
                name: "SourceCommandId",
                table: "SnapshotNodeMappings");
        }
    }
}
