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

Its data (users, customers) is kept in JSON files, on purpose, so the builder could get going quickly. Do not treat the missing database as a gap, and do not ask about it unless the builder brings it up.

If the builder asks to add the database, or for their data to be kept safely or shared by more than one copy of the app, that is a request to **add the database**:

- The sign-in and every existing screen and API behaviour stay exactly as they are. Say so as a requirement, with the acceptance criterion that the existing tests still pass.
- The data already in the files is carried over once, so nobody loses their account or records. Write that as a requirement, including what happens if the import is run twice.
- The builder will need a MariaDB to connect to. Do not ask technical questions (which driver, which tables). If you must ask, ask in plain words whether they already have a MariaDB or want the bundled one started with Docker, and whether they will bring their own user and database name. The setup help for the missing pieces is shown to them automatically.
- Out of scope unless asked: changing screens, adding features, moving to another database.


## About this existing project

This is an existing project that was not created by DevTeam. Extend it; never recreate or restructure it. Follow the conventions already in its code, and reuse what is already there.

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.

Not set up in this project (so nothing proves these yet):
- The app starts and works as set up: Nothing starts the app and looks at it (no scripts/smoke/run.mjs), so nothing proves the app starts and works yet.
Where you touch code, add automated tests for it (write the test for the existing behaviour first, then change the code). Do not create the missing scaffolding unless the request asks for it.
