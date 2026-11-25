-- DBA-only template. Execute against the ArcadeNode database after migration.
-- Create a dedicated server login named arcadenode_runtime using your secret
-- manager/DBA tooling first. No passwords or SQLCMD string substitution here.
-- Never use the migration/DB owner identity to serve HTTP requests.
USE [ArcadeNode];
GO
IF SUSER_ID(N'arcadenode_runtime') IS NULL
    THROW 51000, 'Provision the dedicated runtime login first.', 1;
IF USER_ID(N'arcadenode_runtime') IS NULL
    CREATE USER [arcadenode_runtime] FOR LOGIN [arcadenode_runtime];
GO
IF DATABASE_PRINCIPAL_ID(N'arcadenode_runtime_role') IS NULL
    CREATE ROLE [arcadenode_runtime_role];
GO
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[dbo] TO [arcadenode_runtime_role];
DENY ALTER ON SCHEMA::[dbo] TO [arcadenode_runtime_role];
DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION TO [arcadenode_runtime_role];
ALTER ROLE [arcadenode_runtime_role] ADD MEMBER [arcadenode_runtime];
GO
-- Verify server role membership separately and remove any inherited admin roles.
-- The API also refuses effective sysadmin/db_owner/db_ddladmin/CONTROL/ALTER grants.
