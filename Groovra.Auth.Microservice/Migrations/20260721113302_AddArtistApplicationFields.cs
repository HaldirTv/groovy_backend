using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Auth.Microservice.Migrations
{
    /// <inheritdoc />
    public partial class AddArtistApplicationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtistApplicationCountry",
                schema: "auth",
                table: "Profiles",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ArtistApplicationGenre",
                schema: "auth",
                table: "Profiles",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ArtistApplicationName",
                schema: "auth",
                table: "Profiles",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ArtistApplicationPlatform",
                schema: "auth",
                table: "Profiles",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ArtistApplicationStatus",
                schema: "auth",
                table: "Profiles",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArtistApplicationSubmittedAt",
                schema: "auth",
                table: "Profiles",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArtistApplicationCountry",
                schema: "auth",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ArtistApplicationGenre",
                schema: "auth",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ArtistApplicationName",
                schema: "auth",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ArtistApplicationPlatform",
                schema: "auth",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ArtistApplicationStatus",
                schema: "auth",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ArtistApplicationSubmittedAt",
                schema: "auth",
                table: "Profiles");
        }
    }
}
