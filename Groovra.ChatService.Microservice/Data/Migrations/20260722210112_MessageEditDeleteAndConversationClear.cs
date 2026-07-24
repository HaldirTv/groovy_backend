using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.ChatService.Microservice.Data.Migrations
{
    /// <inheritdoc />
    public partial class MessageEditDeleteAndConversationClear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClearedAt",
                schema: "chat",
                table: "Participants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EditedAt",
                schema: "chat",
                table: "Messages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEdited",
                schema: "chat",
                table: "Messages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MessageDeletions",
                schema: "chat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageDeletions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageDeletions_Messages_MessageId",
                        column: x => x.MessageId,
                        principalSchema: "chat",
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeletions_MessageId_UserId",
                schema: "chat",
                table: "MessageDeletions",
                columns: new[] { "MessageId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeletions_UserId",
                schema: "chat",
                table: "MessageDeletions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageDeletions",
                schema: "chat");

            migrationBuilder.DropColumn(
                name: "ClearedAt",
                schema: "chat",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "EditedAt",
                schema: "chat",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsEdited",
                schema: "chat",
                table: "Messages");
        }
    }
}
