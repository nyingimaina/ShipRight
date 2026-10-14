-- ShipRight Cloud - MariaDB Schema
-- PascalCase names to match Dapper/QBuilder conventions.
-- Set lower_case_table_names=1 on Linux MariaDB for case-insensitive tables.

CREATE DATABASE IF NOT EXISTS shipright CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE shipright;

-- ============================================================================
-- Auth Tables (structured columns for indexed lookups)
-- ============================================================================

CREATE TABLE Company (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    Name        VARCHAR(255) NOT NULL,
    Slug        VARCHAR(255) NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    UNIQUE KEY uq_company_slug (Slug)
) ENGINE=InnoDB;

-- One person: exactly one company and one role (Admin, Operator or Viewer). Matches migrations 0001 + 0002.
CREATE TABLE User (
    Id                 CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId          CHAR(36)     NOT NULL,
    Email              VARCHAR(255) NOT NULL,
    NormalizedEmail    VARCHAR(255) NULL,
    PasswordHash       VARCHAR(255) NULL,
    Name               VARCHAR(255) NOT NULL DEFAULT '',
    Role               VARCHAR(20)  NOT NULL DEFAULT 'Viewer',
    IsActive           BOOLEAN      NOT NULL DEFAULT TRUE,
    MustChangePassword BOOLEAN      NOT NULL DEFAULT FALSE,
    SecurityStamp      VARCHAR(64)  NULL,
    LockoutEnd         DATETIME     NULL,
    AccessFailedCount  INT          NOT NULL DEFAULT 0,
    Created            DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified           DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted            BOOLEAN      NOT NULL DEFAULT FALSE,
    UNIQUE KEY uq_user_email (Email),
    UNIQUE KEY uq_user_normalized_email (NormalizedEmail),
    KEY idx_user_company (CompanyId),
    KEY idx_user_company_name (CompanyId, Name),
    KEY idx_user_company_role (CompanyId, Role, IsActive),
    CONSTRAINT fk_user_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE ExternalLogin (
    Provider    VARCHAR(50)  NOT NULL,
    ProviderKey VARCHAR(255) NOT NULL,
    UserId      CHAR(36)     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (Provider, ProviderKey),
    KEY idx_external_login_user (UserId),
    CONSTRAINT fk_external_login_user FOREIGN KEY (UserId) REFERENCES User(Id)
) ENGINE=InnoDB;

-- Records which numbered migrations have run. A fresh database already has their result, so they are marked done.
CREATE TABLE SchemaMigration (
    Id        VARCHAR(100) NOT NULL PRIMARY KEY,
    AppliedAt DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB;

INSERT INTO SchemaMigration (Id) VALUES ('0001_identity'), ('0002_drop_legacy_auth'), ('0003_key_files');

-- ============================================================================
-- Domain Tables (JSON-based for flexibility, matching existing SQLite pattern)
-- ============================================================================

CREATE TABLE Project (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    Name        VARCHAR(255) NOT NULL,
    Data        LONGTEXT     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_project_company (CompanyId),
    CONSTRAINT fk_project_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE Server (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    Name        VARCHAR(255) NOT NULL,
    Data        LONGTEXT     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_server_company (CompanyId),
    CONSTRAINT fk_server_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE Build (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    ProjectId   CHAR(36)     NOT NULL,
    ProjectName VARCHAR(255) NOT NULL DEFAULT '',
    Status      VARCHAR(50)  NOT NULL DEFAULT 'Pending',
    GitTag      VARCHAR(255) NOT NULL DEFAULT '',
    Data        LONGTEXT     NULL,
    StartedAt   DATETIME     NULL,
    CompletedAt DATETIME     NULL,
    LogPath     VARCHAR(500) NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_build_company (CompanyId),
    KEY idx_build_project (ProjectId),
    KEY idx_build_status (Status),
    CONSTRAINT fk_build_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

-- All resource stores use a simple Id + Name + Data(JSON) pattern, matching
-- the existing SQLite stores. Name is indexed for lookups.
CREATE TABLE DockerRegistryResource (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    Name        VARCHAR(255) NOT NULL,
    Data        LONGTEXT     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_docker_registry_company (CompanyId),
    CONSTRAINT fk_docker_registry_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE ScriptResource (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    Name        VARCHAR(255) NOT NULL,
    Scope       VARCHAR(50)  NOT NULL DEFAULT 'Global',
    ProjectId   CHAR(36)     NULL,
    Data        LONGTEXT     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_script_company (CompanyId),
    KEY idx_script_scope (Scope),
    CONSTRAINT fk_script_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE CredentialResource (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    Name        VARCHAR(255) NOT NULL,
    ProjectId   CHAR(36)     NULL,
    Data        LONGTEXT     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_credential_company (CompanyId),
    CONSTRAINT fk_credential_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

-- .pem key files, shared company-wide. Name + Tags live in Data (JSON); the .pem content is inside
-- Data too and is never selected into a metadata read. idx_key_file_company_created serves ordered paging.
CREATE TABLE KeyFileResource (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    Name        VARCHAR(255) NOT NULL,
    Data        LONGTEXT     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_key_file_company (CompanyId),
    KEY idx_key_file_company_created (CompanyId, Created),
    CONSTRAINT fk_key_file_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE PipelineResource (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    Name        VARCHAR(255) NOT NULL,
    Scope       VARCHAR(50)  NOT NULL DEFAULT 'Global',
    ProjectId   CHAR(36)     NULL,
    Data        LONGTEXT     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_pipeline_company (CompanyId),
    KEY idx_pipeline_scope (Scope),
    CONSTRAINT fk_pipeline_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE AwsProfileResource (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    Name        VARCHAR(255) NOT NULL,
    Data        LONGTEXT     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_aws_profile_company (CompanyId),
    CONSTRAINT fk_aws_profile_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

-- ============================================================================
-- Scheduler / Watch Branch / SSH Tables (structured columns)
-- ============================================================================

CREATE TABLE BackupHistory (
    Id              CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId       CHAR(36)     NOT NULL,
    ProjectId       CHAR(36)     NOT NULL,
    ProjectName     VARCHAR(255) NOT NULL DEFAULT '',
    DatabaseName    VARCHAR(255) NOT NULL DEFAULT '',
    Status          VARCHAR(50)  NOT NULL DEFAULT 'completed',
    StartedAt       DATETIME     NOT NULL,
    CompletedAt     DATETIME     NULL,
    DurationMs      BIGINT       NOT NULL DEFAULT 0,
    ErrorMessage    TEXT         NULL,
    BackupFileName  VARCHAR(500) NULL,
    BackupSizeBytes BIGINT       NOT NULL DEFAULT 0,
    ScheduleId      CHAR(36)     NOT NULL,
    CorrelationId   CHAR(36)     NOT NULL,
    Created         DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified        DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted         BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_backup_company (CompanyId),
    KEY idx_backup_project (ProjectId),
    KEY idx_backup_started (StartedAt),
    CONSTRAINT fk_backup_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE WatchBranchHistory (
    Id                CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId         CHAR(36)     NOT NULL,
    ProjectId         CHAR(36)     NOT NULL,
    ProjectName       VARCHAR(255) NOT NULL DEFAULT '',
    BranchName        VARCHAR(255) NOT NULL DEFAULT '',
    TriggeredBuildId  CHAR(36)     NULL,
    Status            VARCHAR(50)  NOT NULL DEFAULT 'triggered',
    TriggeredAt       DATETIME     NOT NULL,
    ErrorMessage      TEXT         NULL,
    ScheduleId        CHAR(36)     NOT NULL,
    CorrelationId     CHAR(36)     NOT NULL,
    Created           DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified          DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted           BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_watch_branch_company (CompanyId),
    KEY idx_watch_branch_project (ProjectId),
    CONSTRAINT fk_watch_branch_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE SshKey (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId   CHAR(36)     NOT NULL,
    ProjectId   CHAR(36)     NOT NULL,
    PublicKey   TEXT         NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    KEY idx_ssh_key_company (CompanyId),
    KEY idx_ssh_key_project (ProjectId),
    CONSTRAINT fk_ssh_key_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;
