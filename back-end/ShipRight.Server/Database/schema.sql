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

CREATE TABLE User (
    Id              CHAR(36)     NOT NULL PRIMARY KEY,
    CompanyId       CHAR(36)     NOT NULL,
    Email           VARCHAR(255) NOT NULL,
    PasswordHash    VARCHAR(255) NOT NULL,
    Name            VARCHAR(255) NOT NULL DEFAULT '',
    IsAdmin         BOOLEAN      NOT NULL DEFAULT FALSE,
    TokenVersion    INT          NOT NULL DEFAULT 1,
    Created         DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified        DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted         BOOLEAN      NOT NULL DEFAULT FALSE,
    UNIQUE KEY uq_user_email (Email),
    KEY idx_user_company (CompanyId),
    CONSTRAINT fk_user_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

CREATE TABLE RefreshToken (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    UserId      CHAR(36)     NOT NULL,
    TokenHash   VARCHAR(64)  NOT NULL,
    ExpiresAt   DATETIME     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    RevokedAt   DATETIME     NULL,
    KEY idx_refresh_token_hash (TokenHash),
    KEY idx_refresh_token_user (UserId),
    CONSTRAINT fk_refresh_token_user FOREIGN KEY (UserId) REFERENCES User(Id)
) ENGINE=InnoDB;

CREATE TABLE ResetToken (
    Id          CHAR(36)     NOT NULL PRIMARY KEY,
    UserId      CHAR(36)     NOT NULL,
    TokenHash   VARCHAR(64)  NOT NULL,
    ExpiresAt   DATETIME     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Deleted     BOOLEAN      NOT NULL DEFAULT FALSE,
    UsedAt      DATETIME     NULL,
    KEY idx_reset_token_hash (TokenHash),
    KEY idx_reset_token_user (UserId),
    CONSTRAINT fk_reset_token_user FOREIGN KEY (UserId) REFERENCES User(Id)
) ENGINE=InnoDB;

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
