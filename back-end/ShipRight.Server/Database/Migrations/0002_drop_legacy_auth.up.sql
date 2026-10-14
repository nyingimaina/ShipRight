-- 0002_drop_legacy_auth (up): removes the pieces of the hand-built sign-in that Identity replaced.
-- Accounts and companies are untouched; only the old admin flag, token counter and token tables go.

ALTER TABLE `User`
    DROP COLUMN IF EXISTS IsAdmin,
    DROP COLUMN IF EXISTS TokenVersion;

DROP TABLE IF EXISTS RefreshToken;
DROP TABLE IF EXISTS ResetToken;
