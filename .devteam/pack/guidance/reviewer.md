## This project's layout (found in its files; it wins over anything below)

- API: `back-end/ShipRight.Server` (built from `the project root`); features in `back-end/ShipRight.Server/Modules`. .NET 10.
- Web app: `front-end`; features in `front-end/src/pages`, pages in `front-end/src/app`.
- API tests: `back-end/ShipRight.Tests`.
- Web tests: jest. No browser test suite.
- Migrations: no folder found; follow how the project already changes its schema.
- Tenancy key: `CompanyId`.
- Where the text below names a folder or tool of the template and this list says otherwise, this list is right.

<!-- end of layout -->

Look specifically for: endpoints marked `AllowAnonymous` without a reason, row types or sensitive columns returned from endpoints, missing pagination on list endpoints, queries not filtered by `CompanyId`, SQL built from strings, loops that query (N+1), missing indexes for filtered/sorted columns, migrations edited after the fact or without a matching down script, new packages without a recorded decision, and frontend screens without loading/empty/error states.

## Running tests yourself

Judge by the evidence you are given (the checks' results, the developer's report, the diff). Do not re-run what the checks already ran, and do not run the whole browser suite. If you run anything, run one test file or one command that answers a specific doubt, and say which doubt in your finding.


Also look for resource use (PERF-001): connections or streams that are not disposed, database and HTTP calls that ignore the request's cancellation token, whole tables or files read into memory, and timers or listeners that are never cleaned up.

## About this existing project

This is an existing project that was not created by DevTeam. Extend it; never recreate or restructure it. Follow the conventions already in its code, and reuse what is already there.

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.

Not set up in this project (so nothing proves these yet):
- The app starts and works as set up: Nothing starts the app and looks at it (no scripts/smoke/run.mjs), so nothing proves the app starts and works yet.
Where you touch code, add automated tests for it (write the test for the existing behaviour first, then change the code). Do not create the missing scaffolding unless the request asks for it.
