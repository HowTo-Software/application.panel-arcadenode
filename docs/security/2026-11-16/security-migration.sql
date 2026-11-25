IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] uniqueidentifier NOT NULL,
        [Timestamp] datetime2 NOT NULL,
        [UserId] uniqueidentifier NULL,
        [Username] nvarchar(100) NULL,
        [Method] nvarchar(10) NOT NULL,
        [Path] nvarchar(2048) NOT NULL,
        [StatusCode] int NOT NULL,
        [IpAddress] nvarchar(45) NULL,
        [UserAgent] nvarchar(512) NULL,
        [DurationMs] bigint NOT NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [Nests] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [Author] nvarchar(100) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Nests] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [Nodes] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [Fqdn] nvarchar(255) NOT NULL,
        [DaemonPort] int NOT NULL,
        [SftpPort] int NOT NULL,
        [UseSsl] bit NOT NULL,
        [BehindProxy] bit NOT NULL,
        [TotalMemory] int NOT NULL,
        [TotalDisk] int NOT NULL,
        [MemoryOverallocate] int NOT NULL,
        [DiskOverallocate] int NOT NULL,
        [DockerEndpoint] nvarchar(max) NOT NULL,
        [DaemonToken] nvarchar(max) NULL,
        [IsOnline] bit NOT NULL,
        [IsMaintenanceMode] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [LastCheckedAt] datetime2 NULL,
        CONSTRAINT [PK_Nodes] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] uniqueidentifier NOT NULL,
        [Username] nvarchar(100) NOT NULL,
        [Email] nvarchar(255) NOT NULL,
        [PasswordHash] nvarchar(max) NOT NULL,
        [FirstName] nvarchar(50) NOT NULL,
        [LastName] nvarchar(50) NOT NULL,
        [Role] int NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [LastLoginAt] datetime2 NULL,
        [FailedLoginAttempts] int NOT NULL,
        [LockoutEnd] datetime2 NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [Eggs] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [DockerImage] nvarchar(max) NOT NULL,
        [DockerImages] nvarchar(max) NULL,
        [StartupCommand] nvarchar(max) NOT NULL,
        [ConfigFiles] nvarchar(max) NULL,
        [ConfigStartup] nvarchar(max) NULL,
        [ConfigStop] nvarchar(max) NULL,
        [Variables] nvarchar(max) NULL,
        [InstallScript] nvarchar(max) NULL,
        [InstallContainer] nvarchar(max) NULL,
        [InstallEntrypoint] nvarchar(max) NULL,
        [NestId] uniqueidentifier NOT NULL,
        [CopyFrom] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Eggs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Eggs_Nests_NestId] FOREIGN KEY ([NestId]) REFERENCES [Nests] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [ApiKeys] (
        [Id] uniqueidentifier NOT NULL,
        [Identifier] nvarchar(100) NOT NULL,
        [Token] nvarchar(max) NOT NULL,
        [Description] nvarchar(500) NULL,
        [AllowedIps] nvarchar(max) NULL,
        [Permissions] nvarchar(max) NULL,
        [LastUsedAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ApiKeys] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ApiKeys_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [InviteCodes] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(32) NOT NULL,
        [MaxUses] int NOT NULL,
        [TimesUsed] int NOT NULL,
        [ExpiresAt] datetime2 NULL,
        [IsRevoked] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CreatedByUserId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_InviteCodes] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_InviteCodes_Users_CreatedByUserId] FOREIGN KEY ([CreatedByUserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [Servers] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [Identifier] nvarchar(450) NOT NULL,
        [ContainerId] nvarchar(max) NULL,
        [DockerImage] nvarchar(max) NOT NULL,
        [StartupCommand] nvarchar(max) NULL,
        [EnvironmentVariables] nvarchar(max) NULL,
        [MemoryLimit] int NOT NULL,
        [CpuLimit] int NOT NULL,
        [DiskLimit] int NOT NULL,
        [Port] int NOT NULL,
        [AdditionalPorts] int NULL,
        [IpAddress] nvarchar(50) NOT NULL,
        [Status] int NOT NULL,
        [IsSuspended] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [LastStartedAt] datetime2 NULL,
        [OwnerId] uniqueidentifier NOT NULL,
        [NodeId] uniqueidentifier NOT NULL,
        [EggId] uniqueidentifier NULL,
        CONSTRAINT [PK_Servers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Servers_Eggs_EggId] FOREIGN KEY ([EggId]) REFERENCES [Eggs] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_Servers_Nodes_NodeId] FOREIGN KEY ([NodeId]) REFERENCES [Nodes] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Servers_Users_OwnerId] FOREIGN KEY ([OwnerId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [Allocations] (
        [Id] uniqueidentifier NOT NULL,
        [IpAddress] nvarchar(50) NOT NULL,
        [Port] int NOT NULL,
        [Alias] nvarchar(100) NULL,
        [Notes] nvarchar(max) NULL,
        [IsAssigned] bit NOT NULL,
        [NodeId] uniqueidentifier NOT NULL,
        [ServerId] uniqueidentifier NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Allocations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Allocations_Nodes_NodeId] FOREIGN KEY ([NodeId]) REFERENCES [Nodes] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_Allocations_Servers_ServerId] FOREIGN KEY ([ServerId]) REFERENCES [Servers] ([Id]) ON DELETE SET NULL
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [Backups] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [IgnoredFiles] nvarchar(max) NULL,
        [Size] bigint NOT NULL,
        [Checksum] nvarchar(max) NULL,
        [IsSuccessful] bit NOT NULL,
        [IsLocked] bit NOT NULL,
        [CompletedAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ServerId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Backups] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Backups_Servers_ServerId] FOREIGN KEY ([ServerId]) REFERENCES [Servers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [Schedules] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [CronExpression] nvarchar(max) NOT NULL,
        [IsActive] bit NOT NULL,
        [IsProcessing] bit NOT NULL,
        [OnlyWhenOnline] bit NOT NULL,
        [LastRunAt] datetime2 NULL,
        [NextRunAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ServerId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Schedules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Schedules_Servers_ServerId] FOREIGN KEY ([ServerId]) REFERENCES [Servers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE TABLE [ScheduleTasks] (
        [Id] uniqueidentifier NOT NULL,
        [SequenceId] int NOT NULL,
        [Action] int NOT NULL,
        [Payload] nvarchar(max) NULL,
        [TimeOffset] int NOT NULL,
        [ContinueOnFailure] bit NOT NULL,
        [ScheduleId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_ScheduleTasks] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ScheduleTasks_Schedules_ScheduleId] FOREIGN KEY ([ScheduleId]) REFERENCES [Schedules] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Author', N'CreatedAt', N'Description', N'Name', N'UpdatedAt') AND [object_id] = OBJECT_ID(N'[Nests]'))
        SET IDENTITY_INSERT [Nests] ON;
    EXEC(N'INSERT INTO [Nests] ([Id], [Author], [CreatedAt], [Description], [Name], [UpdatedAt])
    VALUES (''00000000-0000-0000-0000-000000000002'', N''admin@localhost'', ''2024-01-01T00:00:00.0000000Z'', N''Minecraft game servers including vanilla, Spigot, Paper, and modded servers.'', N''Minecraft'', ''2024-01-01T00:00:00.0000000Z'')');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Author', N'CreatedAt', N'Description', N'Name', N'UpdatedAt') AND [object_id] = OBJECT_ID(N'[Nests]'))
        SET IDENTITY_INSERT [Nests] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'ConfigFiles', N'ConfigStartup', N'ConfigStop', N'CopyFrom', N'CreatedAt', N'Description', N'DockerImage', N'DockerImages', N'InstallContainer', N'InstallEntrypoint', N'InstallScript', N'Name', N'NestId', N'StartupCommand', N'UpdatedAt', N'Variables') AND [object_id] = OBJECT_ID(N'[Eggs]'))
        SET IDENTITY_INSERT [Eggs] ON;
    EXEC(N'INSERT INTO [Eggs] ([Id], [ConfigFiles], [ConfigStartup], [ConfigStop], [CopyFrom], [CreatedAt], [Description], [DockerImage], [DockerImages], [InstallContainer], [InstallEntrypoint], [InstallScript], [Name], [NestId], [StartupCommand], [UpdatedAt], [Variables])
    VALUES (''00000000-0000-0000-0000-000000000003'', NULL, NULL, NULL, NULL, ''2024-01-01T00:00:00.0000000Z'', N''High performance Minecraft server using Paper'', N''ghcr.io/pterodactyl/yolks:java_17'', NULL, NULL, NULL, NULL, N''Paper'', ''00000000-0000-0000-0000-000000000002'', N''java -Xms128M -Xmx{{SERVER_MEMORY}}M -jar server.jar'', ''2024-01-01T00:00:00.0000000Z'', CONCAT(CAST(N''['' AS nvarchar(max)), nchar(13), nchar(10), N''                {"Name":"Server JAR","EnvVariable":"SERVER_JARFILE","DefaultValue":"server.jar","UserViewable":true,"UserEditable":true},'', nchar(13), nchar(10), N''                {"Name":"Minecraft Version","EnvVariable":"MINECRAFT_VERSION","DefaultValue":"latest","UserViewable":true,"UserEditable":true}'', nchar(13), nchar(10), N''            ]''))');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'ConfigFiles', N'ConfigStartup', N'ConfigStop', N'CopyFrom', N'CreatedAt', N'Description', N'DockerImage', N'DockerImages', N'InstallContainer', N'InstallEntrypoint', N'InstallScript', N'Name', N'NestId', N'StartupCommand', N'UpdatedAt', N'Variables') AND [object_id] = OBJECT_ID(N'[Eggs]'))
        SET IDENTITY_INSERT [Eggs] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Allocations_NodeId_IpAddress_Port] ON [Allocations] ([NodeId], [IpAddress], [Port]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Allocations_ServerId] ON [Allocations] ([ServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ApiKeys_Identifier] ON [ApiKeys] ([Identifier]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ApiKeys_UserId] ON [ApiKeys] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_Timestamp] ON [AuditLogs] ([Timestamp]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_UserId] ON [AuditLogs] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Backups_ServerId] ON [Backups] ([ServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Eggs_NestId] ON [Eggs] ([NestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_InviteCodes_Code] ON [InviteCodes] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InviteCodes_CreatedByUserId] ON [InviteCodes] ([CreatedByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Nodes_Fqdn] ON [Nodes] ([Fqdn]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Schedules_ServerId] ON [Schedules] ([ServerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ScheduleTasks_ScheduleId] ON [ScheduleTasks] ([ScheduleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Servers_EggId] ON [Servers] ([EggId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Servers_Identifier] ON [Servers] ([Identifier]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Servers_NodeId] ON [Servers] ([NodeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Servers_OwnerId] ON [Servers] ([OwnerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Email] ON [Users] ([Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Username] ON [Users] ([Username]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214434_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260813214434_InitialCreate', N'10.0.15');
END;

COMMIT;
GO
BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214932_AdditionalPortsToString'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Servers]') AND [c].[name] = N'AdditionalPorts');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Servers] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [Servers] ALTER COLUMN [AdditionalPorts] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260813214932_AdditionalPortsToString'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260813214932_AdditionalPortsToString', N'10.0.15');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    IF EXISTS (SELECT 1 FROM [Nodes] WHERE LEN([DockerEndpoint]) > 450)
        THROW 51000, 'Resolve Docker endpoints longer than 450 characters before migration.', 1;
    IF EXISTS (SELECT [DockerEndpoint] FROM [Nodes] GROUP BY [DockerEndpoint] HAVING COUNT(*) > 1)
        THROW 51000, 'Resolve duplicate Docker endpoints before migration.', 1;
    IF EXISTS (SELECT [NodeId], [Port] FROM [Servers] GROUP BY [NodeId], [Port] HAVING COUNT(*) > 1)
        THROW 51000, 'Resolve duplicate node/port reservations before migration.', 1;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    DROP INDEX [IX_Servers_NodeId] ON [Servers];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    ALTER TABLE [Users] ADD [SecurityStamp] nvarchar(32) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    UPDATE [Users] SET [SecurityStamp] = REPLACE(CONVERT(nvarchar(36), NEWID()), '-', '');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    UPDATE [InviteCodes] SET [IsRevoked] = 1;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Nodes]') AND [c].[name] = N'DockerEndpoint');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [Nodes] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [Nodes] ALTER COLUMN [DockerEndpoint] nvarchar(450) NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    ALTER TABLE [Nodes] ADD [ReservationVersion] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    ALTER TABLE [InviteCodes] ADD [ConcurrencyVersion] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    CREATE TABLE [AuthSessions] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AuthSessions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AuthSessions_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Servers_NodeId_Port] ON [Servers] ([NodeId], [Port]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Nodes_DockerEndpoint] ON [Nodes] ([DockerEndpoint]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    CREATE INDEX [IX_AuthSessions_UserId_ExpiresAt] ON [AuthSessions] ([UserId], [ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117011423_SecurityHardening'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261117011423_SecurityHardening', N'10.0.15');
END;

COMMIT;
GO
