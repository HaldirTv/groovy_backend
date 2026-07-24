using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.ChatService.Microservice.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageMediaFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MediaFileName",
                schema: "chat",
                table: "Messages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MediaFileSizeBytes",
                schema: "chat",
                table: "Messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaUrl",
                schema: "chat",
                table: "Messages",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MediaFileName",
                schema: "chat",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaFileSizeBytes",
                schema: "chat",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaUrl",
                schema: "chat",
                table: "Messages");
        }
    }
}
