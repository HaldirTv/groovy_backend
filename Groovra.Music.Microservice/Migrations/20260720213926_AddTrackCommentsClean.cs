using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    public partial class AddTrackCommentsClean : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF OBJECT_ID(N'[music].[TrackCommentLikes]', N'U') IS NOT NULL DROP TABLE [music].[TrackCommentLikes];");
            migrationBuilder.Sql("IF OBJECT_ID(N'[music].[TrackComments]', N'U') IS NOT NULL DROP TABLE [music].[TrackComments];");

            migrationBuilder.CreateTable(
                name: "TrackCommentLikes",
                schema: "music",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackCommentLikes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackComments",
                schema: "music",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TrackId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    LikesCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackComments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackCommentLikes_CommentId_UserId",
                schema: "music",
                table: "TrackCommentLikes",
                columns: new[] { "CommentId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackComments_TrackId",
                schema: "music",
                table: "TrackComments",
                column: "TrackId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackCommentLikes",
                schema: "music");

            migrationBuilder.DropTable(
                name: "TrackComments",
                schema: "music");
        }
    }
}
