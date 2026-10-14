# How DevTeam's rules were adjusted to this project

The pack's guidance was written for the template. These parts were rewritten with what DevTeam found here:

- `.NET 10` → `.NET 10`
- `api/src/Shared/Db/Migrations/` → `back-end/ShipRight.Server/Database/Migrations/`
- `@ownerId` → `@companyId`
- `owner_id` → `CompanyId`
- `api/src/Features/` → `back-end/ShipRight.Server/Modules/`
- `web/src/features/` → `front-end/src/pages/`
- `TEST_MARIADB` → `the project's own test-database setting (see its README or test fixtures)`
- `test code in the pattern checks` → (left out of them)
- `the template's sign-in section` → (replaced by what is true here)
- `the template's Customers feature` → (rewritten for this project)
- `web/src/app/` → `front-end/src/app/`
- `api/tests/Api.Tests` → `back-end/ShipRight.Tests`
- `the browser-spec step` → (rewritten for this project)
- `the template's /api sign-in sentence` → (rewritten for this project)
- ``dotnet test` in `api/`` → ``dotnet test` in `the project root``
- ``api/`` → ``back-end/ShipRight.Server/``
- ``web/`` → ``front-end/``
- `(scripts/smoke)` → `(DevTeam's own start-up check)`

## This project's layout (found in its files; it wins over anything below)

- API: `back-end/ShipRight.Server` (built from `the project root`); features in `back-end/ShipRight.Server/Modules`. .NET 10.
- Web app: `front-end`; features in `front-end/src/pages`, pages in `front-end/src/app`.
- API tests: `back-end/ShipRight.Tests`.
- Web tests: jest. No browser test suite.
- Migrations: `back-end/ShipRight.Server/Database/Migrations`.
- Tenancy key: `CompanyId`.
- Where the text below names a folder or tool of the template and this list says otherwise, this list is right.

<!-- end of layout -->


