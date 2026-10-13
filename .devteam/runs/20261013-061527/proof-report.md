# Proof report: Refactor the authentication setup in my ASP.NET Core project. Strip out all existing auth code and replace it with Microsoft.AspNetCore.Identity. Requirements: 1. Pure .NET Core Identity: Do NOT use Entity Framework Core (EF Core) or AddEntityFrameworkStores. I use custom SQL/Dapper for data access, so implement or wire up a minimal custom IUserStore / IUserRoleStore or lightweight custom handlers.

Run `20261013-061527` · pack `nextjs-csharp-mariadb` · Stopped · 337m 4s · 23 agent session(s) · $12.75

Models used: claude-sonnet-5-5 (13 calls), opencode/big-pickle (8 calls)

## Verdict

- Requirements with tests: **13 of 13**
- Automated checks: **17 of 18 passed**
- Open blocking findings: **3**
- Revision rounds used: 3 · decisions made: 21

**Not proven.** See the gaps below.

## Ready to ship?

**Not ready: something is still broken.**

| Status | What was checked | Evidence |
| --- | --- | --- |
| Not proven | The app starts and works as set up | Nothing starts this app and looks at it yet. DevTeam can set that up: run  devteam adopt doctor. |
| Ready | Automated tests and checks pass | 17 of 17 checks passed. 903 tests ran (75 skipped), all passed. |
| Ready | Every requirement has a test that names it | 13 of 13 requirements have a test. |
| Blocked | No problems left open by review, QA or security | 3 problem(s) are still open. |
| Not proven | The tests run the code | No coverage report was produced, so nobody knows how much of the code the tests run. |
| Ready | Nothing was skipped | Every check that applies was run. |

_DevTeam 0.40.238_

## Specialists

- **Database**: waiting.

## Requirements → tests

| ID | Requirement | Tests |
| --- | --- | --- |
| R1 | People who already have a ShipRight account can sign in with their current email and password after the switch, and see the same company, projects and data as before. | Login_AnyCapitalisationOfTheEmail_Works (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); OldAccount_SignsInWithTheOldEmailAndPassword_AndItsHashIsUpgraded (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); Login_FiveWrongPasswords_LockTheAccount_EvenForTheCorrectPasswordAfterwards (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); Login_WrongPasswordAndUnknownEmail_GiveTheSameGenericMessage (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ErrorResponses_NeverContainSecrets (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`) |
| R2 | A person can register with email and password, which creates a new company with that person as its Admin. | Register_CreatesOneCompanyAndOneAdmin_AndSignsThePersonIn (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); Register_PressedTwiceAtOnce_CreatesOnlyOneCompanyAndAccount (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); Register_WithATakenEmail_AnyCapitalisation_IsRefusedBesideTheEmailField_AndLeavesNoCompany (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); Register_InvalidFields_AreReportedBesideTheField (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); throws the error object returned by the server, including field errors (`front-end/src/shared/auth/__tests__/AuthApi.test.ts`) |
| R3 | A person can sign in with Google. If no ShipRight account exists for that Google email, a new company is created for them with them as its Admin; if one exists, they are signed in to it. | ErrorResponses_NeverContainSecrets (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); FirstTimePerson_BecomesAdminOfANewCompanyNamedAfterThem (`back-end/ShipRight.Tests/Modules/Identity/GoogleSignInTests.cs`); SigningInTwice_FindsTheSameAccount_AndCreatesNoSecondCompany (`back-end/ShipRight.Tests/Modules/Identity/GoogleSignInTests.cs`); ADoubleSignInAtOnce_CreatesOnlyOneCompany (`back-end/ShipRight.Tests/Modules/Identity/GoogleSignInTests.cs`); AnEmailGoogleHasNotVerified_IsRefused (`back-end/ShipRight.Tests/Modules/Identity/GoogleSignInTests.cs`); Google_WhenNotConfigured_IsOffAndEmailSignInStillWorks (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); shows the Google button only when the server has Google configured (`front-end/src/shared/auth/__tests__/LoginPage.test.tsx`); keeps email sign-in available and explains when Google is not configured (`front-end/src/shared/auth/__tests__/LoginPage.test.tsx`); ASwitchedOffPerson_IsStillFlaggedOff_SoTheSignInIsRefused (`back-end/ShipRight.Tests/Modules/Identity/GoogleSignInTests.cs`); ASwitchedOffPerson_AtTheGoogleCallback_IsRefused_AndNoSessionIsCreated (`back-end/ShipRight.Tests/Modules/Identity/GoogleSignInTests.cs`) |
| R4 | Every person has exactly one company and exactly one role (Admin, Operator or Viewer), and every query for company data is limited to their own company. | AnotherCompanysProject_LooksExactlyLikeOneThatDoesNotExist (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ACompanyIdInAHeaderOrQuery_IsIgnored (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); AnotherCompanysTeamMember_IsNotFound_ForEveryTeamAction (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ChangeRole_OfSomeoneInAnotherCompany_IsNotFound (`back-end/ShipRight.Tests/Modules/Identity/TeamRepositoryTests.cs`) |
| R5 | The server enforces roles: Admin can do everything; Operator can build and deploy but cannot change settings or servers; Viewer can only read. | EndpointsWithoutSignIn_AnswerSignInRequired (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); OperatorCanReadAndOperate_ButNotChangeSettingsOrServers (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ViewerCanOnlyRead_AndSeesNoChangeAllowed (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ARoleChangeAppliesOnThePersonsNextRequest (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); APersonSwitchedOffWhileSignedIn_LosesAccessOnTheNextRequest (`back-end/ShipRight.Tests/Modules/Identity/CloudApiPipelineTests.cs`); TemporaryPassword_AllowsOnlyChangePasswordMeAndLogout (`back-end/ShipRight.Tests/Modules/Identity/RoleAccessRulesTests.cs`); hides the management UI from non-Admin roles (`front-end/src/modules/Team/__tests__/TeamPage.test.tsx`) |
| R6 | An Admin can add a teammate to their company by typing the teammate's email, a temporary password and choosing a role (Admin, Operator or Viewer). | ErrorResponses_NeverContainSecrets (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); TemporaryPassword_AllowsOnlyChangePasswordMeAndLogout (`back-end/ShipRight.Tests/Modules/Identity/RoleAccessRulesTests.cs`); Teammate_IsCreatedInTheAdminsCompany_AndNeverEchoesThePassword (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); Teammate_MustChooseANewPasswordFirst_ThenWorksNormally (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); Teammate_SavedTwiceAtOnce_IsCreatedOnce (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); Teammate_InvalidFields_AreReportedBesideTheField (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); adds a teammate with the chosen role and refreshes the list (`front-end/src/modules/Team/__tests__/TeamPage.test.tsx`); surfaces validation errors from the server next to the fields (`front-end/src/modules/Team/__tests__/TeamPage.test.tsx`); sends a person with a temporary password to change-password (`front-end/src/shared/auth/__tests__/LoginPage.test.tsx`); forces a person with a temporary password to change-password (`front-end/src/shared/auth/__tests__/AuthGuard.test.tsx`); throws the error object returned by the server, including field errors (`front-end/src/shared/auth/__tests__/AuthApi.test.ts`) |
| R7 | An Admin can change a teammate's role. | LastActiveAdmin_CannotBeDemotedOrSwitchedOff (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); TwoAdminsStepDownAtOnce_LeaveTheCompanyWithAnAdmin (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); RoleChange_WhenSomeoneElseChangedItFirst_IsReportedNotSilentlyOverwritten (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ChangeRole_RefusedForTheLastAdmin_IsReportedAsLastAdmin (`back-end/ShipRight.Tests/Modules/Identity/TeamRepositoryTests.cs`); ChangeRole_WhenSomeoneElseChangedItFirst_IsAConflictNotASilentOverwrite (`back-end/ShipRight.Tests/Modules/Identity/TeamRepositoryTests.cs`); keeps the list after a role conflict and shows the guidance (`front-end/src/modules/Team/__tests__/TeamPage.test.tsx`) |
| R8 | An Admin can switch a teammate off, which stops them signing in while keeping their account and history, and can switch them back on. | AnotherCompanysTeamMember_IsNotFound_ForEveryTeamAction (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); APersonSwitchedOffWhileSignedIn_LosesAccessOnTheNextRequest (`back-end/ShipRight.Tests/Modules/Identity/CloudApiPipelineTests.cs`); LastActiveAdmin_CannotBeDemotedOrSwitchedOff (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); SwitchingOff_StopsSignInAndAnOpenSession_KeepsEverything_AndCanBeUndone (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); SwitchingOffOrOn_Twice_DoesNothingAndShowsNoError (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ASwitchedOffPerson_IsStillFlaggedOff_SoTheSignInIsRefused (`back-end/ShipRight.Tests/Modules/Identity/GoogleSignInTests.cs`); deactivates a teammate only after confirmation (`front-end/src/modules/Team/__tests__/TeamPage.test.tsx`); ASwitchedOffPerson_AtTheGoogleCallback_IsRefused_AndNoSessionIsCreated (`back-end/ShipRight.Tests/Modules/Identity/GoogleSignInTests.cs`); BuildStartedByASwitchedOffTeammate_StillShowsTheirName (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`) |
| R9 | A team screen lists the company's people with email, name, role and active/switched-off status, and offers add, change role, and switch off or on to Admins. | TeamList_IsPaged_OrderedByName_WithATotal_AndAServerSideMaximum (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); CompanyWithOnlyItsAdmin_ListsThatPerson (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); TeamList_With50People_AndSignIn_AreEachUnderOneSecond (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); loads and lists the team for an Admin (`front-end/src/modules/Team/__tests__/TeamPage.test.tsx`) |
| R10 | A person can sign out, stay signed in for 7 days, and reset a forgotten password by email (the forgot-password email is kept); a teammate with a temporary password must choose a new one at first sign-in. | ErrorResponses_NeverContainSecrets (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ForgotPassword_SaysTheSameThing_WhetherOrNotTheEmailExists_AndNeverShowsTheLink (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ResetLink_WorksOnce_ThenSaysItIsUsedOrExpired (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ResettingForASwitchedOffAccount_NeitherWorksNorSwitchesItOn (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); ResetRequests_AreRateLimited (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`) |
| R11 | In desktop/local mode no sign-in is required, and switching to the new system keeps all existing data and projects accessible with no action from the user. | DesktopMode_StartsWithNoCloudSettings_AndReadsDataWithoutSigningIn (`back-end/ShipRight.Tests/Modules/Identity/DesktopModeStartupTests.cs`); DesktopMode_HasNoSignInRoutes_AndNeverAsksForOne (`back-end/ShipRight.Tests/Modules/Identity/DesktopModeStartupTests.cs`); Upgrade_RunTwice_LeavesTheDataIntact (`back-end/ShipRight.Tests/Database/MigrationUpgradeTests.cs`); Upgrade_RerunningTheFirstScriptByHand_IsHarmless (`back-end/ShipRight.Tests/Database/MigrationUpgradeTests.cs`); skips the identity check and renders children in desktop mode (`front-end/src/shared/auth/__tests__/AuthGuard.test.tsx`); skips login entirely and redirects to projects in desktop mode (`front-end/src/shared/auth/__tests__/LoginPage.test.tsx`) |
| R12 | The old sign-in pieces (own tokens, refresh tokens, IsAdmin flag) are removed and replaced, and the hand-built password reset is replaced by an equivalent reset-by-email flow; the existing sign-in, register and reset screens keep working against the new system. | OldCookieOrTokenFromBeforeTheSwitch_GetsAPlainSignInRequired_NotAnErrorPage (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); OldAuthEndpoints_AreGone (`back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`); OldTokenOrCookieFromBeforeTheSwitch_GetsAPlainSignInRequired (`back-end/ShipRight.Tests/Modules/Identity/CloudApiPipelineTests.cs`); clears auth and surfaces a sign-in-required error rather than retrying (`front-end/src/shared/__tests__/ApiService.test.ts`); redirects to /login when the identity cookie is rejected on a protected page (`front-end/src/shared/auth/__tests__/AuthGuard.test.tsx`); throws a readable ApiError when the server returns an empty 404 body (`front-end/src/shared/auth/__tests__/AuthApi.test.ts`) |
| R13 | The settings the app needs to start are cut down to exactly those still used after this change, consistently in the app, docker-compose and the example settings file. The mail settings for forgot-password stay; Google sign-in settings are added; settings that nothing uses any more are removed. | DesktopMode_StartsWithNoCloudSettings_AndReadsDataWithoutSigningIn (`back-end/ShipRight.Tests/Modules/Identity/DesktopModeStartupTests.cs`); ExampleFile_ListsExactlyTheAppsSettings (`back-end/ShipRight.Tests/Modules/Identity/SettingsAgreementTests.cs`); DockerCompose_ListsExactlyTheAppsSettings (`back-end/ShipRight.Tests/Modules/Identity/SettingsAgreementTests.cs`); RemovedSettings_AppearNowhereInTheThreeLists (`back-end/ShipRight.Tests/Modules/Identity/SettingsAgreementTests.cs`); ExampleFile_HoldsNoRealLookingSecrets (`back-end/ShipRight.Tests/Modules/Identity/SettingsAgreementTests.cs`) |

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

## Out of scope

- Invitation emails or invite links; teammates are created directly by an Admin
- Permanently deleting a person's account
- A person belonging to more than one company
- Sign-in providers other than Google
- Two-factor authentication
- Custom roles beyond Admin, Operator and Viewer
- Choosing the company from a header or subdomain

## Non-functional requirements

| Area | Requirement | How it is checked |
| --- | --- | --- |
| security | Passwords are stored only as hashes, and passwords, temporary passwords, reset links and Google tokens never appear in logs, error messages or responses. | Review of logs and responses from registration, sign-in, reset, teammate creation and Google sign-in shows none of these values. |
| security | Every endpoint requires sign-in except sign-in, registration, Google sign-in and password reset, each marked public with a reason. In desktop/local mode, this is satisfied without a sign-in screen. | An automated test calls each endpoint without credentials and gets 'sign in required' except the listed public ones. |
| security | Accounts lock for 5 minutes after 5 wrong passwords. | A test shows the sixth attempt within 5 minutes is refused even with the correct password. |
| performance | Sign-in completes in under 1 second, and the team list opens in under 1 second for up to 50 people. | A timing test with 50 people in a company meets both limits. |
| reliability | A failed registration, Google first sign-in or teammate creation leaves no partial records. | Tests that force a failure mid-way find no company without a person and no person without a company. |
| accessibility | Sign-in, register, reset, Google and team screens are fully keyboard-operable, have visible labels, and show field errors beside the field. | A keyboard-only walkthrough completes each flow, and component tests check labels and error placement. |
| reliability | The app starts and its home page loads in both desktop/local mode and signed-in server mode. | A start-and-look check opens the home page and reads data successfully in both modes. |
| reliability | The example settings file, docker-compose and the app agree on the same list of settings. | An automated check compares the three lists and fails on any difference. |

## House rules

| Rule | Verdict | How |
| --- | --- | --- |
| PERF-001: Do no needless work and use only the memory, network, storage and startup time the feature needs: no repeated identical work, no loop over everything, no polling where an event exists, nothing loaded that is not shown, no unbounded read, query or cache, and everything opened (connection, listener, timer, file) is released. It must still behave when the data or the number of users is a hundred times larger. | passed | review |
| BOOT-001: The app must start and work as it was set up: its home page loads, sign-in behaves as configured (none when single-user), and reading data through the web app succeeds. | FAILED | automated: The app starts and works as set up (2 finding(s), 0 fixed) |
| SEC-001: No secrets (passwords, keys, tokens, connection-string passwords) in source code or committed config. | passed | automated: No secrets in code, No secrets in committed config |
| SEC-002: Never build SQL by string concatenation or interpolation. SQL text is a constant; every value is passed as a Dapper parameter (@name). | passed | automated: No interpolated or concatenated SQL |
| API-001: Every endpoint requires authorization (RequireAuthorization or a fallback policy) unless explicitly marked AllowAnonymous with a reason in a comment. | passed | review |
| API-002: Users can only read or change records they are allowed to see; ownership/tenant checks happen in the query, not after loading. | passed | review |
| API-003: Validate every write request (required fields, lengths, formats) and return RFC 7807 problem details with field errors on failure. | passed | review |
| API-004: Endpoints accept and return DTO records. Database row types never cross the HTTP boundary, and sensitive columns (password hashes, owner ids) are never returned. | passed | review |
| API-005: Every list endpoint is paginated (page + pageSize with a server-side maximum) and returns the total count. | passed | review |
| DB-001: Every schema change is a numbered migration pair in the project's own migrations folder (see the project map) (NNNN_name.up.sql and NNNN_name.down.sql). The down script undoes the up script. Never edit a migration that has been applied; add a new one. | passed | review |
| DB-002: Index foreign keys and every column used for filtering, sorting or uniqueness; enforce uniqueness with a unique index, not only in code. | passed | review |
| DB-003: Read queries select only the columns the screen needs into DTOs; list queries are paged in SQL (LIMIT/OFFSET); no N+1 queries (no query inside a loop: load related rows with a join, or one query for all the ids). | passed | automated: No SELECT * (read only the columns the screen needs) |
| DB-004: A search never starts with a wildcard (LIKE '%text' or LIKE CONCAT('%', ...)): it cannot use an index and reads the whole table. Search by prefix, or use a full-text index. | passed | automated: No search that starts with a wildcard |
| ARCH-001: Backend feature code lives in back-end/ShipRight.Server/Modules/<Feature>/ and frontend feature code in front-end/src/pages/<feature>/. Code used by two or more features lives in the Shared folders. Reuse before adding. | passed | review |
| ARCH-002: No new packages, frameworks or architectural patterns without an explicit decision recorded in the decision log. | passed | review |
| QUAL-001: No debugging leftovers: Console.WriteLine and Debugger.Break in the API, console.log in the web app. Use ILogger with structured messages. | passed | automated: No debugging leftovers (API), No debugging leftovers (web) |
| BUILD-001: The API and web app build without errors, all tests pass, and lint passes. | passed | automated: API compiles, Web app types are right, Web app lints and builds, API builds and all tests pass |
| UX-001: Every data screen handles loading, empty, error and success states explicitly. | passed | review |
| UX-002: Forms have visible labels, show field errors next to the field, disable submit while saving, and are fully keyboard-accessible. | passed | review (1 finding(s), 0 fixed) |
| TEST-001: Every requirement is proven by at least one test that names it. | passed | engine: build report validated |
| TEST-002: API behaviour is tested through HTTP (WebApplicationFactory) against a real MariaDB (the server named by the project's own test-database setting (see its README or test fixtures), for example the compose database), including unauthorized and not-found cases. | passed | automated: API builds and all tests pass |
| TEST-003: Web app behaviour is tested with Jest and React Testing Library: components are rendered the way a person sees them (loading, error, empty and success states), and logic such as API calls and hooks has unit tests. | passed | automated: Web app tests pass |
| SCOPE-001: Only files inside the design's declared scope may change. | FAILED | engine: diff checked against design scope (1 finding(s), 0 fixed) |
| USR-001: For the frontend use the following npm i jattac.libs.web.zest-responsive-layout, npm i jattac.libs.web.zest-file-upload, npm i jattac.libs.web.zest-tabs, npm i jattac.libs.web.responsive-table, npm i jattac.libs.web.zest-textbox, npm i jattac.libs.web.zest-sidekick-menu, npm i jattac.libs.web.zest-button, npm i jattac.libs.web.overflow-menu, and npm i jattac-web-poller in preference of any other components or native ui elements that may perform the same function | passed | automated: No native button (use zest-button), No native text input or textarea (use zest-textbox), No native table (use responsive-table), No react-tabs (use zest-tabs), No hand-rolled setInterval polling (use jattac-web-poller) (3 finding(s), 3 fixed) |

## Findings

| ID | Status | Cites | Description | Note |
| --- | --- | --- | --- | --- |
| CHK-app-boots | Advisory | BOOT-001 | Check 'The app starts and works as set up' is failing. | It could not be run, so nothing was checked here; it does not block this feature. |
| CHK-rule-USR-001-1 | Fixed | USR-001 | Automated check 'No native button (use zest-button)' failed. | check now passes |
| CHK-rule-USR-001-2 | Fixed | USR-001 | Automated check 'No native text input or textarea (use zest-textbox)' failed. | check now passes |
| CHK-rule-USR-001-3 | Fixed | USR-001 | Automated check 'No native table (use responsive-table)' failed. | check now passes |
| QA-1 | Open | R8 | R8 acceptance line 'Builds and deploys they started still show their name' has no test (and no build field carries the initiator), so history retention for switched-off people is unproven. |  |
| QA-2 | Open | R8 | R8 edge 'A switched-off person trying Google sign-in with the same email is also refused' is enforced only by an untested endpoint branch; the guard could break without any test failing. |  |
| QA-3 | Advisory | R3 | R3 acceptance 'If Google sign-in is cancelled or fails, the person returns to the sign-in screen with a plain message and can still use email and password' is only half-proven (failure via not-configured branch; cancellation and the return path untested). |  |
| QA-4 | Advisory | R9 | R9 acceptance 'The list shows loading, empty, error-with-retry and success states' is covered only for the success state. |  |
| QA-5 | Advisory | UX-002 | Keyboard operability and visible-label requirements are untested on the team and sign-in forms (R9 acceptance 'Every action works with the keyboard only, and every field has a visible label'). |  |
| QA-6 | Advisory | R12 | R12 acceptance 'The sign-in, register, reset and Google screens show loading, error and success states' is proven only for the sign-in screen; register/reset screens have no component tests. |  |
| QA-7 | Advisory | R6 | The reliability check for a failed teammate creation / Google first sign-in (no company without a person, no person without a company) is not proven by a forced-failure test. |  |
| QA-8 | Advisory | BOOT-001 | The feature's new read addresses have not been looked at by a running-app probe. |  |
| SEC-1 | Advisory |  | CSRF protection rests on SameSite=Lax alone; no antiforgery token is issued or checked. ISolated state-changing GET endpoints (db/backups/open-folder) became reachable cross-site under the new cookie scheme. Recommend SameSite=Strict for the auth cookie and/or server-side antiforgery validation, and move actions that mutate host state off GET. |  |
| SEC-2 | Advisory |  | Availability: IP-partitioned rate limits (including the R10 reset limiter) are not forwarded-header aware, so behind the reverse proxy common in a cloud deployment they degrade to a shared site-wide budget that one attacker can starve. Configure trusted-proxy forwarding (X-Forwarded-For) or partition on a subnet/account basis instead. |  |
| SEC-3 | Advisory |  | The session cookie is only marked Secure when the app itself observes an HTTPS request. In a TLS-terminating-proxy deployment without forwarded-headers configuration the cookie is transmitted in the clear over the local hop. Enforce Secure for the auth cookie in cloud mode (or set ForwardedHeaders/KnownProxies) before production. |  |
| SCOPE | Open | SCOPE-001 | Files outside the design's declared scope were changed. Revert them or keep changes inside scope. |  |

## Decisions

- People already have ShipRight accounts with passwords. When the new sign-in system goes live, should those people still be able to log in with their current email and password? → **Yes, keep existing accounts and passwords working** (builder)
- Today every account is either an admin or not (the first person who signs up for a company is the admin). Which roles should people be able to have inside a company? → **Admin, Operator and Viewer. Also ensure that for local/desktop mode, no auth is required and we don't loose access to our data or projects and that for a user on the desktop, this switch over is frictionless and transparent** (builder)
- When someone signs in with Google for the first time and has no ShipRight account yet, what should happen? → **Anyone can sign in with Google and a new company is created for them as its Admin** (builder)
- Today every sign-up creates a brand-new company, and there is no way to add a teammate to an existing one. With the new Operator and Viewer roles, how should an Admin get other people into their company? → **No teammates for now** (builder)
- Operator and Viewer exist as roles, but nobody can be given them yet. When they are used later, what should each be allowed to do in ShipRight? For example, can a Viewer see projects and builds but not start a deploy? → **Admin does everything; Operator can build and deploy but not change settings or servers; Viewer is read-only** (builder)
- Replace ShipRight's hand-built sign-in (own tokens, refresh tokens, password reset, IsAdmin flag) with ASP.NET Core Identity running on the project's existing SQL data access (no EF Core). Every person belongs to exactly one company (CompanyId) and has one role: Admin, Operator or Viewer. Existing accounts keep working with their current email and password. Anyone can also sign in with Google; a first-time Google person gets a brand-new company and becomes its Admin. Desktop/local mode needs no sign-in and switches over silently with all data and projects intact. There are no teammates for now, so every company has one person (its Admin) and no invite or user-management screen is built; Operator and Viewer are enforced by the server but cannot yet be given to anyone. Permissions: Admin does everything; Operator can build and deploy but not change settings or servers; Viewer is read-only. → **agreed to all but expand scope to included: Adding teammates, invites, or any screen to assign Operator or Viewer** (builder)
- You've asked for teammates to be added to a company. How should an Admin get a teammate in? For example, Sam (Admin) wants Priya to join as an Operator. → **Admin creates Priya's account directly: types her email and a temporary password, and picks her role** (builder)
- When an Admin no longer wants a teammate in the company (for example Priya leaves), what should "removing" her do? → **Switch her off: she can no longer sign in, but her account and history stay, and the Admin can switch her back on** (builder)
- Replace ShipRight's hand-built sign-in (own tokens, refresh tokens, password reset, IsAdmin flag) with ASP.NET Core Identity running on the project's existing SQL data access, without EF Core. Every person belongs to exactly one company (CompanyId) and has exactly one role: Admin, Operator or Viewer. Existing accounts keep working with their current email and password. Anyone can also sign in with Google; a first-time Google person gets a brand-new company and becomes its Admin. An Admin can add teammates by typing the teammate's email, a temporary password and a role, change a teammate's role, and switch a teammate off (and back on). The server enforces the permissions: Admin does everything; Operator can build and deploy but not change settings or servers; Viewer is read-only. Desktop/local mode needs no sign-in and switches over silently with all data and projects intact. All data stays scoped to the person's company. → **looks ok, but let's also strip down the required env vars to just what's in use in this version e.g if we are not using emailing functionality going forward just yet, using characterization tests so code does not break, remove the code and clean the expected env vars in both docker-compose, the app itself and the .env-example file to exactly what we need** (builder)
- You asked to remove the emailing code if we are not using it yet. Today the only thing ShipRight emails is the 'forgot password' link. If that goes, how should someone who forgets their password get back in? Example: Priya forgets her password. → **Keep the forgot-password email** (builder)
- Replace ShipRight's hand-built sign-in (own tokens, refresh tokens, password reset, IsAdmin flag) with ASP.NET Core Identity running on the project's existing SQL data access, without EF Core. Every person belongs to exactly one company (CompanyId) and has exactly one role: Admin, Operator or Viewer. Existing accounts keep working with their current email and password. Anyone can also sign in with Google; a first-time Google person gets a brand-new company and becomes its Admin. An Admin can add teammates by typing the teammate's email, a temporary password and a role, change a teammate's role, and switch a teammate off (and back on). The server enforces the permissions: Admin does everything; Operator can build and deploy but not change settings or servers; Viewer is read-only. Desktop/local mode needs no sign-in and switches over silently with all data and projects intact. All data stays scoped to the person's company. The forgot-password email stays, so its mail settings stay; every other setting that is no longer used (such as the old signing key) is removed from the app, docker-compose and the example settings file, and Google's settings are added, after tests lock in current behaviour. → **Looks right, start designing** (builder)
- The developer couldn't work on the Build stage because of this, not because of anything in your app. The AI service is limiting how fast this account can work. Wait a few minutes, then try again. → **Try again now** (builder)
- This feature has used $12.75 of its $10.00 limit. Raise the limit and continue, or stop? → **Raise the limit and continue** (builder)
- The build stage has been working for 71 minutes (the limit is 45). Let it keep going for another 45 minutes, or stop? → **Keep going** (builder)
- After 2 revision round(s), 3 blocking problem(s) remain. Give the developer one more round, or stop? → **Give one more round** (telegram:6096790972)
- The build stage has been working for 154 minutes (the limit is 45). Let it keep going for another 45 minutes, or stop? → **Keep going** (telegram:6096790972)
- The build stage has been working for 154 minutes (the limit is 45). Let it keep going for another 45 minutes, or stop? → **Keep going** (telegram:6096790972)
- The developer hasn't printed anything for 15 minutes. It may be busy with something slow, such as a long test run, or it may be stuck. You can answer here, or from another window with 'devteam decide <option>'. → **Keep waiting** (devteam, because the agent was last seen running a command and nobody answered in 5 minutes)
- The developer hasn't printed anything for 30 minutes. It may be busy with something slow, such as a long test run, or it may be stuck. You can answer here, or from another window with 'devteam decide <option>'. → **Stop it and try again** (devteam, because nobody answered in 20 minutes)
- The developer hasn't printed anything for 15 minutes. It may be busy with something slow, such as a long test run, or it may be stuck. You can answer here, or from another window with 'devteam decide <option>'. → **Stop it and try again** (devteam, because nobody answered in 5 minutes)
- The developer printed nothing for 15 minutes, so DevTeam stopped it. This is usually a hung session or a network problem, not something you did. Try the Build stage again, or stop? → **Stop this feature** (telegram:6096790972)

## Role documentation

### Analyst: requirements

Replace ShipRight's hand-built sign-in (own tokens, refresh tokens, password reset, IsAdmin flag) with ASP.NET Core Identity running on the project's existing SQL data access, without EF Core. Every person belongs to exactly one company (CompanyId) and has exactly one role: Admin, Operator or Viewer. Existing accounts keep working with their current email and password. Anyone can also sign in with Google; a first-time Google person gets a brand-new company and becomes its Admin. An Admin can add teammates by typing the teammate's email, a temporary password and a role, change a teammate's role, and switch a teammate off (and back on). The server enforces the permissions: Admin does everything; Operator can build and deploy but not change settings or servers; Viewer is read-only. Desktop/local mode needs no sign-in and switches over silently with all data and projects intact. All data stays scoped to the person's company. The forgot-password email stays, so its mail settings stay; every other setting that is no longer used (such as the old signing key) is removed from the app, docker-compose and the example settings file, and Google's settings are added, after tests lock in current behaviour.

### Architect: design

Cloud mode only: replace the hand-built JWT/refresh-token sign-in with ASP.NET Core Identity (framework-included, no EF Core) using a Dapper/QBuilder IUserStore, cookie sessions (7 days) and Google via the framework's generic AddOAuth handler (no new package, so ARCH-002 is not triggered). Desktop mode is untouched: it registers no auth, binds 127.0.0.1 and keeps its SQLite stores, so local data stays intact with no action from the user (R11). Findings that shape the design: (1) cloud Mariadb*Store classes currently write CompanyId = Guid.Empty and never filter by company, so R4 needs a small ICompanyContext (company from the signed-in person's claim, never from a header) that every cloud store applies in its WHERE and INSERT; background work (watch-branch startup, scheduler) runs inside an explicit SystemScope and everything else fails closed with no company. (2) Existing hashes are base64(16B salt + 20B PBKDF2-SHA256/100k); a LegacyAwarePasswordHasher verifies them and returns SuccessRehashNeeded so the next sign-in silently upgrades to Identity v3 (R1). (3) Tables: User gains NormalizedEmail (unique), Role (Admin/Operator/Viewer, one per person), IsActive, MustChangePassword, SecurityStamp, LockoutEnd, AccessFailedCount; new ExternalLogin(Provider, ProviderKey -> UserId) unique; RefreshToken and ResetToken are dropped (reset uses Identity's DataProtection tokens + the kept SMTP sender); IsAdmin/TokenVersion are dropped after backfill. Migration 0001 backfills every existing person as Admin (every legacy account was the sole owner of its company, so nobody loses access), moves legacy Guid.Empty domain rows to the oldest company, and is applied at cloud startup by a tiny idempotent runner; schema.sql is updated for fresh installs. (4) Roles: no RoleManager/role table (only three fixed roles); an IUserRoleStore adapter exposes the one Role column and a claims factory emits role + company_id claims. Enforcement is central, not per-endpoint: a fallback policy requires sign-in (endpoints marked AllowAnonymous with a reason comment: login, register, google, forgot/reset, health), plus one RoleAccessRules table (method + route prefix -> minimum role) evaluated by an authorization handler: Viewer = GET only; Operator = everything except changing projects settings, servers, resources, ssh keys, team; Admin = all. Rules are tested against every mapped route so a new route without a rule fails a test. (5) Sign-in: Identity lockout 5 failures/5 min, password >= 8, case-insensitive email, cookie 7 days with stamp validation every 1 minute (so switching someone off takes effect within a minute at one PK read per user per minute). Registration, Google first sign-in and teammate creation each insert Company+User inside one transaction (Google completion is a GET so it opens its own; POSTs reuse DatabaseTransactionMiddleware), so there are no partial records. The 'last active Admin' rule is checked in the same UPDATE (conditional on another active Admin existing) to avoid races. (6) Team list: GET /api/team?page&pageSize (default 20, max 50) ordered by name with index (CompanyId, Name), returns total; always CompanyId-filtered in SQL. (7) Settings after the change: MODE, TIME_ZONE, DB_CONNECTION, CORS_ORIGINS, PUBLIC_URL, SMTP_HOST/PORT/USERNAME/PASSWORD/FROM, plus GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET (optional so an existing deployment still starts; Google button hidden when unset). Removed: JWT_KEY, ADMIN_EMAIL, ADMIN_PASSWORD, and the System.IdentityModel.Tokens.Jwt package. Compose, .env.example and code are compared by an automated test. (8) Front end: tokens and refresh logic removed from ApiService/AuthStore (cookie is sent same-origin; CORS already allows credentials); AuthGuard, login, signup, forgot/reset pages keep their look and gain Google button, change-password screen for temporary passwords, and a new Team page (zest-responsive-table/textbox/button/overflow-menu per USR-001) shown to Admins only. Order of work (tests first): characterization tests for current sign-in/signup/reset, hash compatibility, CloudConfiguration, CloudDiRegistrar, ApiService/AuthGuard, then the new code, then deletion of the old pieces. Costs: no extra work in desktop mode; cloud adds one PK read per user per minute and one indexed company filter per query; no new background threads, timers or packages; startup adds one idempotent migration check. Open items for review: legacy rows with Guid.Empty are assigned to the oldest company (cannot be known precisely if several legacy companies exist); SshKeyStore is file-based per machine and is not company-scoped by this change.

- Touches: back-end/ShipRight.Server/Program.cs (cloud pipeline: Identity, cookie auth, authorization, remove JwtMiddleware/seed; desktop branch unchanged; expose Program for WebApplicationFactory), back-end/ShipRight.Server/CloudDiRegistrar.cs and CloudConfiguration.cs (drop JWT/admin seed, add Google, register Identity services), back-end/ShipRight.Server/Modules/Auth/** (old router, middleware, token/jwt/setup/password services and models replaced), back-end/ShipRight.Server/Modules/Auth/Services/PasswordResetEmail.cs (kept; link built from Identity reset token), All cloud Mariadb*Store classes (Projects, Servers, Builds, Resources/Stores x5, Scheduler history, WatchBranch history) to filter and insert by CompanyId, back-end/ShipRight.Server/Database/schema.sql plus new Database/Migrations pair, back-end/ShipRight.Server/ShipRight.Server.csproj (remove Jwt package), docker-compose.yml, .env.example, SETUP.md (settings list), front-end/src/shared/ApiService.ts, shared/auth/*, pages/_app.tsx, login, signup, forgot-password, reset-password, modules/AppShell/AppShell.tsx and their tests, back-end/ShipRight.Tests auth, config and DI tests (JwtMiddlewareTests replaced)
- Reuses: Rocket.Libraries.DatabaseIntegrator IDatabaseHelper + QBuilder (all SQL, no EF Core), DatabaseTransactionMiddleware for atomic POSTs, CompanyScopedModel and DatabaseReaderBase.ApplyTenantFilter pattern, SmtpPasswordResetEmailSender and PasswordResetEmail.BuildResetLink (forgot-password email kept), Existing Company table, CompanyId columns and idx_*_company indexes, Existing rate limiter, CORS policy with credentials, HealthRouter AppMode, useServerMode on the front end, Existing login/signup/forgot/reset pages, Auth.module.css, AuthGuard PUBLIC_PATHS, HoverTip, Microsoft.AspNetCore.Identity and Authentication.OAuth from the ASP.NET Core shared framework
- Adds: Modules/Identity: AppUser, DapperUserStore (IUserStore/Password/Email/Role/Lockout/SecurityStamp/Login), IdentityRepository, LegacyAwarePasswordHasher, claims factory, ICompanyContext + SystemScope, RoleAccessRules + handler, AuthEndpoints, TeamEndpoints, TeamRepository, DTO records and validators (RFC 7807), Migration 0001_identity up/down and a small idempotent MigrationRunner, Google sign-in via AddOAuth with Google endpoints and /api/auth/google, /google/callback, /google/complete, front-end Team page and module, change-password page, Google button, role-aware nav, WebApplicationFactory integration tests against MariaDB, characterization tests, settings-agreement test, jest tests
- Scope: back-end/ShipRight.Server/Program.cs, back-end/ShipRight.Server/CloudDiRegistrar.cs, back-end/ShipRight.Server/CloudConfiguration.cs, back-end/ShipRight.Server/ShipRight.Server.csproj, back-end/ShipRight.Server/Database/schema.sql, back-end/ShipRight.Server/Database/Migrations/**, back-end/ShipRight.Server/Database/MigrationRunner.cs, back-end/ShipRight.Server/Database/CloudDatabaseRegistrar.cs, back-end/ShipRight.Server/Modules/Auth/**, back-end/ShipRight.Server/Modules/Identity/**, back-end/ShipRight.Server/Modules/Projects/MariadbProjectStore.cs, back-end/ShipRight.Server/Modules/Servers/MariadbServerStore.cs, back-end/ShipRight.Server/Modules/Builds/MariadbBuildStore.cs, back-end/ShipRight.Server/Modules/Resources/Stores/Mariadb*.cs, back-end/ShipRight.Server/Modules/Scheduler/MariadbBackupHistoryStore.cs, back-end/ShipRight.Server/Modules/Scheduler/CloudSchedulerRegistrar.cs, back-end/ShipRight.Server/Modules/WatchBranch/MariadbWatchBranchHistoryStore.cs, back-end/ShipRight.Server/Shared/Tenancy/**, back-end/ShipRight.Tests/Modules/Auth/**, back-end/ShipRight.Tests/Modules/Identity/**, back-end/ShipRight.Tests/Modules/Deployment/CloudConfigurationTests.cs, back-end/ShipRight.Tests/Database/CloudDiRegistrarTests.cs, back-end/ShipRight.Tests/Database/CloudDatabaseRegistrarTests.cs, back-end/ShipRight.Tests/Database/Migration*Tests.cs, back-end/ShipRight.Tests/Modules/Tenancy/**, back-end/ShipRight.Tests/ShipRight.Tests.csproj, docker-compose.yml, .env.example, SETUP.md, front-end/package.json, front-end/package-lock.json, front-end/src/shared/ApiService.ts, front-end/src/shared/auth/**, front-end/src/shared/__tests__/ApiService.test.ts, front-end/src/pages/_app.tsx, front-end/src/pages/login.tsx, front-end/src/pages/signup.tsx, front-end/src/pages/forgot-password.tsx, front-end/src/pages/reset-password.tsx, front-end/src/pages/change-password.tsx, front-end/src/pages/team.tsx, front-end/src/modules/Team/**, front-end/src/modules/AppShell/**, front-end/src/pages/Styles/Auth.module.css, front-end/src/pages/Styles/Team.module.css

### Developer: build

Revision round 3: fixed only the two open R8 findings (plus a compile error left in the working tree). QA-2: added ASwitchedOffPerson_AtTheGoogleCallback_IsRefused_AndNoSessionIsCreated, which drives the real /api/auth/google/complete endpoint with an external (Google) cookie for a switched-off person whose email Google verified, and asserts the callback redirects to /login?error=switched_off and creates no session - so deleting the guard at GoogleSignIn.cs:113 now fails a test. To drive the callback without Google, the in-memory test host gained a Google-enabled mode and a test-only middleware that plants the external identity cookie from a header. QA-1: added BuildStartedByASwitchedOffTeammate_StillShowsTheirName (MariaDB suite), which starts a build as an Operator teammate, switches them off, and proves both GET /api/builds/{id} and the project's build history still show their name (BuildRecord.Initiator is snapshotted at start and stored in the build's Data JSON). Also removed a stray duplicate 'using System.Security.Claims;' and an unqualified System.* reference at the end of BuildRouter.cs that stopped the server compiling. No other behaviour changed. Full backend suite: 904 passed, 0 failed, 76 skipped (MariaDB-gated).

## Files changed

- `.env.example`
- `back-end/ShipRight.Server/CloudConfiguration.cs`
- `back-end/ShipRight.Server/CloudDiRegistrar.cs`
- `back-end/ShipRight.Server/Database/CloudDatabaseRegistrar.cs`
- `back-end/ShipRight.Server/Database/MigrationRunner.cs`
- `back-end/ShipRight.Server/Database/Migrations/0001_identity.down.sql`
- `back-end/ShipRight.Server/Database/Migrations/0001_identity.up.sql`
- `back-end/ShipRight.Server/Database/Migrations/0002_drop_legacy_auth.down.sql`
- `back-end/ShipRight.Server/Database/Migrations/0002_drop_legacy_auth.up.sql`
- `back-end/ShipRight.Server/Database/schema.sql`
- `back-end/ShipRight.Server/Modules/Auth/AuthRouter.cs`
- `back-end/ShipRight.Server/Modules/Auth/Middleware/JwtMiddleware.cs`
- `back-end/ShipRight.Server/Modules/Auth/Models/RefreshToken.cs`
- `back-end/ShipRight.Server/Modules/Auth/Models/ResetToken.cs`
- `back-end/ShipRight.Server/Modules/Auth/Services/JwtService.cs`
- `back-end/ShipRight.Server/Modules/Auth/Services/PasswordHelper.cs`
- `back-end/ShipRight.Server/Modules/Auth/Services/SetupService.cs`
- `back-end/ShipRight.Server/Modules/Auth/Services/TokenService.cs`
- `back-end/ShipRight.Server/Modules/Builds/BuildOrchestrator.cs`
- `back-end/ShipRight.Server/Modules/Builds/BuildRecord.cs`
- `back-end/ShipRight.Server/Modules/Builds/BuildRouter.cs`
- `back-end/ShipRight.Server/Modules/Builds/MariadbBuildStore.cs`
- `back-end/ShipRight.Server/Modules/Identity/AppClaimsPrincipalFactory.cs`
- `back-end/ShipRight.Server/Modules/Identity/AppUser.cs`
- `back-end/ShipRight.Server/Modules/Identity/AuthEndpoints.cs`
- `back-end/ShipRight.Server/Modules/Identity/DapperUserStore.cs`
- `back-end/ShipRight.Server/Modules/Identity/GoogleSignIn.cs`
- `back-end/ShipRight.Server/Modules/Identity/IdentityDtos.cs`
- `back-end/ShipRight.Server/Modules/Identity/IdentityRegistration.cs`
- `back-end/ShipRight.Server/Modules/Identity/IdentityRepository.cs`
- `back-end/ShipRight.Server/Modules/Identity/LegacyAwarePasswordHasher.cs`
- `back-end/ShipRight.Server/Modules/Identity/RegistrationService.cs`
- `back-end/ShipRight.Server/Modules/Identity/RoleAccessRules.cs`
- `back-end/ShipRight.Server/Modules/Identity/RoleAuthorizationHandler.cs`
- `back-end/ShipRight.Server/Modules/Identity/TeamEndpoints.cs`
- `back-end/ShipRight.Server/Modules/Identity/TeamRepository.cs`
- `back-end/ShipRight.Server/Modules/Projects/MariadbProjectStore.cs`
- `back-end/ShipRight.Server/Modules/Resources/Stores/MariadbCompanyScopedAwsProfileResourceStore.cs`
- `back-end/ShipRight.Server/Modules/Resources/Stores/MariadbCompanyScopedCredentialResourceStore.cs`
- `back-end/ShipRight.Server/Modules/Resources/Stores/MariadbCompanyScopedDockerRegistryResourceStore.cs`
- `back-end/ShipRight.Server/Modules/Resources/Stores/MariadbCompanyScopedPipelineResourceStore.cs`
- `back-end/ShipRight.Server/Modules/Resources/Stores/MariadbCompanyScopedScriptResourceStore.cs`
- `back-end/ShipRight.Server/Modules/Scheduler/CloudSchedulerRegistrar.cs`
- `back-end/ShipRight.Server/Modules/Scheduler/MariadbBackupHistoryStore.cs`
- `back-end/ShipRight.Server/Modules/Servers/MariadbServerStore.cs`
- `back-end/ShipRight.Server/Modules/WatchBranch/MariadbWatchBranchHistoryStore.cs`
- `back-end/ShipRight.Server/Program.cs`
- `back-end/ShipRight.Server/Shared/Tenancy/CompanyContext.cs`
- `back-end/ShipRight.Server/Shared/Tenancy/CompanyScope.cs`
- `back-end/ShipRight.Server/ShipRight.Server.csproj`
- `back-end/ShipRight.Tests/Database/CloudDiRegistrarTests.cs`
- `back-end/ShipRight.Tests/Database/MigrationScriptsTests.cs`
- `back-end/ShipRight.Tests/Database/MigrationUpgradeTests.cs`
- `back-end/ShipRight.Tests/Modules/Auth/AuthCharacterizationTests.cs`
- `back-end/ShipRight.Tests/Modules/Auth/JwtMiddlewareTests.cs`
- `back-end/ShipRight.Tests/Modules/Deployment/CloudConfigurationTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/CloudApiFactory.cs`
- `back-end/ShipRight.Tests/Modules/Identity/CloudApiIdentityTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/CloudApiPipelineTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/DesktopModeStartupTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/GoogleSignInTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/IdentityPipelineTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/IdentityValidationTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/InMemoryCloudFactory.cs`
- `back-end/ShipRight.Tests/Modules/Identity/LegacyAwarePasswordHasherTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/MariaDbTestDatabase.cs`
- `back-end/ShipRight.Tests/Modules/Identity/RoleAccessRulesTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/SettingsAgreementTests.cs`
- `back-end/ShipRight.Tests/Modules/Identity/TeamRepositoryTests.cs`
- `back-end/ShipRight.Tests/Modules/Tenancy/CompanyScopedStoreTests.cs`
- `back-end/ShipRight.Tests/Modules/Tenancy/RecordingDatabaseHelper.cs`
- `back-end/ShipRight.Tests/ShipRight.Tests.csproj`
- `docker-compose.yml`
- `front-end/src/modules/AppShell/AppShell.tsx`
- `front-end/src/modules/Team/TeamPage.module.css`
- `front-end/src/modules/Team/TeamPage.tsx`
- `front-end/src/modules/Team/__tests__/TeamPage.test.tsx`
- `front-end/src/pages/change-password.tsx`
- `front-end/src/pages/forgot-password.tsx`
- `front-end/src/pages/login.tsx`
- `front-end/src/pages/reset-password.tsx`
- `front-end/src/pages/signup.tsx`
- `front-end/src/pages/Styles/Auth.module.css`
- `front-end/src/pages/Styles/Team.module.css`
- `front-end/src/pages/team.tsx`
- `front-end/src/pages/_app.tsx`
- `front-end/src/shared/ApiService.ts`
- `front-end/src/shared/auth/AuthApi.ts`
- `front-end/src/shared/auth/AuthGuard.tsx`
- `front-end/src/shared/auth/AuthStore.ts`
- `front-end/src/shared/auth/index.ts`
- `front-end/src/shared/auth/__tests__/AuthApi.test.ts`
- `front-end/src/shared/auth/__tests__/AuthGuard.test.tsx`
- `front-end/src/shared/auth/__tests__/LoginPage.test.tsx`
- `front-end/src/shared/types/IBuildRecord.ts`
- `front-end/src/shared/__tests__/ApiService.test.ts`
- `SETUP.md`

All prompts, agent outputs and artifacts for this run are in the `transcripts/` and `artifacts/` folders next to this report.
