using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Auth.Microservice.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileSettingsJson : Migration
    {
        /// <inheritdoc />
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettingsJson",
                schema: "auth",
                table: "Profiles");
        }
    }
}
