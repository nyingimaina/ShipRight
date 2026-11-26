-- 0004_company_memberships (down): old builds read User.CompanyId/Role, which were never changed, so dropping the
-- membership table restores the previous single-company shape without touching accounts or resources.

DROP TABLE IF EXISTS CompanyMembership;
