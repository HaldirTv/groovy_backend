
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
                  ALTER TABLE [auth].[Profiles] ADD [ArtistApplicationSubmittedAt] datetime2 NULL;",
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[Users]') AND name = 'IsSuspended')
                  ALTER TABLE [auth].[Users] ADD [IsSuspended] bit NOT NULL DEFAULT 0;"
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

            // Таблиці аудиту для адмінської панелі безпеки. Свідомо тут, а не окремою
            // EF-міграцією: знімок моделі (AuthDbContextModelSnapshot) уже розійшовся з реальною
            // схемою спільної хмарної БД - у ньому немає UserFollow, тому scaffold згенерував би
            // зайвий CREATE TABLE і впав. Той самий підхід, що й для UserFollows/2FA/SettingsJson.
            // Індекси й FK дублюють те, що описано в AuthDbContext.OnModelCreating, щоб модель
            // і фізична схема не розходились. Блоки ідемпотентні - безпечні при кожному старті.
            string[] auditTableSqls = new string[]
            {
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[auth].[LoginAudits]'))
                  BEGIN
                      CREATE TABLE [auth].[LoginAudits] (
                          [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_LoginAudits] PRIMARY KEY,
                          [UserId] uniqueidentifier NULL,
                          [Email] nvarchar(256) NOT NULL DEFAULT '',
                          [Success] bit NOT NULL DEFAULT 0,
                          [IpAddress] nvarchar(64) NULL,
                          [FailureReason] nvarchar(256) NULL,
                          [CreatedAt] datetime2 NOT NULL DEFAULT (GETUTCDATE()),
                          CONSTRAINT [FK_LoginAudits_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[Users] ([Id]) ON DELETE NO ACTION
                      );
                      CREATE INDEX [IX_LoginAudits_CreatedAt] ON [auth].[LoginAudits] ([CreatedAt]);
                      CREATE INDEX [IX_LoginAudits_Email_CreatedAt] ON [auth].[LoginAudits] ([Email], [CreatedAt]);
                      CREATE INDEX [IX_LoginAudits_UserId] ON [auth].[LoginAudits] ([UserId]);
                  END",

                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[auth].[ThreatEvents]'))
                  BEGIN
                      CREATE TABLE [auth].[ThreatEvents] (
                          [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ThreatEvents] PRIMARY KEY,
                          [UserId] uniqueidentifier NULL,
                          [Type] nvarchar(64) NOT NULL DEFAULT '',
                          [Score] int NOT NULL DEFAULT 0,
                          [Description] nvarchar(1024) NOT NULL DEFAULT '',
                          [DetectedAt] datetime2 NOT NULL DEFAULT (GETUTCDATE()),
                          [IsResolved] bit NOT NULL DEFAULT 0,
                          CONSTRAINT [FK_ThreatEvents_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[Users] ([Id]) ON DELETE NO ACTION
                      );
                      CREATE INDEX [IX_ThreatEvents_DetectedAt] ON [auth].[ThreatEvents] ([DetectedAt]);
                      CREATE INDEX [IX_ThreatEvents_Score] ON [auth].[ThreatEvents] ([Score]);
                      CREATE INDEX [IX_ThreatEvents_UserId] ON [auth].[ThreatEvents] ([UserId]);
                  END",

                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[auth].[OAuthRiskEvents]'))
                  BEGIN
                      CREATE TABLE [auth].[OAuthRiskEvents] (
                          [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_OAuthRiskEvents] PRIMARY KEY,
                          [UserId] uniqueidentifier NULL,
                          [Provider] nvarchar(64) NOT NULL DEFAULT '',
                          [RiskScore] int NOT NULL DEFAULT 0,
                          [Reason] nvarchar(512) NOT NULL DEFAULT '',
                          [CreatedAt] datetime2 NOT NULL DEFAULT (GETUTCDATE()),
                          CONSTRAINT [FK_OAuthRiskEvents_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[Users] ([Id]) ON DELETE NO ACTION
                      );
                      CREATE INDEX [IX_OAuthRiskEvents_CreatedAt] ON [auth].[OAuthRiskEvents] ([CreatedAt]);
                      CREATE INDEX [IX_OAuthRiskEvents_Provider] ON [auth].[OAuthRiskEvents] ([Provider]);
                      CREATE INDEX [IX_OAuthRiskEvents_UserId] ON [auth].[OAuthRiskEvents] ([UserId]);
                  END",

                // Догортання для БД, де таблицю вже створив попередній (форкнутий) код без
                // цієї колонки - інакше AdminService.GetSecurityStatsAsync впаде на t.IsResolved.
                @"IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[auth].[ThreatEvents]'))
                  AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[auth].[ThreatEvents]') AND name = 'IsResolved')
                  ALTER TABLE [auth].[ThreatEvents] ADD [IsResolved] bit NOT NULL DEFAULT 0;"
            };

            foreach (var sql in auditTableSqls)
            {
                try
                {
                    db.Database.ExecuteSqlRaw(sql);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("MigrateDbHelper audit-table error: " + ex.Message);
                }
            }
        }
    }
}
