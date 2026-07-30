using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Groovra.Music.Microservice.Migrations
{
    public partial class AddLyricsAndComments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded: both LyricsLrc (AddLyricsLrc) and TrackComments/TrackCommentLikes
            // (AddTrackCommentsClean, with the correct string-TrackId/nullable-UserId shape
            // that matches the current model) are already created by earlier migrations in
            // this chain. This migration's original unconditional CreateTable calls used the
            // OLD Guid-TrackId/FK-to-Tracks shape and would fail outright on a fresh database
            // (objects already exist) - kept as a guarded no-op for history-compatibility on
            // already-migrated databases instead of being deleted outright.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID(N'[music].[Tracks]') AND name = 'LyricsLrc'
                )
                BEGIN
                    ALTER TABLE [music].[Tracks] ADD [LyricsLrc] nvarchar(max) NULL;
                END
            ");

            migrationBuilder.Sql(@"
                IF OBJECT_ID(N'[music].[TrackComments]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [music].[TrackComments] (
                        [Id] uniqueidentifier NOT NULL,
                        [TrackId] nvarchar(450) NOT NULL,
                        [UserId] uniqueidentifier NULL,
                        [AuthorName] nvarchar(256) NOT NULL,
                        [Text] nvarchar(2000) NOT NULL,
                        [LikesCount] int NOT NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [IsDeleted] bit NOT NULL,
                        CONSTRAINT [PK_TrackComments] PRIMARY KEY ([Id])
                    );
                    CREATE INDEX [IX_TrackComments_TrackId] ON [music].[TrackComments] ([TrackId]);
                END
            ");

            migrationBuilder.Sql(@"
                IF OBJECT_ID(N'[music].[TrackCommentLikes]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [music].[TrackCommentLikes] (
                        [Id] uniqueidentifier NOT NULL,
                        [CommentId] uniqueidentifier NOT NULL,
                        [UserId] uniqueidentifier NOT NULL,
                        CONSTRAINT [PK_TrackCommentLikes] PRIMARY KEY ([Id])
                    );
                    CREATE UNIQUE INDEX [IX_TrackCommentLikes_CommentId_UserId] ON [music].[TrackCommentLikes] ([CommentId], [UserId]);
                END
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackCommentLikes",
                schema: "music");

            migrationBuilder.DropTable(
                name: "TrackComments",
                schema: "music");

            migrationBuilder.DropColumn(
                name: "LyricsLrc",
                schema: "music",
                table: "Tracks");
        }
    }
}
