## This project's layout (found in its files; it wins over anything below)

- API: `back-end/ShipRight.Server` (built from `the project root`); features in `back-end/ShipRight.Server/Modules`. .NET 10.
- Web app: `front-end`; features in `front-end/src/pages`, pages in `front-end/src/app`.
- API tests: `back-end/ShipRight.Tests`.
- Web tests: jest. No browser test suite.
- Migrations: no folder found; follow how the project already changes its schema.
- Tenancy key: `CompanyId`.
- Where the text below names a folder or tool of the template and this list says otherwise, this list is right.

<!-- end of layout -->

Stack: ASP.NET Core minimal APIs (.NET 10) with Dapper + MySqlConnector on MariaDB; Next.js App Router with TypeScript. Read an existing feature of this project first (the project map lists them): it is the pattern to copy.

- A feature named `Customers` means `back-end/ShipRight.Server/Modules/Customers/` (endpoints, DTOs, repository with its SQL) and `front-end/src/pages/customers/` (components, API client), plus pages under `front-end/src/app/`. API tests go in `back-end/ShipRight.Tests/` and browser tests in `web/e2e/`.
- A schema change is a new numbered migration pair in `the project's own migrations folder (see the project map)` (`NNNN_name.up.sql` and `.down.sql`): include that folder in scope when the schema changes.
- The Database specialist owns the migrations (`the project's own migrations folder (see the project map)`) and the SQL repositories (`back-end/ShipRight.Server/Modules/<Feature>/<Feature>Repository.cs`). When the feature needs data, list them as design components with those paths, and name each repository method (its inputs and what it returns): the engine then calls the specialist before the developer, and the developer calls your named methods.
- Name the table, its indexes and unique constraints in the design. Name every endpoint (method, route, who may call it) and every page.
- Every record a user owns carries an `CompanyId`, and every query filters on it. Say so in the design.
- Prefer server components for reading data and client components only for interactive parts.


## Who is signed in

This project has its own sign-in and tenancy. Follow what its existing endpoints and queries already do (the project map lists them): every query stays scoped the way the existing ones are, by `CompanyId`. Never read around that scope.



## About this existing project

This is an existing project that was not created by DevTeam. Extend it; never recreate or restructure it. Follow the conventions already in its code, and reuse what is already there.

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.

Not set up in this project (so nothing proves these yet):
- The app starts and works as set up: Nothing starts the app and looks at it (no scripts/smoke/run.mjs), so nothing proves the app starts and works yet.
Where you touch code, add automated tests for it (write the test for the existing behaviour first, then change the code). Do not create the missing scaffolding unless the request asks for it.
