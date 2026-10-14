## This project's layout (found in its files; it wins over anything below)

- API: `back-end/ShipRight.Server` (built from `the project root`); features in `back-end/ShipRight.Server/Modules`. .NET 10.
- Web app: `front-end`; features in `front-end/src/pages`, pages in `front-end/src/app`.
- API tests: `back-end/ShipRight.Tests`.
- Web tests: jest. No browser test suite.
- Migrations: `back-end/ShipRight.Server/Database/Migrations`.
- Tenancy key: `CompanyId`.
- Where the text below names a folder or tool of the template and this list says otherwise, this list is right.

<!-- end of layout -->

This is a CRUD web app: people sign in, then add, change, look up and delete their own records. Write requirements in what-should-happen terms. Never say how to build it.

Decide each of these, and write it down as a requirement, an edge case or an assumption (never leave it silent):

- **Ownership.** Whose record is it? By default people see and change only their own (house rule API-002). Ask only if the feature needs sharing.
- **Fields.** Which are required, their maximum lengths and allowed characters, and the exact message the person sees next to the field when it is wrong (API-003, UX-002).
- **Duplicates.** Is the same value allowed twice (the same email, name or code), and for whom? The answer decides what error appears.
- **Double submit.** Pressing Save twice, or refreshing during a save, must not create two records.
- **Screens.** What the list shows when it is empty, while loading, and when loading fails (UX-001).
- **Lists.** If they can grow: paging, searching and sorting expectations, and which order is the default (API-005).
- **Deleting.** Gone for good, or kept? Does the person confirm first? What happens to things that point at it?
- **Dates, money, numbers.** Whose time zone, how they are shown, currency and rounding.
- **Personal data.** What is stored, who can see it, how long it is kept, and whether it may appear in logs or error messages.
- **Two people at once.** What happens if someone changes a record another person is editing?
- **Keyboard and screen readers.** Every action is possible without a mouse; fields have visible labels (UX-002).

Sensible defaults when the builder says "you decide" (write each one down as an assumption): 20 records per page; names up to 200 characters; email addresses compared without regard to capitals; people can only see their own records; deleting asks for confirmation; errors never show technical details.

Typical pitfalls to state as behaviour: a failed save leaves nothing half-created; a record that someone else owns looks exactly like one that does not exist; validation messages say what to fix, not what went wrong inside; lists never load everything at once; the same rule is enforced on the server and on the form.

Non-functional defaults for this stack: sign-in is required for everything except sign-in and registration; passwords are never shown or logged; a list of a thousand records opens in about a second; every screen works at phone width; the app keeps working (with a clear message) when the server is slow or unavailable.


## Who is signed in

This project has its own sign-in and tenancy. Follow what its existing endpoints and queries already do (the project map lists them): every query stays scoped the way the existing ones are, by `CompanyId`. Never read around that scope.



## About this existing project

This is an existing project that was not created by DevTeam. Extend it; never recreate or restructure it. Follow the conventions already in its code, and reuse what is already there.

- API (ASP.NET Core): project in `back-end/ShipRight.Server`, built from `.`. It has automated tests. It uses a MariaDB/MySQL database.
- Web app (Next.js, TypeScript): in `front-end`. Its scripts: dev, build, start, test, lint, generate-icon.
- Features live in: `back-end/ShipRight.Server/Modules`, `front-end/src/pages`.
- Shared code lives in: `back-end/ShipRight.Server/Shared`, `front-end/src/shared`.
