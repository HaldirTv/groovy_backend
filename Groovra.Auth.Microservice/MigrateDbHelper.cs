
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Groovra.Auth.Microservice.Data;

namespace Groovra.Auth.Microservice
{
    public static class MigrateDbHelper
    {
        public static void EnsureColumns(AuthDbContext db)
        {
            string[] sqls = new string[]
            {
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Users]') AND name = 'TwoFactorEnabled')
                  ALTER TABLE [auth].[Users] ADD [TwoFactorEnabled] bit NOT NULL DEFAULT 0;",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Users]') AND name = 'TwoFactorSecret')
                  ALTER TABLE [auth].[Users] ADD [TwoFactorSecret] nvarchar(max) NULL;",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Users]') AND name = 'TempTwoFactorSecret')
                  ALTER TABLE [auth].[Users] ADD [TempTwoFactorSecret] nvarchar(max) NULL;",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Users]') AND name = 'TwoFactorRecoveryCodesJson')
                  ALTER TABLE [auth].[Users] ADD [TwoFactorRecoveryCodesJson] nvarchar(max) NULL;",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Profiles]') AND name = 'SettingsJson')
                  ALTER TABLE [auth].[Profiles] ADD [SettingsJson] nvarchar(max) NOT NULL DEFAULT '';",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Profiles]') AND name = 'ArtistApplicationStatus')
                  ALTER TABLE [auth].[Profiles] ADD [ArtistApplicationStatus] nvarchar(64) NOT NULL DEFAULT 'None';",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Profiles]') AND name = 'ArtistApplicationName')
                  ALTER TABLE [auth].[Profiles] ADD [ArtistApplicationName] nvarchar(256) NOT NULL DEFAULT '';",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Profiles]') AND name = 'ArtistApplicationGenre')
                  ALTER TABLE [auth].[Profiles] ADD [ArtistApplicationGenre] nvarchar(128) NOT NULL DEFAULT '';",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Profiles]') AND name = 'ArtistApplicationCountry')
                  ALTER TABLE [auth].[Profiles] ADD [ArtistApplicationCountry] nvarchar(128) NOT NULL DEFAULT '';",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Profiles]') AND name = 'ArtistApplicationPlatform')
                  ALTER TABLE [auth].[Profiles] ADD [ArtistApplicationPlatform] nvarchar(128) NOT NULL DEFAULT '';",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Profiles]') AND name = 'ArtistApplicationSubmittedAt')
                  ALTER TABLE [auth].[Profiles] ADD [ArtistApplicationSubmittedAt] datetime2 NULL;"
            };

            foreach (var sql in sqls)
            {
                try {
                    db.Database.ExecuteSqlRaw(sql);
                } catch (Exception ex) {
                    Console.WriteLine("MigrateDbHelper SQL error: " + ex.Message);
                }
            }

            try
            {
                db.Database.ExecuteSqlRaw(@"
                    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[auth].[UserFollows]'))
                    BEGIN
                        CREATE TABLE [auth].[UserFollows] (
                            [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_UserFollows] PRIMARY KEY,
                            [FollowerId] uniqueidentifier NOT NULL,
                            [FollowedId] uniqueidentifier NOT NULL,
                            [FollowedAt] datetime2 NOT NULL DEFAULT (GETUTCDATE()),
                            CONSTRAINT [FK_UserFollows_Users_FollowerId] FOREIGN KEY ([FollowerId]) REFERENCES [auth].[Users] ([Id]) ON DELETE NO ACTION,
                            CONSTRAINT [FK_UserFollows_Users_FollowedId] FOREIGN KEY ([FollowedId]) REFERENCES [auth].[Users] ([Id]) ON DELETE NO ACTION
                        );
                        CREATE UNIQUE INDEX [IX_UserFollows_FollowerId_FollowedId] ON [auth].[UserFollows] ([FollowerId], [FollowedId]);
                    END
                ");
            }
            catch (Exception ex)
            {
                Console.WriteLine("MigrateDbHelper UserFollows error: " + ex.Message);
            }
        }
    }
}
