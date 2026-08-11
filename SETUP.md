# ShipRight — Setting Up for a Test Run (Guide for Humans)

This guide walks you through running ShipRight on your own computer so you can try it
before it goes live on a server. By the end you will have ShipRight running in your
browser at `http://localhost:5200`, complete with a login screen and a database.

**A few things to know before you start:**

- This guide assumes you already have two things installed and working:
  - **WSL** (Windows Subsystem for Linux) with Ubuntu
  - **Docker** (inside that Ubuntu)
- You do **not** need any programming knowledge. If you can copy and paste, you can do this.
- The app comes with its **own built-in database** (a small MariaDB). Any database you
  already have on your Windows computer is **not touched** — this is completely separate.
- If you get stuck at any step, jump to the [Troubleshooting](#troubleshooting) section at
  the bottom. Every step there has a matching problem-and-fix entry.

---

## Step 0 — Get the code onto your computer

First, you need a terminal for the Ubuntu half of your computer.

1. Press the **Windows key**, type **Ubuntu**, and press Enter. A black (or dark) window opens.
   This is called the **terminal**. Everything below happens in this window.
2. Copy this whole block, paste it into the terminal, and press **Enter**:

   ```bash
   git clone https://github.com/nyingimaina/ShipRight.git ~/shipright && cd ~/shipright && git checkout feature/multi-registry-ecr
   ```

   What it does, in plain English:
   - `git clone ...` downloads a copy of the ShipRight code into a folder called `shipright`.
   - `cd ~/shipright` steps into that folder.
   - `git checkout feature/multi-registry-ecr` switches to the latest version of the code.

   **What you should see:** a few lines of text, and when it finishes, the prompt returns.
   There is no error message. If you see "fatal:" or "error:" in red, see
   [Troubleshooting](#troubleshooting) entry **A**.

3. To check you're in the right place, type this and press Enter:

   ```bash
   pwd
   ```

   **What you should see:** a line ending in `/home/yourname/shipright`.

---

## Step 1 — Create your private settings file

ShipRight needs a small file of private settings (your login details and passwords).
This file is called `.env`. The dot at the start just makes it a hidden file — that's normal.

1. In the same terminal, type this and press Enter:

   ```bash
   cp .env.example .env
   ```

   This makes a copy of the template so you can fill in your own details.
   **What you should see:** nothing. That's fine — it worked.

2. Let the computer generate the secret "key" for you. Type this and press Enter:

   ```bash
   sed -i "s/CHANGE-ME-TO-A-RANDOM-64-CHAR-HEX-STRING/$(openssl rand -hex 32)/" .env
   ```

   This is a long random string that keeps logins safe. You never need to see or remember it.
   **What you should see:** nothing. Fine.

3. Now open the settings file in the built-in text editor:

   ```bash
   nano .env
   ```

   The file's contents appear on screen. Use the **up / down arrow keys** to move the cursor.

   Find these lines and change them (type over the text):

   | Line you will find             | What to type instead                                              |
   | ------------------------------ | ----------------------------------------------------------------- |
   | `SHIPRIGHT__ADMIN_EMAIL=`      | Your email address (this is your login)                           |
   | `SHIPRIGHT__ADMIN_PASSWORD=`   | A password you choose (at least 8 characters)                     |
   | `MYSQL_ROOT_PASSWORD=`         | Another password you choose — any password                        |
   | `MYSQL_PASSWORD=`              | Another password you choose — must be different from the one above |

   Do **not** change any other line. In particular, leave the line starting with
   `SHIPRIGHT__DB_CONNECTION` exactly as it is.

4. Saving and exiting the editor is the part people forget. Do it in this exact order:
   1. Press **Ctrl+O** (the letter O, not zero) — it asks "File Name to Write".
   2. Press **Enter** to confirm.
   3. Press **Ctrl+X** to exit back to the terminal.

**Tip:** don't use the same password for everything, and don't use these passwords on
anything important — this is only for trying the app.

---

## Step 2 — Build and start the app

This is the step that takes the longest the first time. The computer downloads all the
parts the app needs and builds it. This can take **several minutes** — that's normal.

1. Type this and press Enter:

   ```bash
   docker compose up --build -d
   ```

   - `docker compose` is the program that runs everything.
   - `--build` means "put the pieces together first".
   - `-d` means "run it in the background" so the terminal isn't locked up.

   **What you should see:** lots of scrolling text with lines like `Building...`,
   `Step 1/...`, `Downloading...`. The first time, it looks busy. Let it finish.

2. When the prompt comes back, check that everything is healthy:

   ```bash
   docker compose ps
   ```

   **What you should see:** two rows, one called `shipright-db` and one called `shipright-app`,
   with the word **healthy** or **running** next to them.

   If either row says `Exited` or `unhealthy`, see [Troubleshooting](#troubleshooting)
   entry **B**.

---

## Step 3 — Watch it start up

1. Look at the app's start-up messages:

   ```bash
   docker compose logs -f app
   ```

   **What you should see** (look for these lines):
   - `Starting in CLOUD mode`
   - `Created admin user your@email.com with company ...`

   The second line means your login was created. 

2. When you've seen those lines, press **Ctrl+C** to stop watching the log and return to
   the terminal prompt.

   If instead you see `JWT signing key is required`, see [Troubleshooting](#troubleshooting)
   entry **C**.

---

## Step 4 — Open it in your browser and log in

1. Open your normal web browser (Edge, Chrome, or Firefox).
2. Go to this address: **http://localhost:5200**

   **What you should see:** the ShipRight screen with a login form.

3. Log in with the **email** and **password** you set in Step 1.
   **What you should see:** the main ShipRight screen (no error message).

   If the page never loads, see [Troubleshooting](#troubleshooting) entry **D**.

---

## Step 5 — Try a few things

Give it a quick test drive:

1. Click around the app — look at the menu and open a couple of screens.
2. Log out and log back in again (to make sure login really works).
3. In your browser, go to **http://localhost:5200/api/health** — you should see a small
   page of text saying the app is healthy.

That's it — it's working.

---

## Daily use

Here are the few commands you'll ever need. All of them go in the Ubuntu terminal.

| What you want to do                     | Type this                          |
| --------------------------------------- | ---------------------------------- |
| Start the app (if it's stopped)         | `docker compose up -d`             |
| Stop the app (your data is kept)        | `docker compose down`              |
| See what's running                      | `docker compose ps`                |
| Watch the app's messages                | `docker compose logs -f app`       |
| See the database's messages             | `docker compose logs -f db`        |
| Start fresh and erase all test data     | `docker compose down -v`           |

**Careful with the last one:** `docker compose down -v` deletes everything — your projects,
builds, and the admin login. You'd have to log in with the same admin email/password from
Step 1 again (the app re-creates it automatically on next start). Only use it if you want a
completely clean slate.

---

## Troubleshooting

| #  | What you see                                   | Why it happens and what to do                                                                                                                                   |
| -- | ---------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| A  | `fatal:` or `error:` when cloning (Step 0)     | Probably a typo or no internet. Re-paste the whole block and try again. If it still fails, double-check you're online.                                          |
| B  | `Exited` or `unhealthy` in `docker compose ps` | Something didn't start. Look at the messages: `docker compose logs app` (and `docker compose logs db`). If a message says the port is busy, see entry E.         |
| C  | `JWT signing key is required`                  | The secret key step didn't take effect. In the terminal, run `sed -i "s/CHANGE-ME-TO-A-RANDOM-64-CHAR-HEX-STRING/$(openssl rand -hex 32)/" .env`, then `docker compose up -d --force-recreate` to start again. |
| D  | The browser can't open localhost:5200          | The app may not be running. In the terminal run `docker compose ps` — if a row is `Exited`, run `docker compose up -d` and wait a minute.                        |
| E  | `port is already allocated` / `5200` busy      | Another program is using the same port. Stop that program, or reset everything with `docker compose down` and try `docker compose up -d` again.                 |
| F  | Login says "wrong password"                    | You mistyped the password in Step 1, or the editor didn't save. Edit `.env` again (`nano .env`), save with Ctrl+O, Enter, Ctrl+X, then start fresh with `docker compose down -v` followed by `docker compose up -d`. |
| G  | A build step failed halfway through            | Usually a momentary internet hiccup. Simply run `docker compose up --build -d` again — it continues where it left off.                                           |
| H  | `docker: command not found`                    | Docker isn't installed in your Ubuntu terminal. Get it working first, then come back to Step 0.                                                                 |
| I  | "Created admin user" never appears in logs     | Your admin login wasn't set in `.env`. Edit `.env` (Step 1), save it, then start fresh with `docker compose down -v` and `docker compose up -d`.                 |
| J  | App keeps restarting with `exec ./ShipRight.Server: no such file or directory` | The container's base image was wrong in an older version of the code. Update the code (`git pull`), then rebuild with `docker compose up --build -d`.              |

Still stuck? The most common fix is a clean start: `docker compose down -v`, wait a moment,
then `docker compose up --build -d` and re-check Step 3.

---

## What this guide intentionally does NOT cover

- **Connecting to an existing database on your Windows computer.** This guide uses the
  app's own built-in database, which is also how the app will run in production.
- **Pushing to Amazon ECR / AWS from inside the app.** That part isn't ready in this
  version yet — it doesn't affect anything you just did.
