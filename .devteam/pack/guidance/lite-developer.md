## This project's layout (found in its files; it wins over anything below)

- API: `back-end/ShipRight.Server` (built from `the project root`); features in `back-end/ShipRight.Server/Modules`. .NET 10.
- Web app: `front-end`; features in `front-end/src/pages`, pages in `front-end/src/app`.
- API tests: `back-end/ShipRight.Tests`.
- Web tests: jest. No browser test suite.
- Migrations: `back-end/ShipRight.Server/Database/Migrations`.
- Tenancy key: `CompanyId`.
- Where the text below names a folder or tool of the template and this list says otherwise, this list is right.

<!-- end of layout -->

## This app has no database yet (fast-MVP version)

Data is kept in JSON files through `FileStorage` (api/src/Shared/Storage). Write new storage code the same way the two existing stores do: a class over `FileStorage`, scoped by owner, with the work done inside the table's `Read` or `Write` so two requests at once cannot corrupt it. Do not add a database package or SQL.

If the task is to **add the database**, bring in the reference version rather than writing it again:

- The full MariaDB version of this app is in `.devteam/reference/database/`. Copy `api/src/Shared/Db/**`, the SQL-based `UserStore.cs` and `CustomerRepository.cs`, `Program.cs`, `Api.csproj`, the test support (`ScratchDatabase.cs`, `TestApp.cs`) and the migration, setup and health tests, then adapt them. Keep this app's namespace (the reference already uses it).
- Keep `api/src/Shared/Db/Db.cs`: its presence is how the app is recognised as upgraded.
- Add a one-time import of the existing `users.json` and `customers.json` (ids and password hashes preserved, safe to run twice, only when the tables are empty). Test it: an import, a second run that changes nothing, and no files present.
- The existing HTTP tests must pass unchanged against the database version. The database tests need `the project's own test-database setting (see its README or test fixtures)`; if it is not set, say so honestly in your report instead of claiming they passed.
- Update `.env.example`, `docker-compose.yml` and the README as in the reference, and say in the README how existing data is carried over.

## Testing while you build

Cheapest first, and stop at the first failure (chain with `&&`, never `;`, and read the failure instead of grepping counts):

1. In `front-end/`: `npx tsc --noEmit && npm run lint`, then `npx jest --findRelatedTests <the files you changed>` (or one test file). In `back-end/ShipRight.Server/`: `dotnet test --filter <the test class you touched>`.
2. This project has no browser test suite; do not add one unless the request asks for it.
3. The full checks (`npm run verify`, `dotnet test`, the whole jest suite) run once, at the end of your step, not after each edit. They are re-run automatically anyway.

Do not run a command again unless you changed something it depends on.



## About this existing project

This is an existing project that was not created by DevTeam. Extend it; never recreate or restructure it. Follow the conventions already in its code, and reuse what is already there.

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.
