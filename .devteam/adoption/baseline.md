# DevTeam adoption baseline

DevTeam 0.38.238 · 2026-10-13 03:04

## What was found

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.

## Quick checks

- The app starts and works as set up: **not set up**
- API compiles: **passed**
- API builds and all tests pass: **later** (the slower proof, run with: devteam verify)
- Web app tests pass: **later** (the slower proof, run with: devteam verify)
- Web app types are right: **passed**
- Web app lints and builds: **passed**

## Not set up in this project

- The app starts and works as set up: Nothing starts the app and looks at it (no scripts/smoke/run.mjs), so nothing proves the app starts and works yet.

These stay out of the readiness verdict as **not proven**. The developer is told to add tests where it changes code; nothing is added to your project unless a feature asks for it.

## How to make more checks work

### 1. Nothing proves the app starts and works

This is optional. Without it the readiness page says 'not proven' for starting the app, which is honest rather than a failure.

1. If you want it: devteam run "Add a start-up check that starts the app and requests its home page"

After you have done any of these, run `devteam adopt refresh` so DevTeam looks at the project again.

## Next:

1. `devteam verify` for the slow proof (full tests, coverage, starting the app).
2. `devteam run "describe the feature you want"`. It is built on its own branch; nothing is merged for you.
3. Changed your mind? `devteam unadopt` removes only the `.devteam` folder.
