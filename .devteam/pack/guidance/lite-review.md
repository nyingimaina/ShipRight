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

Keeping data in JSON files is deliberate: the builder chose the fast-MVP version. Do not raise the absence of a database, SQL, migrations or database tests as a finding. Judge the file-based code on its own terms: owner scoping, validation, simultaneous requests, and that no secret or personal data is written anywhere it should not be. When the task is to add the database, the full version in `.devteam/reference/database/` is the model to compare against.


## About this existing project

This is an existing project that was not created by DevTeam. Extend it; never recreate or restructure it. Follow the conventions already in its code, and reuse what is already there.

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.

Not set up in this project (so nothing proves these yet):
- The app starts and works as set up: Nothing starts the app and looks at it (no scripts/smoke/run.mjs), so nothing proves the app starts and works yet.
Where you touch code, add automated tests for it (write the test for the existing behaviour first, then change the code). Do not create the missing scaffolding unless the request asks for it.
