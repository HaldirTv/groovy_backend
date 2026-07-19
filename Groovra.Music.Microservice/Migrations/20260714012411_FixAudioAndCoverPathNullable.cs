using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    /// <inheritdoc />
    public partial class FixAudioAndCoverPathNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "AudioRelativePath",
                schema: "music",
                table: "Tracks",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512,
                oldNullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
