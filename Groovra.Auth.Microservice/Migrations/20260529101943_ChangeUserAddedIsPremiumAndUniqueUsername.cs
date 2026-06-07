using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Auth.Microservice.Migrations
{
    /// <inheritdoc />
    public partial class ChangeUserAddedIsPremiumAndUniqueUsername : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                schema: "auth",
                table: "Users");

            migrationBuilder.AddColumn<bool>(
                name: "IsPremium",
                schema: "auth",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                schema: "auth",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                schema: "auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsPremium",
                schema: "auth",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                schema: "auth",
                table: "Users",
                column: "Username");
        }
    }
}
