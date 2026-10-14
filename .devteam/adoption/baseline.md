# DevTeam adoption baseline

DevTeam 0.46.241 · 2026-10-14 13:13

## What was found

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.

## Quick checks

- The app starts and works as set up: **later** (the slower proof, run with: devteam verify)
- API compiles: **passed**
- API builds and all tests pass: **later** (the slower proof, run with: devteam verify)
- Web app tests pass: **later** (the slower proof, run with: devteam verify)
- Web app types are right: **passed**
- Web app lints and builds: **passed**

## Not set up in this project

Nothing missing that DevTeam looks for.

These stay out of the readiness verdict as **not proven**. The developer is told to add tests where it changes code; nothing is added to your project unless a feature asks for it.

## How to make more checks work

Nothing is stopping a check right now.

## Next:

1. `devteam verify` for the slow proof (full tests, coverage, starting the app).
2. `devteam run "describe the feature you want"`. It is built on its own branch; nothing is merged for you.
3. Changed your mind? `devteam unadopt` removes only the `.devteam` folder.
