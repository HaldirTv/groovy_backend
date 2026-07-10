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
            migrationBuilder.EnsureSchema(name: "music");

            // 1. ВМЕСТО СОЗДАНИЯ ТАБЛИЦЫ TRACKS — ПРОСТО ДОБАВЛЯЕМ НОВЫЕ КОЛОНКИ:
            migrationBuilder.AddColumn<bool>(
                name: "IsExternal",
                schema: "music",
                table: "Tracks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ExternalAudioUrl",
                schema: "music",
                table: "Tracks",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalCoverUrl",
                schema: "music",
                table: "Tracks",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            // 2. ОСТАВЛЯЕМ СОЗДАНИЕ НОВОЙ ТАБЛИЦЫ FavoriteTracks БЕЗ ИЗМЕНЕНИЙ:
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
