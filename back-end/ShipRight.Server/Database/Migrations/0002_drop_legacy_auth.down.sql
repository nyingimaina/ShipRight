-- 0002_drop_legacy_auth (down): puts the old columns and tables back (empty; old sessions cannot be restored).

ALTER TABLE `User`
    ADD COLUMN IF NOT EXISTS IsAdmin      BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS TokenVersion INT     NOT NULL DEFAULT 1;

CREATE TABLE IF NOT EXISTS RefreshToken (
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
    CONSTRAINT fk_refresh_token_user FOREIGN KEY (UserId) REFERENCES `User`(Id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS ResetToken (
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
    CONSTRAINT fk_reset_token_user FOREIGN KEY (UserId) REFERENCES `User`(Id)
) ENGINE=InnoDB;
