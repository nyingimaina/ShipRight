# MyApp

A web app with a worked example (Customers), and sign-in you can switch on when more than one person will use it. It was created by DevTeam from a vetted template, in the **fast MVP version: no database**. The base is already secure and tested, and DevTeam's agents add features on top of it.

| Part | What it is | Where |
| --- | --- | --- |
| API | ASP.NET Core minimal API on .NET 10 | `api/` |
| Web app | Next.js (App Router, TypeScript) | `web/` |
| Data | JSON files in the `data/` folder (users and customers) | created on first use |

## Run it

You need the .NET 10 SDK and Node 20.19 or newer. There is nothing to configure: no database, no passwords.

```
# API
dotnet run --project api/src

# Web app, in another window
cd web
npm install
npm run dev
```

Open http://localhost:3000. Or, with Docker: `docker compose up --build`.

## Where is my data?

In the `data/` folder next to the API (`users.json`, `customers.json`); in Docker, in the `api-data` volume. The files survive restarts. They are never committed to git.

This is good for a demo, or for a few people on one machine. It is **not a database**: only one copy of the app can use the files at a time, everything is held in memory, and it will slow down with many thousands of records. Passwords are stored hashed, but the files are not encrypted, so keep the folder private.

## Add a database later

When you are ready (more users, more data, a real deployment), start a normal DevTeam run and say what you want:

```
devteam run "add the database"
```

DevTeam works as it does for any feature: it asks you a few questions, then builds and checks the change. A ready-made MariaDB version of this same app is kept for it in `.devteam/reference/database/`, so the sign-in and Customers screens stay exactly as they are and your existing data is carried over. You will need a MariaDB to connect to; DevTeam explains how to set one up when it asks.

## Sign-in: one person by default

This app starts with **no sign-in**: it is for one person on their own computer, and nothing asks for a password. Behind the scenes everything you make belongs to a single built-in "local owner".

When more than one person will use it, turn sign-in on. The code for it (accounts, passwords, lockout after repeated wrong passwords) is already there, switched off:

```
AUTH_MODE=multi        (in your .env file, or as an environment variable; then restart the app)
```

The first person to register after that takes over everything that was made before, so nothing is lost. After that, each person sees only their own records. To keep everything you made out of the takeover, set `Auth__ClaimLocalData=false`.

Without sign-in anyone who can reach the app can read and change everything in it, so a deployment that other people can reach must use `AUTH_MODE=multi`. The app refuses to start in production mode without it. The Docker files in this folder only publish the ports to this computer (127.0.0.1) and allow single-user mode for that reason; change both if you share it.

## Tests

```
cd api
dotnet test
```

The tests go through real HTTP and keep their data in a throwaway folder, so they need nothing installed. The browser tests drive a real Chromium (once: `npx playwright install chromium`):

```
cd web
npm run e2e
```
