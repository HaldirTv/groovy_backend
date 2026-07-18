using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Downloads",
                schema: "music",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AlbumName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ArtistName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DownloadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_UserId_Type_AlbumName_ArtistName",
                schema: "music",
                table: "Downloads",
                columns: new[] { "UserId", "Type", "AlbumName", "ArtistName" },
                unique: true,
                filter: "[AlbumName] IS NOT NULL AND [ArtistName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_UserId_Type_ItemId",
                schema: "music",
                table: "Downloads",
                columns: new[] { "UserId", "Type", "ItemId" },
                unique: true,
                filter: "[ItemId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Downloads",
                schema: "music");
        }
    }
}
