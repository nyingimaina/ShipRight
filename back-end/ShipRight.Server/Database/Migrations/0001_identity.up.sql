-- 0001_identity (up): moves existing accounts onto ASP.NET Core Identity.
-- Safe to run again: every statement is guarded, and nothing is deleted here (old columns are dropped by 0002).

ALTER TABLE `User`
    ADD COLUMN IF NOT EXISTS NormalizedEmail    VARCHAR(255) NULL,
    ADD COLUMN IF NOT EXISTS Role               VARCHAR(20)  NOT NULL DEFAULT 'Viewer',
    ADD COLUMN IF NOT EXISTS IsActive           BOOLEAN      NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS MustChangePassword BOOLEAN      NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS SecurityStamp      VARCHAR(64)  NULL,
    ADD COLUMN IF NOT EXISTS LockoutEnd         DATETIME     NULL,
    ADD COLUMN IF NOT EXISTS AccessFailedCount  INT          NOT NULL DEFAULT 0;

-- Google-only accounts have no password.
ALTER TABLE `User` MODIFY PasswordHash VARCHAR(255) NULL;

-- Every existing account was the only owner of its company, so every one becomes an Admin:
-- nobody loses any access they had. Old password hashes are kept as they are and still work.
UPDATE `User` SET NormalizedEmail = UPPER(Email) WHERE NormalizedEmail IS NULL;
UPDATE `User` SET SecurityStamp = REPLACE(UUID(), '-', '') WHERE SecurityStamp IS NULL OR SecurityStamp = '';
UPDATE `User` SET Role = 'Admin' WHERE Role = 'Viewer';

CREATE UNIQUE INDEX IF NOT EXISTS uq_user_normalized_email ON `User` (NormalizedEmail);
CREATE INDEX IF NOT EXISTS idx_user_company_name ON `User` (CompanyId, Name);
CREATE INDEX IF NOT EXISTS idx_user_company_role ON `User` (CompanyId, Role, IsActive);

CREATE TABLE IF NOT EXISTS ExternalLogin (
    Provider    VARCHAR(50)  NOT NULL,
    ProviderKey VARCHAR(255) NOT NULL,
    UserId      CHAR(36)     NOT NULL,
    Created     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (Provider, ProviderKey),
    KEY idx_external_login_user (UserId),
    CONSTRAINT fk_external_login_user FOREIGN KEY (UserId) REFERENCES `User`(Id)
) ENGINE=InnoDB;

-- Older cloud stores wrote the empty company id on domain rows. Give those rows to the oldest company
-- so they are not orphaned (they cannot be told apart if several legacy companies exist).
UPDATE Project p SET p.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE p.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE Server s SET s.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE s.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE Build b SET b.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE b.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE DockerRegistryResource r SET r.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE r.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE ScriptResource r SET r.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE r.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE CredentialResource r SET r.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE r.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE PipelineResource r SET r.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE r.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE AwsProfileResource r SET r.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE r.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE BackupHistory h SET h.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE h.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE WatchBranchHistory h SET h.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE h.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
UPDATE SshKey k SET k.CompanyId = (SELECT c.Id FROM Company c WHERE c.Deleted = 0 ORDER BY c.Created, c.Id LIMIT 1)
    WHERE k.CompanyId = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM Company);
