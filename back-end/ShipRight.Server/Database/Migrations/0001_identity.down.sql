-- 0001_identity (down): undoes 0001_identity.up.sql. Run 0002_drop_legacy_auth.down.sql first.
-- Accounts and companies are kept. Domain rows that were given a company stay with it.

-- Old sign-in needs a password hash on every account; Google-only accounts get an unusable one.
UPDATE `User` SET PasswordHash = '' WHERE PasswordHash IS NULL;
UPDATE `User` SET IsAdmin = (Role = 'Admin');

DROP TABLE IF EXISTS ExternalLogin;

ALTER TABLE `User` DROP INDEX IF EXISTS uq_user_normalized_email;
ALTER TABLE `User` DROP INDEX IF EXISTS idx_user_company_name;
ALTER TABLE `User` DROP INDEX IF EXISTS idx_user_company_role;

ALTER TABLE `User` MODIFY PasswordHash VARCHAR(255) NOT NULL;

ALTER TABLE `User`
    DROP COLUMN IF EXISTS NormalizedEmail,
    DROP COLUMN IF EXISTS Role,
    DROP COLUMN IF EXISTS IsActive,
    DROP COLUMN IF EXISTS MustChangePassword,
    DROP COLUMN IF EXISTS SecurityStamp,
    DROP COLUMN IF EXISTS LockoutEnd,
    DROP COLUMN IF EXISTS AccessFailedCount;
