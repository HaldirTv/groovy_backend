using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    public partial class AddTracksPlayCountIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Tracks_IsDeleted_PlayCount",
                schema: "music",
                table: "Tracks",
                columns: new[] { "IsDeleted", "PlayCount" },
                descending: new[] { false, true });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tracks_IsDeleted_PlayCount",
                schema: "music",
                table: "Tracks");
        }
    }
}
