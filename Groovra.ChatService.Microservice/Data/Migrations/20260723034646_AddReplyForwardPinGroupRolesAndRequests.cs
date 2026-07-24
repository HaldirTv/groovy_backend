using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.ChatService.Microservice.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReplyForwardPinGroupRolesAndRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Role",
                schema: "chat",
                table: "Participants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ForwardedFromMessageId",
                schema: "chat",
                table: "Messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForwardedFromSenderName",
                schema: "chat",
                table: "Messages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplyToMessageId",
                schema: "chat",
                table: "Messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                schema: "chat",
                table: "Conversations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PinnedMessageId",
                schema: "chat",
                table: "Conversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RequestedByUserId",
                schema: "chat",
                table: "Conversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "chat",
                table: "Conversations",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Role",
                schema: "chat",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "ForwardedFromMessageId",
                schema: "chat",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ForwardedFromSenderName",
                schema: "chat",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReplyToMessageId",
                schema: "chat",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                schema: "chat",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "PinnedMessageId",
                schema: "chat",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "RequestedByUserId",
                schema: "chat",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "chat",
                table: "Conversations");
        }
    }
}
