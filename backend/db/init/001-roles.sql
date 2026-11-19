-- The three database roles (PLAN §9.2, D24), the same names locally and on Azure (D58). Locally they have
-- passwords; on Azure they are Entra identities and password authentication is off.
CREATE ROLE todo_migrator LOGIN PASSWORD 'local-only-migrator';
CREATE ROLE todo_app LOGIN PASSWORD 'local-only-app';
CREATE ROLE todo_readonly LOGIN PASSWORD 'local-only-readonly';

-- The migrator owns the schema; the app reads and writes its tables; the read-only role only reads.
CREATE SCHEMA todo AUTHORIZATION todo_migrator;
GRANT USAGE ON SCHEMA todo TO todo_app, todo_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE todo_migrator IN SCHEMA todo
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO todo_app;
ALTER DEFAULT PRIVILEGES FOR ROLE todo_migrator IN SCHEMA todo GRANT SELECT ON TABLES TO todo_readonly;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
