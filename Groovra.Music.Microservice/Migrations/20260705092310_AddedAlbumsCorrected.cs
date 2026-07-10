using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    /// <inheritdoc />
    public partial class AddedAlbumsCorrected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Album",
                schema: "music",
                table: "Tracks",
                newName: "AlbumTitle");

            migrationBuilder.AddColumn<Guid>(
                name: "AlbumId",
                schema: "music",
                table: "Tracks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Albums",
                schema: "music",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ArtistName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CoverImageRelativePath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TrackCount = table.Column<int>(type: "int", nullable: false),
                    TotalDurationSeconds = table.Column<double>(type: "float", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Albums", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FavoriteAlbums",
                schema: "music",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlbumId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FavoriteAlbums", x => new { x.UserId, x.AlbumId });
                    table.ForeignKey(
                        name: "FK_FavoriteAlbums_Albums_AlbumId",
                        column: x => x.AlbumId,
                        principalSchema: "music",
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_AlbumId",
                schema: "music",
                table: "Tracks",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_UserId",
                schema: "music",
                table: "Albums",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteAlbums_AlbumId",
                schema: "music",
                table: "FavoriteAlbums",
                column: "AlbumId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tracks_Albums_AlbumId",
                schema: "music",
                table: "Tracks",
                column: "AlbumId",
                principalSchema: "music",
                principalTable: "Albums",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tracks_Albums_AlbumId",
                schema: "music",
                table: "Tracks");

            migrationBuilder.DropTable(
                name: "FavoriteAlbums",
                schema: "music");

            migrationBuilder.DropTable(
                name: "Albums",
                schema: "music");

            migrationBuilder.DropIndex(
                name: "IX_Tracks_AlbumId",
                schema: "music",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "AlbumId",
                schema: "music",
                table: "Tracks");

            migrationBuilder.RenameColumn(
                name: "AlbumTitle",
                schema: "music",
                table: "Tracks",
                newName: "Album");
        }
    }
}
