using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "music");

            migrationBuilder.CreateTable(
                name: "Tracks",
                schema: "music",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ArtistName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Album = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Genre = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DurationSeconds = table.Column<double>(type: "float", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsExternal = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ExternalAudioUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ExternalCoverUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    AudioRelativePath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CoverImageRelativePath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayCount = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FavoriteTracks",
                schema: "music",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TrackId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LikedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FavoriteTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FavoriteTracks_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalSchema: "music",
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteTracks_TrackId",
                schema: "music",
                table: "FavoriteTracks",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteTracks_UserId_TrackId",
                schema: "music",
                table: "FavoriteTracks",
                columns: new[] { "UserId", "TrackId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FavoriteTracks",
                schema: "music");

            migrationBuilder.DropTable(
                name: "Tracks",
                schema: "music");
        }
    }
}
