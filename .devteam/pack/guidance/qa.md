## This project's layout (found in its files; it wins over anything below)

- API: `back-end/ShipRight.Server` (built from `the project root`); features in `back-end/ShipRight.Server/Modules`. .NET 10.
- Web app: `front-end`; features in `front-end/src/pages`, pages in `front-end/src/app`.
- API tests: `back-end/ShipRight.Tests`.
- Web tests: jest. No browser test suite.
- Migrations: no folder found; follow how the project already changes its schema.
- Tenancy key: `CompanyId`.
- Where the text below names a folder or tool of the template and this list says otherwise, this list is right.

<!-- end of layout -->

API tests use `WebApplicationFactory` against a real MariaDB (`the project's own test-database setting (see its README or test fixtures)`; see `back-end/ShipRight.Tests`). For each requirement check there is a test for the happy path AND for: unauthenticated (401), another user's record (404), invalid input (400 with field errors), a duplicate (409) and not found (404); and that writes without a CSRF token are rejected. Frontend flows should have a Playwright test per user journey in `web/e2e/`.

## Looking at the running app

The automated boot proof (DevTeam's own start-up check) starts the app and checks its home page, sign-in behaviour for the configured mode, and that reading data works. Its result is in the checks you are given: treat a failing boot proof as proof the feature does not work, whatever the unit tests say. A new feature whose read address is not in `scripts/smoke/probes.json` has not been looked at: raise that, citing BOOT-001.

## Running tests yourself

Judge by the evidence you are given (the checks' results, the developer's report, the code and the tests). If you run anything, run one test file or one browser spec that answers a specific doubt: do not run the whole browser suite, and do not re-run what the checks already ran. If the builder chose no browser run, a missing browser run is not a finding.


## About this existing project

This is an existing project that was not created by DevTeam. Extend it; never recreate or restructure it. Follow the conventions already in its code, and reuse what is already there.

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.

Not set up in this project (so nothing proves these yet):
- The app starts and works as set up: Nothing starts the app and looks at it (no scripts/smoke/run.mjs), so nothing proves the app starts and works yet.
Where you touch code, add automated tests for it (write the test for the existing behaviour first, then change the code). Do not create the missing scaffolding unless the request asks for it.
