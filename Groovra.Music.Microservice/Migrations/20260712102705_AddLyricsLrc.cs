using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    public partial class AddLyricsLrc : Migration
    {
                protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns 
                    WHERE object_id = OBJECT_ID(N'[music].[Tracks]') AND name = 'LyricsLrc'
                )
                BEGIN
                    ALTER TABLE [music].[Tracks] ADD [LyricsLrc] nvarchar(max) NULL;
                END
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LyricsLrc",
                schema: "music",
                table: "Tracks");
        }
    }
}
