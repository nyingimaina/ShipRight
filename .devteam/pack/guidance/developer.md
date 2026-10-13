## This project's layout (found in its files; it wins over anything below)

- API: `back-end/ShipRight.Server` (built from `the project root`); features in `back-end/ShipRight.Server/Modules`. .NET 10.
- Web app: `front-end`; features in `front-end/src/pages`, pages in `front-end/src/app`.
- API tests: `back-end/ShipRight.Tests`.
- Web tests: jest. No browser test suite.
- Migrations: no folder found; follow how the project already changes its schema.
- Tenancy key: `CompanyId`.
- Where the text below names a folder or tool of the template and this list says otherwise, this list is right.

<!-- end of layout -->

Stack: ASP.NET Core minimal APIs (.NET 10) with Dapper + MySqlConnector on MariaDB; Next.js App Router with TypeScript.

- Follow how this project's existing features are built (the project map lists them): their endpoints, data access and tests. Keep to how this project's existing endpoints handle sign-in; do not add anonymous access without a reason comment.
- DTOs are `record` types. Map explicitly; never return a row type or a column the screen doesn't need.
- SQL is a `const string`; every value is a Dapper parameter (`@name`). Filter by `CompanyId` inside the SQL. Page lists with `LIMIT/OFFSET` and return the total (`PageRequest`, `PagedResult`). Validate writes with `Validator` and return its problem.
- Schema changes: add `NNNN_name.up.sql` and `NNNN_name.down.sql` to `the project's own migrations folder (see the project map)`. Never edit an applied migration. Make scripts idempotent (`IF NOT EXISTS` / `IF EXISTS`); MariaDB commits DDL immediately.
- MariaDB runs `SET` assignments in an UPDATE left to right; order them with that in mind.
- Run `dotnet test` in `the project root` (needs `the project's own test-database setting (see its README or test fixtures)`, see the README) and `npm run lint && npm run build` in `front-end/` before reporting. Both are re-run automatically and must pass.
- In the build report, list each test with the requirement IDs it proves.
- The Database specialist writes the migrations and the SQL repositories (`the project's own migrations folder (see the project map)`, `*Repository.cs`) when the design needs them: do not edit those files, and call the repository methods the design names. If you need another query or a schema change, report a kickback of type `specialist:database` with the reason. The rules below still apply to any SQL you are asked to write yourself.
- Resource use (PERF-001): open database connections with `await using` so they are always released, pass the request's `CancellationToken` to every database and HTTP call, never load a whole table to filter it in memory, and never run a query or request inside a loop. In the web app, do not sort or filter a big list during render (page it), and clean up timers and listeners in effects.


## Who is signed in

This project has its own sign-in and tenancy. Follow what its existing endpoints and queries already do (the project map lists them): every query stays scoped the way the existing ones are, by `CompanyId`. Never read around that scope.

## The app must start and work (BOOT-001)

DevTeam keeps its own start-up check (under `.devteam`): after the build it starts the app and looks at its home page. This project has no `scripts/smoke` harness, so do not create one or add probe files; if a feature you add cannot start or answer, the check will say so.

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

Not set up in this project (so nothing proves these yet):
- The app starts and works as set up: Nothing starts the app and looks at it (no scripts/smoke/run.mjs), so nothing proves the app starts and works yet.
Where you touch code, add automated tests for it (write the test for the existing behaviour first, then change the code). Do not create the missing scaffolding unless the request asks for it.
