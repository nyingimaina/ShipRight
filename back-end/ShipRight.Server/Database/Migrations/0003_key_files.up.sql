-- 0003_key_files (up): adds the company-shared .pem key-file resource table (R8, R9, R10).
-- Safe to run again: the table and its indexes are guarded, and nothing existing is touched.

CREATE TABLE IF NOT EXISTS KeyFileResource (
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
