-- 0004_company_memberships (up): one person, many companies.
--
-- The User row keeps its CompanyId/Role as the person's *primary* company, so an older ShipRight build can still
-- read this database unchanged. CompanyMembership is the source of truth for who may enter which company and with
-- what role. Every existing person is copied across exactly once, so nobody gains or loses access on upgrade.
-- Safe to run again: the table is created only if missing and the backfill skips pairs that already exist.

CREATE TABLE IF NOT EXISTS CompanyMembership (
    Id        CHAR(36)     NOT NULL PRIMARY KEY,
    UserId    CHAR(36)     NOT NULL,
    CompanyId CHAR(36)     NOT NULL,
    Role      VARCHAR(20)  NOT NULL DEFAULT 'Viewer',
    IsActive  BOOLEAN      NOT NULL DEFAULT TRUE,
    Created   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Modified  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_company_membership_user_company (UserId, CompanyId),
    KEY idx_company_membership_company (CompanyId, Role, IsActive),
    KEY idx_company_membership_user (UserId, IsActive),
    CONSTRAINT fk_company_membership_user FOREIGN KEY (UserId) REFERENCES `User`(Id),
    CONSTRAINT fk_company_membership_company FOREIGN KEY (CompanyId) REFERENCES Company(Id)
) ENGINE=InnoDB;

-- A deterministic id (derived from the pair) keeps a half-finished upgrade from creating a second row.
INSERT INTO CompanyMembership (Id, UserId, CompanyId, Role, IsActive, Created, Modified)
SELECT CONCAT(
           SUBSTRING(REPLACE(u.Id, '-', ''), 1, 8), '-',
           SUBSTRING(MD5(CONCAT(u.Id, ':', u.CompanyId)), 1, 4), '-',
           SUBSTRING(MD5(CONCAT(u.Id, ':', u.CompanyId)), 5, 4), '-',
           SUBSTRING(MD5(CONCAT(u.Id, ':', u.CompanyId)), 9, 4), '-',
           SUBSTRING(MD5(CONCAT(u.Id, ':', u.CompanyId)), 13, 12)),
       u.Id, u.CompanyId, u.Role, u.IsActive, u.Created, u.Modified
FROM `User` u
INNER JOIN Company c ON c.Id = u.CompanyId AND c.Deleted = 0
WHERE u.Deleted = 0
ON DUPLICATE KEY UPDATE UserId = UserId;
