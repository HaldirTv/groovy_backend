using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    /// <inheritdoc />
    public partial class InitMusicSchema : Migration
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
                    AudioRelativePath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CoverImageRelativePath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tracks",
                schema: "music");
        }
    }
}
