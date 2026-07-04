using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.History.Microservice.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialHistorySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "history");

            migrationBuilder.CreateTable(
                name: "PlaybackHistories",
                schema: "history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TrackId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackHistories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistories_UserId",
                schema: "history",
                table: "PlaybackHistories",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackHistories",
                schema: "history");
        }
    }
}
