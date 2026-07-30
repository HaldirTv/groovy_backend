using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    public partial class AddSoftDeleteToTracks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "music",
                table: "Tracks",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "music",
                table: "Tracks");
        }
    }
}
