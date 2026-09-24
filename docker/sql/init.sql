:setvar DatabaseName "ProjectManagementWeb"

IF DB_ID(N'$(DatabaseName)') IS NULL
BEGIN
    CREATE DATABASE [ProjectManagementWeb];
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'pmw_migrator')
BEGIN
    DECLARE @createMigrator nvarchar(max) = N'CREATE LOGIN [pmw_migrator] WITH PASSWORD = ' + QUOTENAME('$(MigratorPassword)', '''') + N', CHECK_POLICY = ON;';
    EXEC sys.sp_executesql @createMigrator;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'pmw_app')
BEGIN
    DECLARE @createApp nvarchar(max) = N'CREATE LOGIN [pmw_app] WITH PASSWORD = ' + QUOTENAME('$(AppPassword)', '''') + N', CHECK_POLICY = ON;';
    EXEC sys.sp_executesql @createApp;
END;
GO

USE [ProjectManagementWeb];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pmw_migrator')
BEGIN
    CREATE USER [pmw_migrator] FOR LOGIN [pmw_migrator];
    ALTER ROLE [db_owner] ADD MEMBER [pmw_migrator];
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pmw_app')
BEGIN
    CREATE USER [pmw_app] FOR LOGIN [pmw_app];
    ALTER ROLE [db_datareader] ADD MEMBER [pmw_app];
    ALTER ROLE [db_datawriter] ADD MEMBER [pmw_app];
END;
GO
