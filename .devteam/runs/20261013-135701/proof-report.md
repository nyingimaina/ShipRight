# Proof report: Continue an earlier feature that was stopped before it was finished. Do not start over.

Run `20261013-135701` · pack `nextjs-csharp-mariadb` · Stopped · 46m 21s · 15 agent session(s) · $1.75

Models used: claude-sonnet-5-5 (5 calls), opencode-go/deepseek-v4.1-flash (2 calls)

## Verdict

- Requirements with tests: **0 of 13**
- Automated checks: **0 of 0 passed**
- Open blocking findings: **0**
- Revision rounds used: 0 · decisions made: 6

**Not proven.** See the gaps below.

## Ready to ship?

**Not ready: something is still broken.**

| Status | What was checked | Evidence |
| --- | --- | --- |
| Not proven | The app starts and works as set up | The start-up check did not run in this build. |
| Ready | Automated tests and checks pass | 0 of 0 checks passed. 904 tests ran (76 skipped), all passed. |
| Blocked | Every requirement has a test that names it | Only 0 of 13 requirements have a test. |
| Ready | No problems left open by review, QA or security | None open. 0 fixed along the way, 0 note(s) recorded. |
| Not proven | The tests run the code | No coverage report was produced, so nobody knows how much of the code the tests run. |
| Ready | Nothing was skipped | Every check that applies was run. |

_DevTeam 0.43.238_

## Specialists

- **Database**: waiting.

## Requirements → tests

| ID | Requirement | Tests |
| --- | --- | --- |
| R1 | People who already have a ShipRight account can sign in with their current email and password after the switch, and see the same company, projects and data as before. | **none** |
| R2 | A person can register with email and password, which creates a new company with that person as its Admin. | **none** |
| R3 | A person can sign in with Google. If no ShipRight account exists for that Google email, a new company is created for them with them as its Admin; if one exists, they are signed in to it. | **none** |
| R4 | Every person has exactly one company and exactly one role (Admin, Operator or Viewer), and every query for company data is limited to their own company. | **none** |
| R5 | The server enforces roles: Admin can do everything; Operator can build and deploy but cannot change settings or servers; Viewer can only read. | **none** |
| R6 | An Admin can add a teammate to their company by typing the teammate's email, a temporary password and choosing a role (Admin, Operator or Viewer). | **none** |
| R7 | An Admin can change a teammate's role. | **none** |
| R8 | An Admin can switch a teammate off, which stops them signing in while keeping their account and history, and can switch them back on. | **none** |
| R9 | A team screen lists the company's people with email, name, role and active/switched-off status, and offers add, change role, and switch off or on to Admins. | **none** |
| R10 | A person can sign out, stay signed in for 7 days, and reset a forgotten password by email (the forgot-password email is kept); a teammate with a temporary password must choose a new one at first sign-in. | **none** |
| R11 | In desktop/local mode no sign-in is required, and switching to the new system keeps all existing data and projects accessible with no action from the user. | **none** |
| R12 | The old sign-in pieces (own tokens, refresh tokens, IsAdmin flag) are removed and replaced, and the hand-built password reset is replaced by an equivalent reset-by-email flow; the existing sign-in, register and reset screens keep working against the new system. | **none** |
| R13 | The settings the app needs to start are cut down to exactly those still used after this change, consistently in the app, docker-compose and the example settings file. The mail settings for forgot-password stay; Google sign-in settings are added; settings that nothing uses any more are removed. | **none** |

## Assumptions

Decided on the builder's behalf (change any that are wrong):

- Passwords must be at least 8 characters; names and company names up to 200 characters; emails compared without regard to capitals.
- A Google-created company is named from the person's Google display name.
- Accounts lock for 5 minutes after 5 wrong passwords; sessions last 7 days.
- Only an Admin can create teammates, change roles, or switch people off and on.
- A teammate given a temporary password must choose a new one at first sign-in.
- Company scoping comes from the person's account, not from a header or subdomain.
- A company must always keep at least one active Admin.
- The team list shows 20 people per page, ordered by name.
- The first-admin seed settings (admin email and password) are no longer needed because anyone can register or use Google and become an Admin; existing accounts stay in the database. Review at sign-off.
- Start-up of the app (BOOT-001) is proven by in-process automated tests in both modes, because the project has no start-up check and its guidance forbids adding a smoke harness.

## Out of scope

- Invitation emails or invite links; teammates are created directly by an Admin
- Permanently deleting a person's account
- A person belonging to more than one company
- Sign-in providers other than Google
- Two-factor authentication
- Custom roles beyond Admin, Operator and Viewer
- Choosing the company from a header or subdomain
- Creating a start-up smoke runner (scripts/smoke) or enabling DevTeam's start-up check; that is a separate task

## Non-functional requirements

| Area | Requirement | How it is checked |
| --- | --- | --- |
| security | Passwords are stored only as hashes, and passwords, temporary passwords, reset links and Google tokens never appear in logs, error messages or responses. | Review of logs and responses from registration, sign-in, reset, teammate creation and Google sign-in shows none of these values. |
| security | Every endpoint requires sign-in except sign-in, registration, Google sign-in and password reset, each marked public with a reason. In desktop/local mode, this is satisfied without a sign-in screen. | An automated test calls each endpoint without credentials and gets 'sign in required' except the listed public ones. |
| security | Accounts lock for 5 minutes after 5 wrong passwords. | A test shows the sixth attempt within 5 minutes is refused even with the correct password. |
| performance | Sign-in completes in under 1 second, and the team list opens in under 1 second for up to 50 people. | A timing test with 50 people in a company meets both limits. |
| reliability | A failed registration, Google first sign-in or teammate creation leaves no partial records. | Tests that force a failure mid-way find no company without a person and no person without a company. |
| accessibility | Sign-in, register, reset, Google and team screens are fully keyboard-operable, have visible labels, and show field errors beside the field. | A keyboard-only walkthrough completes each flow, and component tests check labels and error placement. |
| reliability | The app starts and its home page loads in both desktop/local mode and signed-in server mode. | In-process automated tests start the app in desktop/local mode and in signed-in server mode, request the home page and read data successfully in both; no scripts/smoke runner or other new start-up harness is created. |
| reliability | The example settings file, docker-compose and the app agree on the same list of settings. | An automated check compares the three lists and fails on any difference. |

## House rules

| Rule | Verdict | How |
| --- | --- | --- |
| PERF-001: Do no needless work and use only the memory, network, storage and startup time the feature needs: no repeated identical work, no loop over everything, no polling where an event exists, nothing loaded that is not shown, no unbounded read, query or cache, and everything opened (connection, listener, timer, file) is released. It must still behave when the data or the number of users is a hundred times larger. | not reviewed | — |
| BOOT-001: The app must start and work as it was set up: its home page loads, sign-in behaves as configured (none when single-user), and reading data through the web app succeeds. | not reviewed | — |
| SEC-001: No secrets (passwords, keys, tokens, connection-string passwords) in source code or committed config. | not reviewed | — |
| SEC-002: Never build SQL by string concatenation or interpolation. SQL text is a constant; every value is passed as a Dapper parameter (@name). | not reviewed | — |
| API-001: Every endpoint requires authorization (RequireAuthorization or a fallback policy) unless explicitly marked AllowAnonymous with a reason in a comment. | not reviewed | — |
| API-002: Users can only read or change records they are allowed to see; ownership/tenant checks happen in the query, not after loading. | not reviewed | — |
| API-003: Validate every write request (required fields, lengths, formats) and return RFC 7807 problem details with field errors on failure. | not reviewed | — |
| API-004: Endpoints accept and return DTO records. Database row types never cross the HTTP boundary, and sensitive columns (password hashes, owner ids) are never returned. | not reviewed | — |
| API-005: Every list endpoint is paginated (page + pageSize with a server-side maximum) and returns the total count. | not reviewed | — |
| DB-001: Every schema change is a numbered migration pair in the project's own migrations folder (see the project map) (NNNN_name.up.sql and NNNN_name.down.sql). The down script undoes the up script. Never edit a migration that has been applied; add a new one. | not reviewed | — |
| DB-002: Index foreign keys and every column used for filtering, sorting or uniqueness; enforce uniqueness with a unique index, not only in code. | not reviewed | — |
| DB-003: Read queries select only the columns the screen needs into DTOs; list queries are paged in SQL (LIMIT/OFFSET); no N+1 queries (no query inside a loop: load related rows with a join, or one query for all the ids). | not reviewed | — |
| DB-004: A search never starts with a wildcard (LIKE '%text' or LIKE CONCAT('%', ...)): it cannot use an index and reads the whole table. Search by prefix, or use a full-text index. | not reviewed | — |
| ARCH-001: Backend feature code lives in back-end/ShipRight.Server/Modules/<Feature>/ and frontend feature code in front-end/src/pages/<feature>/. Code used by two or more features lives in the Shared folders. Reuse before adding. | not reviewed | — |
| ARCH-002: No new packages, frameworks or architectural patterns without an explicit decision recorded in the decision log. | not reviewed | — |
| QUAL-001: No debugging leftovers: Console.WriteLine and Debugger.Break in the API, console.log in the web app. Use ILogger with structured messages. | not reviewed | — |
| BUILD-001: The API and web app build without errors, all tests pass, and lint passes. | not reviewed | — |
| UX-001: Every data screen handles loading, empty, error and success states explicitly. | not reviewed | — |
| UX-002: Forms have visible labels, show field errors next to the field, disable submit while saving, and are fully keyboard-accessible. | not reviewed | — |
| TEST-001: Every requirement is proven by at least one test that names it. | not reached | engine |
| TEST-002: API behaviour is tested through HTTP (WebApplicationFactory) against a real MariaDB (the server named by the project's own test-database setting (see its README or test fixtures), for example the compose database), including unauthorized and not-found cases. | not reviewed | — |
| TEST-003: Web app behaviour is tested with Jest and React Testing Library: components are rendered the way a person sees them (loading, error, empty and success states), and logic such as API calls and hooks has unit tests. | not reviewed | — |
| SCOPE-001: Only files inside the design's declared scope may change. | passed | engine: diff checked against design scope |
| USR-001: For the frontend use the following npm i jattac.libs.web.zest-responsive-layout, npm i jattac.libs.web.zest-file-upload, npm i jattac.libs.web.zest-tabs, npm i jattac.libs.web.responsive-table, npm i jattac.libs.web.zest-textbox, npm i jattac.libs.web.zest-sidekick-menu, npm i jattac.libs.web.zest-button, npm i jattac.libs.web.overflow-menu, and npm i jattac-web-poller in preference of any other components or native ui elements that may perform the same function | not reviewed | — |

## Findings

None raised.

## Decisions

- Replace ShipRight's hand-built sign-in (own tokens, refresh tokens, password reset, IsAdmin flag) with ASP.NET Core Identity running on the project's existing SQL data access, without EF Core. Every person belongs to exactly one company (CompanyId) and has exactly one role: Admin, Operator or Viewer. Existing accounts keep working with their current email and password. Anyone can also sign in with Google; a first-time Google person gets a brand-new company and becomes its Admin. An Admin can add teammates by typing the teammate's email, a temporary password and a role, change a teammate's role, and switch a teammate off (and back on). The server enforces the permissions: Admin does everything; Operator can build and deploy but not change settings or servers; Viewer is read-only. Desktop/local mode needs no sign-in and switches over silently with all data and projects intact. All data stays scoped to the person's company. The forgot-password email stays, so its mail settings stay; every other setting that is no longer used (such as the old signing key) is removed from the app, docker-compose and the example settings file, and Google's settings are added, after tests lock in current behaviour. → **Looks right, start designing** (builder)
- Before anything is built, DevTeam found places where what you asked for conflicts with the rules or checks the work will be judged by. Building now would end in the steps arguing with each other. How should it be settled? → **Change the requirement to fit** (telegram:6096790972)
- Replace ShipRight's hand-built sign-in (own tokens, refresh tokens, password reset, IsAdmin flag) with ASP.NET Core Identity running on the project's existing SQL data access, without EF Core. Every person belongs to exactly one company (CompanyId) and has exactly one role: Admin, Operator or Viewer. Existing accounts keep working with their current email and password. Anyone can also sign in with Google; a first-time Google person gets a brand-new company and becomes its Admin. An Admin can add teammates by typing the teammate's email, a temporary password and a role, change a teammate's role, and switch a teammate off (and back on). The server enforces the permissions: Admin does everything; Operator can build and deploy but not change settings or servers; Viewer is read-only. Desktop/local mode needs no sign-in and switches over silently with all data and projects intact. All data stays scoped to the person's company. The forgot-password email stays, so its mail settings stay; every other setting that is no longer used (such as the old signing key) is removed from the app, docker-compose and the example settings file, and Google's settings are added, after tests lock in current behaviour. That the app starts and works is proven by in-process automated tests; no new start-up script is added to the project. → **Looks right, start designing** (telegram:6096790972)
- The developer failed to produce valid output after the allowed attempts. Retry the Build stage, or stop? → **Retry the Build stage** (telegram:6096790972)
- The developer failed to produce valid output after the allowed attempts. Retry the Build stage, or stop? → **Retry the Build stage** (telegram:6096790972)
- The developer failed to produce valid output after the allowed attempts. Retry the Build stage, or stop? → **Stop this feature** (telegram:6096790972)

## Role documentation

### Analyst: requirements

Replace ShipRight's hand-built sign-in (own tokens, refresh tokens, password reset, IsAdmin flag) with ASP.NET Core Identity running on the project's existing SQL data access, without EF Core. Every person belongs to exactly one company (CompanyId) and has exactly one role: Admin, Operator or Viewer. Existing accounts keep working with their current email and password. Anyone can also sign in with Google; a first-time Google person gets a brand-new company and becomes its Admin. An Admin can add teammates by typing the teammate's email, a temporary password and a role, change a teammate's role, and switch a teammate off (and back on). The server enforces the permissions: Admin does everything; Operator can build and deploy but not change settings or servers; Viewer is read-only. Desktop/local mode needs no sign-in and switches over silently with all data and projects intact. All data stays scoped to the person's company. The forgot-password email stays, so its mail settings stay; every other setting that is no longer used (such as the old signing key) is removed from the app, docker-compose and the example settings file, and Google's settings are added, after tests lock in current behaviour. That the app starts and works is proven by in-process automated tests; no new start-up script is added to the project.

### Architect: design

Continuation design (round 3). The earlier architecture is kept: the requirement now says start-up is proven by in-process tests, so round 2's smoke runner and its scripts/smoke scope are dropped. Cloud mode uses ASP.NET Core Identity with a Dapper/QBuilder IUserStore (no EF Core), 7-day cookie sessions, Google through the framework's AddOAuth (no new package), one fixed role per person (Admin/Operator/Viewer, no RoleManager or role table, an IUserRoleStore adapter over the single Role column plus role and company_id claims), and tenancy taken from the signed-in person's CompanyId claim, never from a header or subdomain. Desktop/local mode registers no auth and keeps its SQLite stores, so data stays intact (R11). Already in the working tree and to be kept: Modules/Identity/** (AppUser, DapperUserStore, IdentityRepository, LegacyAwarePasswordHasher, AppClaimsPrincipalFactory, RoleAccessRules and handler, AuthEndpoints, GoogleSignIn, RegistrationService, TeamEndpoints, TeamRepository, DTOs), Shared/Tenancy (ICompanyContext, CompanyScope), company-scoped Mariadb stores, migrations 0001_identity and 0002_drop_legacy_auth with MigrationRunner, settings cleanup (JWT_KEY, ADMIN_EMAIL, ADMIN_PASSWORD removed; GOOGLE_CLIENT_ID/SECRET added; SMTP settings kept), front-end auth client, login/signup/forgot/reset/change-password pages, Team page, role-aware AppShell, and the backend and jest tests. The old stack (AuthRouter, JwtMiddleware, JwtService, TokenService, SetupService, PasswordHelper, RefreshToken and ResetToken) is already deleted. Finishing work, test-first: (1) Re-run the full backend and jest suites and the typecheck to confirm the baseline is green, and fix anything red. (2) Close the cheap in-scope hardening notes, each with a failing test first: session cookie Secure always in cloud mode (so TLS terminated at a proxy still yields a Secure cookie); rotate the security stamp on role change and password reset so old sessions and reset links die; Google link-on-match only when Google reports the email verified (tested already; record the accepted risk in SETUP.md); DataProtection keys under the directory the app resolves for SHIPRIGHT_DATA_DIR. (3) Every public endpoint (login, register, providers, forgot-password, reset-password, google start and complete) carries an AllowAnonymous reason comment and the route-sweep test lists exactly those and expects 'sign in required' (401) for all others (API-001); desktop mode satisfies this with no sign-in screen. (4) Prove BOOT-001 with in-process automated tests only: no scripts/smoke runner and no other start-up harness is created. A new test file Modules/Identity/BootInProcessTests.cs starts the app with WebApplicationFactory in desktop/local mode (SQLite in a temp data dir, no sign-in: GET /api/health, GET / and GET /api/projects succeed with no credentials, no sign-in routes exist) and in signed-in server mode against the test MariaDB named by the project's own test-database setting (GET /api/projects without credentials returns 401; after register the same read returns 200 with the session cookie). Existing CloudApiFactory and InMemoryCloudFactory are reused. Also an automated settings-agreement test compares .env.example, docker-compose.yml and CloudConfiguration exactly and fails on any difference. (5) Confirm tests exist for the non-functional checks: lockout after 5 wrong passwords for 5 minutes, sign-in and 50-person team list under 1 second, no partial company/person records when registration, Google first sign-in or teammate creation fails mid-way (company and person inserted in one transaction), and no passwords, reset links or Google tokens in logs or responses. Confirm every list endpoint stays paged (team list: default 20, max 50, ordered by name, total returned, index on CompanyId+Name) and every write returns RFC 7807 field errors. Scale and cost: no change in desktop mode; in cloud mode one primary-key read per person per minute for stamp validation, one indexed CompanyId filter per query, team list bounded to 50 rows per page, sign-in limited by the existing rate limiter and lockout, no new threads, timers, packages or startup work beyond one idempotent migration check. The boot tests cost two in-process app starts only when tests run and nothing at app runtime. The last-active-Admin rule is enforced in the same UPDATE statement to avoid races. Open items for sign-off: legacy rows without a company are assigned to the oldest company; SshKeyStore is file-based per machine and is not company-scoped by this change.

- Touches: back-end/ShipRight.Server/Modules/Identity/** (cookie Secure policy, stamp rotation on role change and reset, DataProtection key path, anonymous-reason comments), back-end/ShipRight.Server/Program.cs, CloudDiRegistrar.cs, CloudConfiguration.cs (only if the baseline run shows a gap), back-end/ShipRight.Server/Modules/Builds/BuildRouter.cs, BuildOrchestrator.cs, BuildRecord.cs and MariadbBuildStore.cs (build initiator snapshot, already added; keep and verify), back-end/ShipRight.Server/Modules/Auth/** (only PasswordResetEmail.cs and Company/User models remain), docker-compose.yml, .env.example, SETUP.md (only if the settings-agreement test shows drift; note the Google link risk), front-end auth client, pages and Team module (only for failures found by the jest run), back-end/ShipRight.Tests/** auth, identity, tenancy, migration and configuration tests
- Reuses: Rocket.Libraries.DatabaseIntegrator IDatabaseHelper and QBuilder for all SQL, DatabaseTransactionMiddleware for atomic POSTs, Shared/Tenancy ICompanyContext and CompanyScope already on this branch, SmtpPasswordResetEmailSender and PasswordResetEmail.BuildResetLink (forgot-password email kept), Existing rate limiter, CORS policy with credentials, HealthRouter AppMode and /api/health, useServerMode, Existing CloudApiFactory, InMemoryCloudFactory and MariaDbTestDatabase test fixtures, Existing login, signup, forgot, reset pages, Auth.module.css, AuthGuard, HoverTip, Microsoft.AspNetCore.Identity and Authentication.OAuth from the ASP.NET Core shared framework
- Adds: back-end/ShipRight.Tests/Modules/Identity/BootInProcessTests.cs: in-process start-up, home-page and data-read proof in desktop/local and signed-in server modes (BOOT-001); no scripts/smoke runner or other new start-up harness, Tests first for each hardening item above (cookie Secure, stamp rotation on role change and reset, DataProtection path), Small additive methods on existing Identity classes only; existing method bodies are not edited in place, new methods or thin wrapper classes are used instead
- Scope: back-end/ShipRight.Server/Program.cs, back-end/ShipRight.Server/CloudDiRegistrar.cs, back-end/ShipRight.Server/CloudConfiguration.cs, back-end/ShipRight.Server/ShipRight.Server.csproj, back-end/ShipRight.Server/Database/schema.sql, back-end/ShipRight.Server/Database/Migrations/**, back-end/ShipRight.Server/Database/MigrationRunner.cs, back-end/ShipRight.Server/Database/CloudDatabaseRegistrar.cs, back-end/ShipRight.Server/Modules/Auth/**, back-end/ShipRight.Server/Modules/Identity/**, back-end/ShipRight.Server/Modules/Projects/MariadbProjectStore.cs, back-end/ShipRight.Server/Modules/Servers/MariadbServerStore.cs, back-end/ShipRight.Server/Modules/Builds/MariadbBuildStore.cs, back-end/ShipRight.Server/Modules/Builds/BuildRouter.cs, back-end/ShipRight.Server/Modules/Builds/BuildOrchestrator.cs, back-end/ShipRight.Server/Modules/Builds/BuildRecord.cs, back-end/ShipRight.Server/Modules/Resources/Stores/Mariadb*.cs, back-end/ShipRight.Server/Modules/Scheduler/MariadbBackupHistoryStore.cs, back-end/ShipRight.Server/Modules/Scheduler/CloudSchedulerRegistrar.cs, back-end/ShipRight.Server/Modules/WatchBranch/MariadbWatchBranchHistoryStore.cs, back-end/ShipRight.Server/Shared/Tenancy/**, back-end/ShipRight.Tests/Modules/Auth/**, back-end/ShipRight.Tests/Modules/Identity/**, back-end/ShipRight.Tests/Modules/Deployment/CloudConfigurationTests.cs, back-end/ShipRight.Tests/Database/CloudDiRegistrarTests.cs, back-end/ShipRight.Tests/Database/CloudDatabaseRegistrarTests.cs, back-end/ShipRight.Tests/Database/Migration*Tests.cs, back-end/ShipRight.Tests/Modules/Tenancy/**, back-end/ShipRight.Tests/ShipRight.Tests.csproj, docker-compose.yml, .env.example, SETUP.md, front-end/package.json, front-end/package-lock.json, front-end/src/shared/ApiService.ts, front-end/src/shared/auth/**, front-end/src/shared/types/IBuildRecord.ts, front-end/src/shared/__tests__/ApiService.test.ts, front-end/src/pages/_app.tsx, front-end/src/pages/login.tsx, front-end/src/pages/signup.tsx, front-end/src/pages/forgot-password.tsx, front-end/src/pages/reset-password.tsx, front-end/src/pages/change-password.tsx, front-end/src/pages/team.tsx, front-end/src/modules/Team/**, front-end/src/modules/AppShell/**, front-end/src/pages/Styles/Auth.module.css, front-end/src/pages/Styles/Team.module.css

## Files changed


All prompts, agent outputs and artifacts for this run are in the `transcripts/` and `artifacts/` folders next to this report.
