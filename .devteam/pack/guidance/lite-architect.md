## This project's layout (found in its files; it wins over anything below)

- API: `back-end/ShipRight.Server` (built from `the project root`); features in `back-end/ShipRight.Server/Modules`. .NET 10.
- Web app: `front-end`; features in `front-end/src/pages`, pages in `front-end/src/app`.
- API tests: `back-end/ShipRight.Tests`.
- Web tests: jest. No browser test suite.
- Migrations: no folder found; follow how the project already changes its schema.
- Tenancy key: `CompanyId`.
- Where the text below names a folder or tool of the template and this list says otherwise, this list is right.

<!-- end of layout -->

## This app has no database yet (fast-MVP version)

Data is kept in JSON files by two classes, `UserStore` (back-end/ShipRight.Server/Modules/Auth) and `CustomerRepository` (back-end/ShipRight.Server/Modules/Customers), built on `FileStorage` (api/src/Shared/Storage). The HTTP code only talks to those two classes; that is the seam. New features in this version follow the same shape: a store class over `FileStorage`, owner-scoped, with the same kind of methods.

If the request is to **add the database**: the full MariaDB version of this same app is in `.devteam/reference/database/` (the Db folder with the migrations and setup help, the SQL-based `UserStore` and `CustomerRepository`, `Program.cs`, the tests, `docker-compose.yml`, `.env.example` and the README). Design the change as bringing that version in, not inventing a new one:

- Replace the two store classes with the reference ones, add `api/src/Shared/Db/**` (it is the file `api/src/Shared/Db/Db.cs` that marks the app as upgraded), update `Program.cs`, `Api.csproj` and the test support files as the reference shows. Remove the file storage once nothing uses it, except for a one-time import (below).
- Add a one-time import of `users.json` and `customers.json` into the new tables, keeping ids and password hashes, safe to run twice (it must not duplicate rows), run at startup only when the tables are empty and the files exist.
- Keep the HTTP behaviour and the existing tests unchanged; the database tests need the scratch-database support the reference has.
- The scope must list every file you will touch, including the test support files and the compose and env files.


## About this existing project

This is an existing project that was not created by DevTeam. Extend it; never recreate or restructure it. Follow the conventions already in its code, and reuse what is already there.

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.

Not set up in this project (so nothing proves these yet):
- The app starts and works as set up: Nothing starts the app and looks at it (no scripts/smoke/run.mjs), so nothing proves the app starts and works yet.
Where you touch code, add automated tests for it (write the test for the existing behaviour first, then change the code). Do not create the missing scaffolding unless the request asks for it.
