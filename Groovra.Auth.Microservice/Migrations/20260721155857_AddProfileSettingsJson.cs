using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Auth.Microservice.Migrations
{
    public partial class AddProfileSettingsJson : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                schema: "auth",
                table: "Profiles",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettingsJson",
                schema: "auth",
                table: "Profiles");
        }
    }
}
