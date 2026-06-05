# ShipRight — Business Requirements Specification

| Field | Value |
|---|---|
| Document version | 1.0 |
| Status | Draft — Awaiting Developer Acknowledgement |
| Date | 2026-06-04 |
| Author | Jattac Systems |
| Audience | Third-party development team |
| Classification | Confidential |

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Problem Statement & Motivation](#2-problem-statement--motivation)
3. [Stakeholders & User Personas](#3-stakeholders--user-personas)
4. [Scope & Explicit Boundaries](#4-scope--explicit-boundaries)
5. [System Overview](#5-system-overview)
6. [Functional Requirements](#6-functional-requirements)
7. [Non-Functional Requirements](#7-non-functional-requirements)
8. [Technical Architecture](#8-technical-architecture)
9. [Data Models](#9-data-models)
10. [API Specification](#10-api-specification)
11. [UI/UX Specification](#11-uiux-specification)
12. [Integration Specifications](#12-integration-specifications)
13. [Security Requirements](#13-security-requirements)
14. [Logging & Observability Requirements](#14-logging--observability-requirements)
15. [Error Handling & Recovery](#15-error-handling--recovery)
16. [Known Pitfalls & Anti-Patterns](#16-known-pitfalls--anti-patterns)
17. [Glossary](#17-glossary)
18. [Future Considerations (v2+)](#18-future-considerations-v2)

---

## 1. Executive Summary

ShipRight is a self-hosted, locally-run deployment automation tool for software engineering teams who manage multi-service Docker applications deployed to Linux servers. It provides a browser-based graphical interface for executing, monitoring, and auditing the full build-and-deployment pipeline — from incrementing version numbers and building Docker images through to restarting services on a remote server via SSH.

ShipRight is designed from day one to run on Linux (specifically WSL — Windows Subsystem for Linux — during development, and EC2 Ubuntu in production). It has no dependency on Windows-native APIs. The tool serves a single authenticated user (the developer) on localhost and is not a multi-user SaaS product.

The primary goal of v1 is to eliminate manual, error-prone shell work and replace it with a reliable, auditable, and observable pipeline that logs everything, surfaces failures clearly, and maintains a complete history of every build and deployment event.

---

## 2. Problem Statement & Motivation

### 2.1 Current State (The Pain)

The development team currently deploys multi-service Docker applications through a sequence of manual steps performed across multiple terminals, operating system contexts (Windows and WSL), and remote SSH sessions. A typical deployment for the reference project (the SMS Gateway application) requires:

1. Manually editing `version.txt` in each service's source directory
2. Checking git status, committing changes, and tagging the release manually
3. Pushing the git tag and branch to the remote repository
4. Opening a WSL terminal and pulling the latest code in a separate directory that contains Docker Compose configuration files
5. Running `docker login` if the session has expired
6. Executing `docker build` for each service image, tagging with the new version
7. Executing `docker push` for each image
8. Opening an SSH session to the remote EC2 server using a PEM key file
9. Executing a `rebuild.sh` script on the server that pulls from git, stops running containers, removes old images, and starts new ones via `docker-compose up`

This process has the following documented failure modes:
- Steps are performed out of order, especially under pressure (e.g., pushing Docker images before committing version files)
- Version numbers fall out of sync between services (e.g., API at `0.1.4` but the compose file still references `0.1.3`)
- Git state is dirty at deploy time — in-progress work is unintentionally committed or left uncommitted
- Docker Hub authentication has expired silently, causing a push failure midway through the pipeline
- The SSH session times out during `rebuild.sh` execution, leaving the remote server in an indeterminate state
- There is no audit trail of what was deployed, when, by whom, or with what result
- Build output and deployment logs are ephemeral — lost when the terminal closes

### 2.2 Desired State (The Goal)

ShipRight replaces the above manual process with:
- A guided, wizard-driven UI that walks the developer through each step in the correct order
- Automatic detection of precondition failures (dirty git state, wrong branch, missing Docker credentials) before the pipeline begins
- Real-time streaming of all subprocess and SSH output to the browser
- Persistent, structured records of every build and deployment event
- A queryable history with full log replay for any past build or deployment
- Clear, actionable error messages when any step fails

---

## 3. Stakeholders & User Personas

### 3.1 Primary User — The Developer (Solo or Small Team)

**Profile:** A software developer who maintains one or more Docker-based applications and deploys them to Linux servers. Comfortable with the command line but wants to reduce cognitive overhead and eliminate error-prone manual steps.

**Goals:**
- Deploy confidently without having to remember the exact sequence of commands
- See what is currently deployed and when it was last deployed
- Diagnose deployment failures quickly from a browser tab without SSH-ing into the server

**Frustrations with today:**
- Must context-switch between Windows Explorer, WSL terminal, and SSH session
- No easy way to answer "what version is currently running in production?"
- A failed deployment midway through leaves things in an unknown state

### 3.2 Secondary Stakeholder — Future Platform/Ops Team

For v2+, ShipRight may be hosted on an EC2 server and used by multiple developers. The architecture must not preclude this. Specifically: no assumptions about Windows, no hardcoded localhost-only CORS (it must be configurable), no hardcoded file paths.

---

## 4. Scope & Explicit Boundaries

### 4.1 In Scope for v1

| Feature | Description |
|---|---|
| Project configuration UI | Create, read, update, delete project configurations via browser |
| Build pipeline | Version bump → git commit/tag/push → WSL pull → Docker build/push |
| Deployment pipeline | SSH to remote server → execute rebuild script → stream output |
| Real-time log streaming | Browser receives live stdout/stderr from all subprocesses and SSH |
| Build & deploy history | Persistent record of every pipeline run with full log |
| History query API | Filter history by project, status, date range |
| Health endpoint | Verify ShipRight backend is running |
| Disk-based persistence | JSON file store for projects and build records |

### 4.2 Explicitly Out of Scope for v1

The following are **not** to be built in v1. Any implementation of these items will be considered scope creep and rejected.

| Feature | Why Deferred |
|---|---|
| User authentication / login | ShipRight runs on localhost only in v1; auth will be added when it moves to a shared server |
| Multi-user support | Single developer tool in v1 |
| Database (MariaDB/PostgreSQL) | Disk persistence is sufficient; DB is a v2 upgrade path |
| Notification integrations (Slack, email) | Valuable but not MVP |
| Rollback pipeline | Triggering a deploy of a previous tag via the UI |
| Scheduled / automated deploys | No CI/CD trigger yet |
| Log search / full-text query | Future; disk-based JSON store is queryable by fields only |
| Container health monitoring | Post-deploy health checks are out of scope |
| Secret rotation | SSH keys and Docker credentials are managed externally |
| Windows installer / MSI | Launcher script is sufficient |

### 4.3 The Provider Pattern (Critical Architectural Constraint)

The persistence layer **must** be built behind interfaces (`IProjectStore`, `IBuildStore`). The v1 disk-based implementation is `JsonProjectStore` and `JsonBuildStore`. These must be drop-in replaceable with MariaDB implementations without any changes to callers. Any code that directly reads or writes to disk files (other than the `JsonProjectStore` and `JsonBuildStore` classes themselves) will be rejected.

**Rationale:** The team has learned from experience that persistence implementations tightly coupled to callers require full rewrites when swapping backends. The interface boundary is the contractual guarantee.

---

## 5. System Overview

### 5.1 High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Developer's Machine (Windows + WSL Ubuntu)                     │
│                                                                 │
│  ┌───────────────────────────────────────────┐                  │
│  │  WSL (Ubuntu)                             │                  │
│  │                                           │                  │
│  │  ┌─────────────────────┐                  │                  │
│  │  │  ShipRight Backend  │◄── run.sh        │                  │
│  │  │  (ASP.NET Core 8)   │                  │                  │
│  │  │  Port 5200          │                  │                  │
│  │  │                     │                  │                  │
│  │  │  ┌───────────────┐  │                  │                  │
│  │  │  │   wwwroot/    │  │  Serves static   │                  │
│  │  │  │  (Next.js     │  │  files           │                  │
│  │  │  │   SSG output) │  │                  │                  │
│  │  │  └───────────────┘  │                  │                  │
│  │  └─────────┬───────────┘                  │                  │
│  │            │ SignalR / HTTP                │                  │
│  │            ▼                              │                  │
│  │  ┌─────────────────────┐                  │                  │
│  │  │  Windows Browser    │                  │                  │
│  │  │  localhost:5200     │                  │                  │
│  │  └─────────────────────┘                  │                  │
│  │                                           │                  │
│  │  ~/.shipright/                            │                  │
│  │    projects.json                          │                  │
│  │    builds/{id}.json                       │                  │
│  └───────────────────────────────────────────┘                  │
└─────────────────────────────────────────────────────────────────┘
           │ docker push
           │ git push
           ▼
┌──────────────────────────────┐
│  Docker Hub Registry         │
│  (nyingi/* images)           │
└──────────────────────────────┘
           │ SSH (Renci.SshNet)
           ▼
┌──────────────────────────────┐
│  EC2 Ubuntu Server           │
│  ubuntu@3.130.65.46          │
│  → rebuild.sh                │
│    → docker compose up       │
└──────────────────────────────┘
```

### 5.2 Runtime Model

ShipRight is started by executing `run.sh` from within WSL. This script:

1. Starts the ShipRight .NET backend process in the background
2. Waits up to 5 seconds for the backend to bind to port 5200 (poll with `curl`)
3. Opens the browser using `wslview http://localhost:5200` (WSL utility that hands off to the Windows default browser)
4. Tails the log file so the developer can see backend output in the terminal

The backend serves both the REST API and the Next.js static frontend from a single process on a single port. There is no separate Node.js process at runtime — the frontend is pre-built into static HTML/JS/CSS and copied into the backend's `wwwroot/` directory.

**Why single-port, single-process?**
This eliminates CORS issues between frontend and backend during development, simplifies firewall rules on EC2, and means the tool has a single process to monitor and restart.

### 5.3 Technology Stack

| Layer | Technology | Version | Rationale |
|---|---|---|---|
| Backend language | C# | .NET 8 LTS | Long-term support, performance, strong typing, rich ecosystem |
| Backend framework | ASP.NET Core | 8.0 | Minimal API support, built-in SignalR, excellent Linux support |
| Frontend framework | Next.js | 14.2.x | SSG support via `output: 'export'`, large ecosystem |
| Frontend language | TypeScript | 5.x | Type safety reduces runtime errors in UI state management |
| Frontend runtime | None (SSG) | — | No Node.js needed at runtime; HTML/JS/CSS only |
| Real-time comms | SignalR (ASP.NET Core) | 8.0 | First-class .NET support; browser client via `@microsoft/signalr` |
| SSH client | Renci.SshNet | Latest stable | Pure .NET, no system SSH dependency, handles PEM directly |
| Structured logging | Serilog | 10.x | Structured (queryable) log output; pluggable sinks |
| Persistence (v1) | JSON files on disk | — | Zero dependencies; provider interface ensures safe swap to DB |
| UI component library | Jattac internal libs | See §8.3 | Project's standard component set |
| Animation | framer-motion | 12.x | Production-grade animation; pairs with React |
| Target OS (runtime) | Linux (WSL Ubuntu / EC2) | Ubuntu 22.04+ | Cross-platform from day one |

---

## 6. Functional Requirements

### 6.1 FR-001: System Startup

**Description:** ShipRight must start via a shell script and open the browser automatically.

**Detailed behaviour:**

The file `back-end/ShipRight/run.sh` must:
```bash
#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_FILE="$HOME/.shipright/logs/shipright.log"
mkdir -p "$(dirname "$LOG_FILE")"

# Start backend
nohup "$SCRIPT_DIR/ShipRight" > "$LOG_FILE" 2>&1 &
BACKEND_PID=$!
echo "ShipRight started (PID $BACKEND_PID)"

# Wait for backend to be ready (max 10 seconds)
for i in $(seq 1 20); do
  if curl -sf http://localhost:5200/api/health > /dev/null 2>&1; then
    echo "Backend ready."
    break
  fi
  sleep 0.5
done

# Open browser (WSL-specific; on EC2 omit this line)
wslview http://localhost:5200 2>/dev/null || true

# Tail the log
tail -f "$LOG_FILE"
```

**Acceptance criteria:**
- Running `bash run.sh` from WSL starts the backend and opens the browser within 10 seconds
- If the backend fails to start, the script reports an error and exits with code 1
- The script is idempotent: running it when ShipRight is already running should notify the user rather than starting a second instance (check for existing process on port 5200)

**Pitfall:** Do not use `&& wslview` chaining — `wslview` must be called regardless of the tail command. Use the structure shown above.

---

### 6.2 FR-002: Health Endpoint

**Endpoint:** `GET /api/health`

**Response (HTTP 200):**
```json
{
  "status": "healthy",
  "version": "1.0.0",
  "startedAt": "2026-06-04T10:00:00Z",
  "dataDirectory": "/home/ubuntu/.shipright",
  "projectCount": 2,
  "buildCount": 47
}
```

**Purpose:** Used by `run.sh` to detect when the backend is ready, and by operators to verify the service is running correctly.

**Implementation note:** This endpoint must respond even if the data directory is empty or projects.json does not yet exist. `projectCount` and `buildCount` return `0` in that case.

---

### 6.3 FR-003: Project Configuration — CRUD

Projects are the top-level entities in ShipRight. A project represents a deployable system consisting of one or more Docker services, a git repository, a WSL working directory, and a remote deployment server.

#### 6.3.1 Project Config Data Shape

See §9 (Data Models) for the full definition. A project contains:

- **General:** id, name
- **Services:** one or more service definitions (name, version file path, build context path, Docker image name)
- **Git:** repository path on the local filesystem, deploy branch name
- **WSL:** working directory path (Linux path, e.g. `/home/nyingi/work/jattac/docker/jattac.sms.gateway.docker`)
- **Server:** SSH host, username, SSH key file path, remote working directory, rebuild script name

#### 6.3.2 Config UI Requirements

The UI must provide the following pages for project management:

**Project list page (`/projects`)**
- Displays all configured projects as cards
- Each card shows: project name, number of services, last build status badge (or "Never built" if no history), last build timestamp
- "New Project" primary button in the top-right
- "Edit" and "Delete" actions on each card
- Delete must show a confirmation dialog before proceeding

**Project create/edit page (`/projects/new` and `/projects/[id]/edit`)**
- Tabbed layout with four tabs: General, Services, Git & WSL, Server
- All fields validated before save (see §6.3.3)
- "Save" button visible at all times (not just on the last tab)
- On save success: redirect to project detail page
- On save failure: display field-level validation errors

**Tab 1 — General:**
- Project Name (required, max 100 chars, unique across all projects)

**Tab 2 — Services:**
- Repeating section: add/remove service entries
- Each service entry:
  - Service Name (required, max 100 chars, e.g. "API", "Web UI")
  - Version File Path (required, must be a valid file path, e.g. `/mnt/d/work/nyingi/code/systems/sms-gateway/jattac.app.sms.gateway/version.txt` in WSL or `D:\work\...` in Windows notation — the backend normalises these)
  - Build Context Path (required, must be a valid directory path — this is the directory passed to `docker build`)
  - Docker Image Name (required, format: `[registry/]namespace/name`, e.g. `nyingi/jattac-sms`. No tag — tags are applied at build time)
- Minimum 1 service per project. Maximum 10.
- Services are ordered; the order determines build sequence

**Tab 3 — Git & WSL:**
- Git Repository Path (required, must be a valid directory containing a `.git` folder — backend validates this on save)
- Deploy Branch (required, default: `master`)
- WSL Working Directory (required, Linux absolute path, e.g. `/home/nyingi/work/jattac/docker/...`)

**Tab 4 — Server:**
- Host (required, IP address or hostname, e.g. `3.130.65.46`)
- Username (required, e.g. `ubuntu`)
- SSH Key Path (required, path to PEM file, e.g. `/home/nyingi/work/jattac/clouds-ssh/jattac-sms-gateway/rocket_documents.pem`)
- Remote Working Directory (required, absolute path on the remote server where `rebuild.sh` lives, e.g. `/home/ubuntu/jattac-sms-gateway-docker`)
- Rebuild Script Name (required, default: `rebuild.sh`)

#### 6.3.3 Server-Side Validation on Save

The backend must validate the following before persisting a project config. Each failed validation returns a descriptive error message:

| Field | Validation |
|---|---|
| Project Name | Non-empty, ≤100 chars, unique (case-insensitive) |
| Service Name | Non-empty, ≤100 chars, unique within project |
| Version File Path | File must exist and be readable |
| Build Context Path | Directory must exist |
| Docker Image Name | Matches regex `^[a-z0-9._\-\/]+$`, no tag suffix (reject if `:` present) |
| Git Repository Path | Directory must exist and contain a `.git` subdirectory |
| Deploy Branch | Non-empty, ≤100 chars |
| WSL Working Directory | Must be a valid absolute Linux path (starts with `/`) |
| Host | Non-empty, ≤253 chars |
| Username | Non-empty, ≤100 chars, no spaces |
| SSH Key Path | File must exist and be readable; must not be world-readable (permissions check: warn if `chmod 644` or looser) |
| Remote Working Directory | Non-empty, starts with `/` |
| Rebuild Script Name | Non-empty, ≤100 chars, no path separators |

**Critical:** Validate SSH key file permissions. A common failure mode is an SSH key with permissions `644` which OpenSSH and Renci.SshNet will refuse. The API must warn the user if the key file permissions are too permissive (readable by group or others). The warning must state the problem and the fix (`chmod 600 {keyPath}`).

---

### 6.4 FR-004: Version Reading

Before initiating a build, ShipRight reads the current version from each service's `version.txt` file and presents it to the user for confirmation and optional editing.

**`version.txt` format:** A single line containing a semantic version string in the format `MAJOR.MINOR.PATCH` (e.g. `0.1.4`). The file may have a trailing newline, which must be stripped before parsing.

**Version suggestion logic:**
- Default suggestion is to increment the PATCH component by 1 (e.g. `0.1.4` → `0.1.5`)
- The UI presents the suggested version in a ZestTextbox input pre-filled with the suggestion
- The user may change the version to any valid semver string
- The backend validates the user-supplied version before the pipeline starts: must match `^\d+\.\d+\.\d+$`
- If the user-supplied version is lower than or equal to the current version, the backend must warn (not block) — a developer may have legitimate reasons to re-deploy the same version

**Pitfall:** Do not parse the version file on the frontend by calling a filesystem API. The backend must read the file and return the current version via the API. The frontend only displays what the backend returns.

---

### 6.5 FR-005: Build Pipeline

The build pipeline is a sequential, multi-step process. Each step must be logged in full. Any step that fails must abort the pipeline and mark the build as `failed`. The developer must be clearly informed which step failed and why.

The pipeline is triggered by `POST /api/builds/start` with the confirmed version numbers.

#### Step 1 — Precondition Check

Before any file is modified, the following checks must pass. If any fail, the build is not started and the user is presented with the specific failure:

- All configured `version.txt` files are readable
- All configured build context paths exist
- Git repository path exists and contains `.git`
- SSH key file exists
- Docker socket is reachable (attempt `docker info`; if it fails, report "Docker daemon not running or not accessible")

#### Step 2 — Git Status Check

```bash
git -C {repoPath} status --porcelain
```

If the output is non-empty (dirty working tree), the pipeline pauses and emits an event to the frontend requesting user confirmation:

```json
{
  "type": "pause",
  "reason": "git_dirty",
  "detail": "Uncommitted changes detected in {repoPath}",
  "prompt": "Would you like to commit all changes before building?",
  "options": ["commit", "abort"]
}
```

If the user chooses `commit`, they must provide a commit message (non-empty). ShipRight then executes:
```bash
git -C {repoPath} add -A
git -C {repoPath} commit -m "{userMessage}"
```

If the user chooses `abort`, the build is marked `aborted` and no further steps execute.

**Important:** ShipRight does **not** automatically commit with a generated message. A commit message is a human responsibility. ShipRight will only supply a default placeholder if the field is submitted empty, and the placeholder must make it obvious it was auto-generated (e.g. `"[ShipRight auto-commit] Pre-build snapshot"`).

#### Step 3 — Branch Check

```bash
git -C {repoPath} rev-parse --abbrev-ref HEAD
```

If the current branch is not equal to `deployBranch` (from project config), the pipeline pauses:

```json
{
  "type": "pause",
  "reason": "wrong_branch",
  "detail": "Currently on branch '{currentBranch}', deploy branch is '{deployBranch}'",
  "prompt": "Switch to {deployBranch} and continue?",
  "options": ["switch", "abort"]
}
```

If the user chooses `switch`:
```bash
git -C {repoPath} checkout {deployBranch}
git -C {repoPath} pull origin {deployBranch}
```

If either command fails (e.g. checkout fails due to conflicts), the pipeline aborts with the full git error output surfaced to the user. ShipRight **must not** force-checkout, stash, or discard changes automatically.

#### Step 4 — Write Versions & Git Tag

For each service, write the new version to its `version.txt`:
```
{newVersion}\n
```
(single line with trailing newline — consistent with Unix text file convention)

Then:
```bash
git -C {repoPath} add {versionFile1} {versionFile2} ...
git -C {repoPath} commit -m "chore: bump versions — {serviceVersionSummary}"
```

Where `{serviceVersionSummary}` is e.g. `API 0.1.5, Web UI 0.1.4`.

Create an annotated git tag:
```bash
git -C {repoPath} tag -a "b_{apiVer}_f_{uiVer}" -m "Build {date}: API {apiVer}, Web UI {uiVer}"
```

**Tag naming convention:** For a project with services named "API" and "Web UI", the tag format is `b_{apiVersion}_f_{webUiVersion}`. For projects with different service names, the tag format must be configurable (advanced, v2) or use a generic format: `ship_{timestamp}` as a fallback.

Push to remote:
```bash
git -C {repoPath} push origin {deployBranch} --follow-tags
```

`--follow-tags` pushes the annotated tag in the same operation. This is preferred over two separate push commands.

**Critical:** Wait for the push to complete with exit code 0 before proceeding. A common failure here is a network error or a remote rejection (e.g. non-fast-forward). The full git error output must be captured and displayed.

#### Step 5 — Compose Repo Sync

`wslWorkingDir` and `repoPath` are **separate standalone git repositories**. The compose repo contains `docker-compose.yml` and `rebuild.sh`. `rebuild.sh` on the EC2 server does `git restore ./docker-compose.yml && git pull && docker-compose up`, which means the server deploys whatever version tags are committed in the compose repo. If this file is not updated, the server always deploys the previously committed version regardless of what was just built.

Step 5 must:

1. Pull first — get any changes from teammates before modifying:
```bash
git -C {wslWorkingDir} pull origin {deployBranch}
```

2. Parse `{wslWorkingDir}/docker-compose.yml` and rewrite the image tag for each service to the new version. For example, `nyingi/jattac-sms:0.1.4` → `nyingi/jattac-sms:0.1.5`. Match services by `DockerImageName` (without tag) from the project config.

3. Commit and push the updated compose file:
```bash
git -C {wslWorkingDir} add docker-compose.yml
git -C {wslWorkingDir} commit -m "chore: deploy — {serviceVersionSummary}"
git -C {wslWorkingDir} push origin {deployBranch}
```

**Why pull before modify?** In a distributed team where multiple developers share the same compose repo remote, pulling first prevents committing on a stale base and surfacing merge conflicts clearly rather than silently overwriting teammates' changes.

**Critical:** The docker-compose.yml update must use a YAML-aware parser or a targeted line replacement that matches only the image fields for the specific services in this project. Do not use naive string replacement that could corrupt unrelated services (e.g. the database container or nginx).

#### Step 6 — Docker Login Check

Check `~/.docker/config.json` for credentials for `docker.io` or `index.docker.io`. If credentials exist, skip this step.

If credentials are absent or the check is inconclusive, the pipeline pauses:

```json
{
  "type": "pause",
  "reason": "docker_login_required",
  "prompt": "Docker Hub credentials required. Please enter your username and password.",
  "fields": ["username", "password"]
}
```

Execute `docker login -u {username} --password-stdin` with the password piped to stdin. **Never pass the password as a command-line argument** — it would be visible in the process list and in logs. See §13 (Security) for specifics.

After successful login, continue the pipeline.

#### Step 7 — Docker Build, Tag & Push (per service)

For each service in order:

```bash
docker build \
  -t {dockerImageName}:{newVersion} \
  -t {dockerImageName}:latest \
  {buildContextPath}
```

```bash
docker push {dockerImageName}:{newVersion}
docker push {dockerImageName}:latest
```

**All stdout and stderr from these commands must be streamed in real-time to the frontend via SignalR.** Docker build output includes build layer progress which is valuable for diagnosing failures. Do not buffer the output — stream it as it is produced.

**Why `latest` tag?** The `latest` tag is a Docker convention. Although `rebuild.sh` references explicit version tags, pushing `latest` ensures tools that pull the image by name (without a tag) get the current version. It also provides a quick human reference for "what is the most recent image."

#### Step 8 — Build Complete

Set `BuildRecord.status = "build_succeeded"`.
Set `BuildRecord.completedAt` to current UTC time.
Set `BuildRecord.gitTag` to the annotated tag created in Step 4.
Persist the record to disk.
Emit a completion event to the frontend.

The "Deploy" button on the build detail page becomes active once this status is reached.

---

### 6.6 FR-006: Deployment Pipeline

Deployment is a **separate user-initiated action**, distinct from the build. The developer reviews the build record and explicitly chooses to deploy it.

**Why separate?** It is common practice to build and test before deploying. A successful build does not automatically mean the developer wants to deploy immediately. The separation also means a build can be deployed multiple times (e.g. re-running the same rebuild.sh after a server configuration change).

**Trigger:** `POST /api/builds/{buildId}/deploy`

#### Step 1 — SSH Connection

Establish an SSH connection to `{server.host}` on port 22 as `{server.username}` using the PEM key at `{server.sshKeyPath}`.

Use `Renci.SshNet` for this. The connection must be established with:
- Private key authentication only (no password authentication)
- Host key verification enabled (do not set `RejectUnknownHosts = false`)
- A connection timeout of 30 seconds

If the connection fails, the deployment is marked `deploy_failed` with the SSH error message.

#### Step 2 — Execute Rebuild Script

```
cd {server.remoteWorkingDir} && bash {server.rebuildScript}
```

This is executed as a single command over the SSH connection. All stdout and stderr must be streamed to the frontend via SignalR in real-time.

**The `rebuild.sh` script (for reference — it lives on the server, not in ShipRight):**
```bash
#!/bin/bash
git restore ./docker-compose.yml
git restore ./rebuild.sh
git pull
docker stop jattac-sms-api
docker rm jattac-sms-api
docker stop jattac-sms-web-ui
docker rm jattac-sms-web-ui
docker stop jattac-nginx
docker rm jattac-nginx
docker image prune -a -f
docker-compose up -d --build --remove-orphans
```

ShipRight does not modify or own this script. It is managed separately on the server. ShipRight only executes it via SSH.

#### Step 3 — Deployment Complete

On SSH command exit code 0:
- Set `BuildRecord.status = "deployed"`
- Set `BuildRecord.deployedAt` to current UTC time
- Append deployment log to `BuildRecord.logOutput`
- Persist record

On non-zero exit code:
- Set `BuildRecord.status = "deploy_failed"`
- Append full SSH output to `BuildRecord.logOutput`
- Persist record

---

### 6.7 FR-007: Build & Deployment History

#### 6.7.1 History List

**Endpoint:** `GET /api/builds`

Supports the following query parameters:

| Parameter | Type | Description | Default |
|---|---|---|---|
| `projectId` | string | Filter by project ID | All projects |
| `status` | string | Filter by status (comma-separated for multiple) | All statuses |
| `from` | ISO 8601 datetime | Builds started on or after | None |
| `to` | ISO 8601 datetime | Builds started on or before | None |
| `page` | integer | Page number (1-based) | 1 |
| `pageSize` | integer | Records per page | 20, max 100 |

**Response:**
```json
{
  "items": [ /* array of BuildRecord (without logOutput) */ ],
  "totalCount": 47,
  "page": 1,
  "pageSize": 20,
  "totalPages": 3
}
```

**Note:** The `logOutput` field is intentionally excluded from list responses to keep payloads small. It is only returned from the single-record endpoint.

#### 6.7.2 Build Detail

**Endpoint:** `GET /api/builds/{id}`

Returns the full `BuildRecord` including `logOutput`.

#### 6.7.3 Build Log (raw text)

**Endpoint:** `GET /api/builds/{id}/log`

Returns the full log as `text/plain`. Useful for downloading or grepping.

#### 6.7.4 Dashboard Summary

**Endpoint:** `GET /api/projects/{id}/summary`

Returns for each project:
- Current version of each service (from `version.txt`)
- Last build record (status, tag, timestamp)
- Last successful deployment (timestamp, tag)
- Build success rate for the last 10 builds (percentage)

---

### 6.8 FR-008: Real-Time Log Streaming

ShipRight uses SignalR (WebSocket with HTTP fallback) to stream build and deployment output to the browser in real-time.

**Hub URL:** `/hubs/build`

**Client subscription:** The client connects to the hub and calls `JoinBuild(buildId)` to subscribe to events for a specific build. When navigating away, the client calls `LeaveBuild(buildId)`.

**Event types emitted by server to client:**

| Event | Payload | Description |
|---|---|---|
| `LogLine` | `{ buildId, source, line, timestamp }` | A single line of stdout/stderr from a subprocess or SSH session. `source` is one of: `git`, `docker`, `ssh`, `shipright` |
| `StepStarted` | `{ buildId, stepNumber, stepName }` | A pipeline step has begun |
| `StepCompleted` | `{ buildId, stepNumber, stepName, success }` | A pipeline step has finished |
| `PauseRequested` | `{ buildId, reason, prompt, options, fields? }` | Pipeline paused, awaiting user input |
| `BuildCompleted` | `{ buildId, status, gitTag? }` | Build pipeline finished (success or failure) |
| `DeployCompleted` | `{ buildId, status }` | Deployment finished |

**Client-side requirement:** The frontend must handle reconnection gracefully. If the SignalR connection drops during a build:
1. Attempt reconnection with exponential backoff (1s, 2s, 4s, 8s, max 30s)
2. On reconnection, re-subscribe to the build by calling `JoinBuild(buildId)`
3. Show the user a "Reconnecting…" indicator during the gap
4. On successful reconnection, call `GET /api/builds/{id}` to catch up on any missed log lines (the log is accumulated server-side in `BuildRecord.logOutput`)

---

### 6.9 FR-009: Pipeline Pause & User Response

When the pipeline requires user input (Steps 2, 3, 6 of the build pipeline), it must pause execution and wait for a response via a REST endpoint. It must not time out while waiting for user input (the developer may be reading output carefully before deciding).

**Response endpoint:** `POST /api/builds/{buildId}/respond`

```json
{
  "reason": "git_dirty",
  "choice": "commit",
  "data": {
    "commitMessage": "Fix: correct phone number validation logic"
  }
}
```

The `reason` field must match the `reason` from the `PauseRequested` event. The backend must validate that the build is in a `paused` state before processing the response.

---

## 7. Non-Functional Requirements

### 7.1 NFR-001: Reliability & Fault Tolerance

**NFR-001-A: Build state must be persisted before each step.**

The `BuildRecord` must be written to disk at the beginning of each pipeline step (with status `running` and the step number) and again at the end (with the result). If ShipRight crashes or is killed mid-pipeline, the persisted record reflects the last known state.

Example log entry at step start:
```json
{ "buildId": "abc-123", "step": 4, "stepName": "WriteVersionsAndTag", "status": "step_started", "timestamp": "2026-06-04T10:05:33Z" }
```

**NFR-001-B: Interrupted builds must be detected on startup.**

On application startup, ShipRight must scan all build records. Any record with status `running` or `deploying` that was last updated more than 5 minutes ago is considered orphaned. These must be updated to status `interrupted` with a log note: `"Build marked as interrupted: ShipRight process was terminated during execution"`.

**NFR-001-C: Process runner failures must be specific.**

When a subprocess exits with a non-zero code, the error logged must include:
- The exact command that was executed (with arguments — but see §13 for credential redaction)
- The exit code
- The last 50 lines of stdout/stderr
- The working directory

A generic "Build failed" message without specifics is not acceptable.

**NFR-001-D: The health endpoint must always respond.**

`GET /api/health` must return HTTP 200 within 500ms under all normal operating conditions. It must not perform any I/O that could block (no network calls, no slow disk reads). If the data directory is unavailable, return `{ "status": "degraded", "reason": "data directory unavailable" }` but still HTTP 200.

**NFR-001-E: SignalR disconnection must not lose build output.**

All log lines emitted via SignalR must also be appended to `BuildRecord.logOutput` in memory. The in-memory accumulation is flushed to disk after each step completes. A client that reconnects can always retrieve the full log from `GET /api/builds/{id}/log`.

### 7.2 NFR-002: Performance

| Metric | Requirement |
|---|---|
| API response time (excluding builds/deployments) | < 200ms at p99 |
| Health endpoint response time | < 100ms |
| Time from "Build" button click to first SignalR log line | < 3 seconds |
| Frontend initial page load (from ShipRight backend) | < 2 seconds on localhost |
| Build history list (100 records) | < 500ms |

**Note on build and deployment times:** The actual `docker build` and SSH operations are bounded by network speed and server performance, not ShipRight itself. ShipRight must not impose additional latency beyond the subprocess execution time.

### 7.3 NFR-003: Queryability

Build records must be queryable by:
- Project ID
- Status (single or multiple values)
- Date range (`startedAt`)
- Git tag (exact match)

The v1 disk-based implementation must implement these filters in memory (read all records from disk, apply filters). This is acceptable for v1 where build record counts will be < 1000.

The data model for `BuildRecord` must include all fields needed to support these queries without parsing `logOutput`.

### 7.4 NFR-004: Reporting

The following data must be computable from the stored build records:

| Report | Fields Required |
|---|---|
| Build success rate (last N builds per project) | `projectId`, `status`, `startedAt` |
| Average build duration per project | `projectId`, `startedAt`, `completedAt` |
| Deployment frequency (deploys per week) | `projectId`, `status == deployed`, `deployedAt` |
| Time since last successful deployment | `projectId`, `status == deployed`, `deployedAt` |
| Most common failure step | `projectId`, `failedStep` |

The `BuildRecord` model must include a `failedStep` field (nullable string) that names the step that caused a failure (e.g. `"DockerBuild"`, `"GitPush"`).

---

## 8. Technical Architecture

### 8.1 Backend Project Structure

```
back-end/
└── ShipRight/
    ├── ShipRight.csproj              # net8.0, linux-x64, no Windows deps
    ├── Program.cs                    # WebApplication builder, middleware, SignalR, SPA fallback
    ├── appsettings.json              # Serilog config, port, data directory override
    ├── appsettings.Development.json  # Dev-specific overrides
    ├── run.sh                        # Launcher script (WSL)
    ├── wwwroot/                      # Built Next.js SSG output (gitignored, populated by build script)
    ├── Shared/
    │   ├── ProcessRunner/
    │   │   ├── IProcessRunner.cs     # Interface: RunAsync(command, args, workingDir, onOutput, onError, cancellationToken)
    │   │   ├── ProcessRunner.cs      # Implementation: wraps System.Diagnostics.Process
    │   │   └── ProcessResult.cs      # ExitCode, StdOut, StdErr, Duration
    │   ├── SshRunner/
    │   │   ├── ISshRunner.cs         # Interface: RunAsync(host, username, keyPath, command, onOutput)
    │   │   └── SshRunner.cs          # Renci.SshNet implementation
    │   ├── Hubs/
    │   │   └── BuildHub.cs           # SignalR hub — JoinBuild, LeaveBuild
    │   ├── Store/
    │   │   └── DataDirectory.cs      # Resolves ~/.shipright/, creates if absent
    │   └── Middleware/
    │       └── RequestLoggingMiddleware.cs  # Log every request: method, path, status, duration
    └── Modules/
        ├── System/
        │   └── HealthRouter.cs        # GET /api/health
        ├── Projects/
        │   ├── ProjectConfig.cs       # Record types (see §9)
        │   ├── IProjectStore.cs       # Interface
        │   ├── JsonProjectStore.cs    # Disk implementation
        │   └── ProjectRouter.cs       # CRUD endpoints
        ├── Builds/
        │   ├── BuildRecord.cs         # Full build record model (see §9)
        │   ├── IBuildStore.cs         # Interface
        │   ├── JsonBuildStore.cs      # Disk implementation (one file per build)
        │   ├── BuildOrchestrator.cs   # Pipeline executor — all steps
        │   ├── PipelineContext.cs     # Carries build state through the pipeline
        │   └── BuildRouter.cs         # POST start, GET detail, GET list, POST respond, POST deploy, GET log
        └── VersionFiles/
            └── VersionFileService.cs  # Read, parse, validate, write version.txt files
```

### 8.2 Frontend Project Structure

```
front-end/
├── package.json
├── next.config.mjs                   # output: 'export', trailingSlash: true, no image optimisation
├── tsconfig.json
├── .env.development                  # NEXT_PUBLIC_API_URL=http://localhost:5200
├── .env.production                   # NEXT_PUBLIC_API_URL= (empty — served from same origin)
└── src/
    ├── pages/
    │   ├── _app.tsx                  # Font setup, Toaster, global providers
    │   ├── _document.tsx             # HTML shell, theme-color meta
    │   ├── index.tsx                 # Dashboard
    │   ├── projects/
    │   │   ├── index.tsx             # Project list
    │   │   ├── new.tsx               # Create project
    │   │   └── [id]/
    │   │       ├── index.tsx         # Project detail + build history
    │   │       └── edit.tsx          # Edit project config
    │   └── history.tsx               # Global build history
    ├── shared/
    │   ├── ApiService.ts             # Fetch wrapper (no auth headers in v1)
    │   ├── SignalRService.ts          # Hub connection, subscription management, reconnect logic
    │   └── types/
    │       ├── IProject.ts
    │       ├── IService.ts
    │       ├── IBuildRecord.ts
    │       └── IHealthResponse.ts
    ├── modules/
    │   ├── AppShell/                  # SidekickMenu sidebar wrapper
    │   ├── Dashboard/                 # Project summary cards
    │   ├── ProjectConfig/             # Tabbed create/edit form
    │   ├── BuildWizard/              # vaul Drawer: versions → git pause prompts → live log
    │   └── History/                  # Build history list + LogViewer drawer
    └── styles/
        └── globals.css               # Full design token system
```

### 8.3 Frontend Dependencies

The following npm packages must be included. Versions are minimum; use the latest compatible minor.

| Package | Version | Purpose |
|---|---|---|
| `next` | `^14.2.13` | Framework |
| `react` | `^18` | UI library |
| `react-dom` | `^18` | DOM rendering |
| `typescript` | `^5` | Type safety |
| `@microsoft/signalr` | `^10.0.0` | SignalR client |
| `framer-motion` | `^12.23.0` | Animations |
| `react-hot-toast` | `^2.4.1` | Toast notifications |
| `react-icons` | `^5.3.0` | Icon set |
| `react-select` | `^5.10.1` | Dropdown selects |
| `react-tabs` | `^6.1.0` | Tabbed UI |
| `vaul` | `^1.1.2` | Drawer/sheet component |
| `timeago-react` | `^3.0.7` | Relative timestamps |
| `lodash` | `^4.17.23` | Utility functions |
| `zustand` | `^5.0.0` | State management |
| `jattac.libs.web.zest-button` | `^1.3.0` | Button component |
| `jattac.libs.web.zest-textbox` | `^0.5.3` | Input component |
| `jattac.libs.web.zest-sidekick-menu` | `^1.2.0` | Sidebar navigation |
| `jattac.libs.web.zest-responsive-layout` | `^2.2.8` | Layout utilities |
| `jattac.libs.web.responsive-table` | `^0.12.0` | Data tables |
| `jattac.libs.web.overflow-menu` | `^0.0.35` | Context menus |
| `react-datepicker` | `^8.2.0` | Date range picker (for history filters) |
| `sharp` | `^0.34.5` | Image optimisation (build time) |

**Important:** The `jattac.libs.web.*` packages are internal npm packages. Confirm the npm registry configuration with the client before starting frontend work. These are not available on the public npm registry.

### 8.4 Backend NuGet Dependencies

| Package | Version | Purpose |
|---|---|---|
| `Serilog.AspNetCore` | `^10.0.0` | Structured logging |
| `Serilog.Sinks.Console` | `^6.1.1` | Console log output |
| `Serilog.Sinks.File` | `^7.0.0` | Rolling file log output |
| `SSH.NET` | `2025.1.0` | SSH client (NuGet package ID: `SSH.NET`; formerly `Renci.SshNet`) |
| `Swashbuckle.AspNetCore` | `^6.4.0` | Swagger/OpenAPI docs |
| `Newtonsoft.Json` | `^13.0.3` | JSON serialisation |
| `Microsoft.AspNetCore.SignalR` | (built-in .NET 8) | Real-time hub |

---

## 9. Data Models

### 9.1 ProjectConfig

```csharp
namespace ShipRight.Modules.Projects;

public record ServiceConfig
{
    public string Name { get; init; } = string.Empty;
    public string VersionFilePath { get; init; } = string.Empty;   // Absolute path to version.txt
    public string BuildContextPath { get; init; } = string.Empty;  // Passed to docker build
    public string DockerImageName { get; init; } = string.Empty;   // e.g. "nyingi/jattac-sms"
}

public record GitConfig
{
    public string RepoPath { get; init; } = string.Empty;          // Local path to .git repo
    public string DeployBranch { get; init; } = "master";
}

public record WslConfig
{
    public string WorkingDir { get; init; } = string.Empty;        // Linux absolute path
}

public record ServerConfig
{
    public string Host { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string SshKeyPath { get; init; } = string.Empty;
    public string RemoteWorkingDir { get; init; } = string.Empty;
    public string RebuildScript { get; init; } = "rebuild.sh";
}

public record ProjectConfig
{
    public string Id { get; init; } = string.Empty;                // URL-safe slug, e.g. "sms-gateway"
    public string Name { get; init; } = string.Empty;
    public List<ServiceConfig> Services { get; init; } = new();
    public GitConfig Git { get; init; } = new();
    public WslConfig Wsl { get; init; } = new();
    public ServerConfig Server { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
}
```

### 9.2 BuildRecord

```csharp
namespace ShipRight.Modules.Builds;

public record ServiceVersion
{
    public string ServiceName { get; init; } = string.Empty;
    public string PreviousVersion { get; init; } = string.Empty;
    public string NewVersion { get; init; } = string.Empty;
    public string DockerImageName { get; init; } = string.Empty;
}

public enum BuildStatus
{
    Pending,          // Created, not yet started
    Running,          // Pipeline executing
    Paused,           // Waiting for user input
    BuildSucceeded,   // All build steps complete; awaiting deploy action
    BuildFailed,      // A build step failed
    Aborted,          // User chose to abort at a pause point
    Interrupted,      // ShipRight process died during execution
    Deploying,        // SSH deployment in progress
    Deployed,         // rebuild.sh completed successfully
    DeployFailed      // rebuild.sh exited non-zero or SSH failed
}

public class BuildRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;       // Denormalised for display without project lookup
    public BuildStatus Status { get; set; } = BuildStatus.Pending;
    public string GitTag { get; set; } = string.Empty;             // Set after Step 4, e.g. "b_0.1.5_f_0.1.4"
    public List<ServiceVersion> Versions { get; init; } = new();
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }                     // Set when build pipeline finishes
    public DateTime? DeployedAt { get; set; }                      // Set when deployment finishes
    public string? FailedStep { get; set; }                        // Name of step that failed, if any
    public int? CurrentStepNumber { get; set; }                    // Last step that started
    public string? CurrentStepName { get; set; }
    public string LogOutput { get; set; } = string.Empty;          // Full accumulated log text
    public string? ErrorSummary { get; set; }                      // Human-readable failure reason
}
```

**Critical:** `BuildRecord` must be serialisable to and from JSON without data loss. All `enum` values must be serialised as their string names (not integer values), since integer-keyed enums break when enum members are reordered. Configure `Newtonsoft.Json` with `StringEnumConverter`.

---

## 10. API Specification

All API routes are prefixed `/api/`. All request and response bodies are JSON (`Content-Type: application/json`).

Error responses follow a consistent shape:
```json
{
  "isError": true,
  "message": "Human-readable error description",
  "field": "fieldName"    // optional, present when error is field-specific
}
```

### 10.1 Health

| Method | Path | Description |
|---|---|---|
| GET | `/api/health` | System health check |

### 10.2 Projects

| Method | Path | Description |
|---|---|---|
| GET | `/api/projects` | List all projects |
| GET | `/api/projects/{id}` | Get project by ID |
| POST | `/api/projects` | Create project (validates all fields) |
| PUT | `/api/projects/{id}` | Update project |
| DELETE | `/api/projects/{id}` | Delete project (rejects if active builds exist) |
| GET | `/api/projects/{id}/summary` | Dashboard summary (current versions + last build/deploy) |
| GET | `/api/projects/{id}/current-versions` | Read version.txt for each service |

### 10.3 Builds

| Method | Path | Description |
|---|---|---|
| POST | `/api/builds/start` | Start build pipeline |
| GET | `/api/builds` | List builds (supports filters, pagination) |
| GET | `/api/builds/{id}` | Get build detail (includes logOutput) |
| GET | `/api/builds/{id}/log` | Get log as plain text |
| POST | `/api/builds/{id}/respond` | Respond to a pipeline pause |
| POST | `/api/builds/{id}/deploy` | Trigger deployment for a succeeded build |

### 10.4 Request/Response Examples

**Start a build:**
```
POST /api/builds/start
{
  "projectId": "sms-gateway",
  "serviceVersions": [
    { "serviceName": "API", "newVersion": "0.1.5" },
    { "serviceName": "Web UI", "newVersion": "0.1.4" }
  ]
}

Response 202 Accepted:
{
  "buildId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
  "status": "Running",
  "message": "Build pipeline started"
}
```

**Respond to a git dirty pause:**
```
POST /api/builds/f47ac10b.../respond
{
  "reason": "git_dirty",
  "choice": "commit",
  "data": { "commitMessage": "Fix: correct phone validation" }
}

Response 200 OK:
{
  "status": "Running",
  "message": "Committed. Pipeline resuming."
}
```

**Trigger deployment:**
```
POST /api/builds/f47ac10b.../deploy

Response 202 Accepted:
{
  "buildId": "f47ac10b...",
  "status": "Deploying",
  "message": "Deployment started"
}
```

---

## 11. UI/UX Specification

### 11.1 Design Language

ShipRight uses a dark-first enterprise design language: deep navy backgrounds, champagne gold accents, soft slate surfaces, and subtle animations. The intent is "quiet confidence" — a tool used by professionals under pressure should project calm and clarity, not noise.

**Colour Tokens (defined as CSS custom properties in `globals.css`):**

```css
:root {
  --color-bg:          #0B1120;   /* Page canvas — deep navy */
  --color-surface:     #131D30;   /* Card / panel surface */
  --color-surface-2:   #1A2640;   /* Inset areas, table headers */
  --color-surface-3:   #1F2E4A;   /* Deepest visible elevation */
  --color-border:      rgba(255, 255, 255, 0.08);
  --color-border-strong: rgba(255, 255, 255, 0.14);
  --color-text:        #F0F2F5;   /* Near-white */
  --color-text-2:      #A8B8CC;   /* Secondary text */
  --color-muted:       #637389;   /* Captions, hints */
  --color-accent:      #C9A84C;   /* Champagne gold — primary actions */
  --color-accent-dark: #A8872F;
  --color-accent-light: rgba(201, 168, 76, 0.15);
  --color-success:     #3D9970;
  --color-success-light: rgba(61, 153, 112, 0.12);
  --color-warning:     #C9943A;
  --color-warning-light: rgba(201, 148, 58, 0.12);
  --color-error:       #B84040;
  --color-error-light: rgba(184, 64, 64, 0.12);
  --color-info:        #4A7FA8;
  --color-info-light:  rgba(74, 127, 168, 0.12);
}
```

**Typography:**
- Body / UI: `Inter` (loaded via `next/font/google`)
- Monospace (version tags, git hashes, log output, command strings): `JetBrains Mono` (loaded via Google Fonts `@import` in `globals.css`)
- Heading weight: 700, tracking `-0.02em`
- Body weight: 400, line-height 1.6

**Status badge mapping:**

| Build Status | Colour Token | Behaviour |
|---|---|---|
| `Running` | `--color-accent` | Animated pulse |
| `Paused` | `--color-info` | Static |
| `BuildSucceeded` | `--color-success` | Static |
| `BuildFailed` | `--color-error` | Static |
| `Deployed` | `--color-success` | Static with ship icon |
| `DeployFailed` | `--color-error` | Static |
| `Interrupted` | `--color-warning` | Static |
| `Aborted` | `--color-muted` | Static |

### 11.2 Navigation Structure

The sidebar (`SidekickMenu`) contains four items in this order:
1. **Dashboard** (icon: grid/home) → `/`
2. **Projects** (icon: cube/stack) → `/projects`
3. **History** (icon: clock/list) → `/history`

The active item is highlighted with a left-border accent in `--color-accent`.

### 11.3 Page Descriptions

#### Dashboard (`/`)
- Header: "ShipRight" wordmark + tagline "Build. Ship. Done."
- Body: one `ProjectCard` per configured project
  - Project name, number of services
  - Service version chips (e.g. `API v0.1.5`, `Web UI v0.1.4`) in monospace
  - Last build: `StatusBadge` + relative timestamp (timeago)
  - Last deployed: relative timestamp or "Never deployed"
  - Build success rate: last 10 builds as a small progress bar
  - "Build" primary button → opens `BuildWizard` drawer
- If no projects configured: empty state with "Add your first project" call to action

#### Project List (`/projects`)
- Page title: "Projects"
- "New Project" button top-right
- Cards as above, with Edit and Delete (confirmation required) actions
- Delete must check for active builds and reject if any exist

#### Project Create/Edit (`/projects/new`, `/projects/[id]/edit`)
- Tabbed layout: General | Services | Git & WSL | Server
- Progress indicator showing which tabs have unsaved changes
- "Save Project" and "Cancel" buttons fixed at the bottom
- Field-level inline validation (real-time for format, on-save for filesystem checks)

#### Project Detail (`/projects/[id]`)
- Project name + edit button
- Current service versions (read live from `GET /api/projects/{id}/current-versions`)
- "Build" button → opens `BuildWizard` drawer
- Deployment history table (last 20, paginated):
  - Columns: Tag, Versions, Status, Duration, Started, Actions
  - "View Log" opens `LogViewer` drawer for that build
  - "Deploy" button appears on `BuildSucceeded` rows

#### Build Wizard (vaul Drawer, 3 steps)

**Step 1 — Version Confirmation:**
- Each service shown with its current version and a ZestTextbox for the new version
- Suggested new version pre-filled (current patch + 1)
- "Start Build" button → sends `POST /api/builds/start`

**Step 2 — Pipeline (Live):**
- Step tracker showing each pipeline step with status icon (pending / running / done / failed)
- `LogViewer` component below: streams log lines via SignalR
- If `PauseRequested` event arrives: overlay with the prompt and action buttons (e.g. "Commit" / "Abort") and optional input fields (e.g. commit message)
- "Deploy" button appears when status reaches `BuildSucceeded`

**Step 3 — Deploy (inline in same drawer):**
- Confirmation: "Deploy tag {gitTag} to {server.host}?"
- "Deploy to Production" button
- Log output continues streaming (SSH output)
- On `DeployCompleted`: success/failure state with full log accessible

#### History (`/history`)
- Page title: "Build History"
- Filter bar: Project dropdown, Status multi-select, Date range picker
- Table: Tag, Project, Versions, Status, Started, Duration, Deploy time
- Each row: "View Log" action → `LogViewer` drawer

#### LogViewer (Drawer/Modal)
- Pre block with `font-family: var(--font-mono)`
- Dark inset background (`--color-surface-2`)
- Auto-scroll to bottom during live streaming
- Scroll-lock toggle (user can pause auto-scroll to read)
- "Copy to clipboard" and "Download as .txt" actions
- Line-by-line framer-motion fade-in during live streaming
- `source` prefix colouring: `[docker]` in blue, `[git]` in green, `[ssh]` in amber, `[shipright]` in grey

### 11.4 Animation Specification

All animations must use `framer-motion`. Animations must respect `prefers-reduced-motion`. Use `useReducedMotion()` hook and substitute instant transitions when it returns `true`.

| Element | Animation |
|---|---|
| Page transition | opacity 0→1 + translateY 6px→0, duration 300ms, ease `[0.16, 1, 0.3, 1]` |
| Card entry | opacity 0→1, staggered by 50ms per card, duration 250ms |
| Card hover | scale 1→1.005, shadow deepens, duration 150ms |
| `StatusBadge` (Running) | `box-shadow` pulse with `--color-accent-glow`, 2s loop |
| LogViewer lines | opacity 0→1, duration 80ms per line (very fast — do not delay readability) |
| Drawer open/close | Handled by `vaul`, do not override |

---

## 12. Integration Specifications

### 12.1 Git Integration

ShipRight calls `git` as a subprocess via `IProcessRunner`. It does not use LibGit2Sharp or any .NET git library. This decision was made because LibGit2Sharp has incomplete SSH key support for push operations, while shelling to `git` works reliably with the system git credential and SSH agent configuration.

**All git commands must use the `-C {path}` flag** rather than changing the working directory of the process. Changing working directories in a multi-threaded async context leads to race conditions.

✅ Correct: `git -C /path/to/repo status`
❌ Wrong: `cd /path/to/repo && git status`

**Git must be available in PATH.** ShipRight must check for git availability at startup and log a warning if `git --version` fails.

**Required git version:** 2.20 or newer (for `--follow-tags` support). Check at startup.

### 12.2 Docker Integration

ShipRight calls `docker` as a subprocess. Docker must be accessible to the user running ShipRight (the `ubuntu` user must be in the `docker` group, or Docker Desktop's WSL integration must be configured).

**At startup**, ShipRight must run `docker info` and log the result. If it fails, log a warning (not an error — ShipRight can still manage projects and view history without Docker being available).

**Never** construct docker commands using string concatenation with user-supplied values. Use `ProcessRunner` with a `string[]` arguments array.

✅ Correct:
```csharp
await processRunner.RunAsync(
    "docker",
    new[] { "build", "-t", $"{imageName}:{version}", buildContextPath },
    workingDir: null,
    onOutput: ...,
    onError: ...
);
```

❌ Wrong:
```csharp
await processRunner.RunAsync(
    "bash",
    new[] { "-c", $"docker build -t {imageName}:{version} {buildContextPath}" },
    ...
);
```

The wrong approach opens a command injection vector if `imageName` or `version` contains shell metacharacters.

### 12.3 SSH / Remote Server Integration

ShipRight uses `Renci.SshNet` (NuGet) for SSH connections. This library provides a pure .NET SSH implementation that works identically on WSL and EC2 without requiring system SSH to be configured.

**Connection settings:**
```csharp
using var keyFile = new PrivateKeyFile(sshKeyPath);
using var client = new SshClient(host, username, keyFile);
client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
client.Connect();
```

**Host key verification** must be enabled. ShipRight must maintain a known hosts file at `~/.shipright/known_hosts`. On first connection to a new host, present the host fingerprint to the user and ask for confirmation before adding it to known hosts. This is standard SSH security practice and must not be bypassed.

**Streaming SSH output:**
```csharp
using var cmd = client.CreateCommand($"cd {remoteWorkingDir} && bash {rebuildScript}");
var asyncResult = cmd.BeginExecute();
using var outputReader = new StreamReader(cmd.OutputStream);
using var errorReader = new StreamReader(cmd.ExtendedOutputStream);

// Read stdout and stderr concurrently...
```

**Timeout:** The SSH command (rebuild.sh) has no timeout on the ShipRight side. The `rebuild.sh` script itself may take several minutes (image pull from Docker Hub can be slow). Do not add an artificial timeout that would abort a healthy deployment.

### 12.4 WSL Path Handling

ShipRight runs inside WSL. All file paths are Linux paths. The `wslWorkingDir` field in project config is a Linux absolute path (e.g. `/home/nyingi/work/...`).

File paths supplied in project config that begin with `/mnt/` are Windows drives mounted in WSL (e.g. `/mnt/d/work/...`). These are valid and must work. Do not assume all paths are under `$HOME`.

ShipRight does not need to handle UNC paths (`\\wsl.localhost\...`) — those are the Windows-side representation of WSL paths and are not relevant when running inside WSL.

---

## 13. Security Requirements

### 13.1 SEC-001: Credential Handling

**SSH Private Keys:**
- Must never be read into memory except during active SSH session establishment and immediately discarded
- Must never appear in log output — if a file path is logged, log only the path (not the content)
- Must never be included in API responses
- `SshKeyPath` is stored in project config as a path; the key content is read at connection time only

**Docker Hub Credentials:**
- Must never be stored by ShipRight. Docker credentials are managed entirely by Docker's own credential store (`~/.docker/config.json`)
- When prompting for Docker login, the password is passed to `docker login` via stdin only:
  ```csharp
  var process = new Process {
      StartInfo = new ProcessStartInfo {
          FileName = "docker",
          Arguments = $"login -u {username} --password-stdin",
          RedirectStandardInput = true,
          ...
      }
  };
  process.Start();
  await process.StandardInput.WriteLineAsync(password);
  process.StandardInput.Close();
  ```
- The password must never appear in:
  - Log files
  - SignalR log stream to the frontend
  - API responses
  - Process arguments (always use stdin)

**Log Redaction:** ShipRight must implement a log redaction filter in `ProcessRunner` and `SshRunner`. Any string matching a list of known sensitive patterns (password, key content preamble `-----BEGIN`) must be replaced with `[REDACTED]` before being written to any log sink or emitted via SignalR.

### 13.2 SEC-002: Input Validation & Injection Prevention

**File path validation:** All file paths supplied via API (project config) must be validated before any filesystem operation:
- Must not contain null bytes
- Must not contain `..` path traversal sequences
- Must be absolute paths
- Must not exceed 4096 characters

**Command injection prevention:** No user-supplied value must ever be interpolated into a string passed to a shell. All subprocess invocations must use `ProcessStartInfo.ArgumentList` (not `Arguments` as a string) so that the OS handles argument quoting correctly, or must pass arguments as a `string[]` to ProcessRunner which builds the argument list safely.

**Example of what is forbidden:**
```csharp
// NEVER DO THIS — buildContextPath could contain "; rm -rf /"
var cmd = $"docker build -t {imageName} {buildContextPath}";
process.StartInfo.Arguments = cmd;
```

**Docker image name validation:** Before passing `dockerImageName` to any subprocess, validate it against the regex `^[a-z0-9._\-\/:]+$`. Reject any name that doesn't match.

**Version string validation:** Before writing to `version.txt`, validate that the version matches `^\d+\.\d+\.\d+$`.

### 13.3 SEC-003: Network Security

**CORS:** In v1 (localhost only), the CORS policy allows origins `http://localhost:5200` and `http://127.0.0.1:5200` only. No wildcard origins. The allowed origin must be configurable via `appsettings.json` to support the v2 EC2 deployment.

**HTTP only in v1:** ShipRight v1 runs over HTTP on localhost, which is acceptable for a single-user local tool. For the EC2 v2 deployment, TLS is mandatory. The architecture must not preclude TLS (do not hardcode `http://`).

**Port binding:** Bind to `127.0.0.1:5200` only in v1. Do not bind to `0.0.0.0` on a local development machine — this would expose the tool to the local network.

### 13.4 SEC-004: SSH Host Key Verification

Host key verification must not be disabled. The pattern `RejectUnknownHosts = false` or equivalent is explicitly forbidden.

On first connection to a new host, ShipRight must:
1. Obtain the remote host's public key fingerprint
2. Present it to the user in the frontend with a confirmation prompt
3. Only proceed after user confirmation
4. Write the accepted fingerprint to `~/.shipright/known_hosts`
5. On subsequent connections, verify against the stored fingerprint

### 13.5 SEC-005: Data Directory Permissions

The `~/.shipright/` directory must be created with permissions `700` (owner read/write/execute only). Files within it must be `600`. ShipRight must check and enforce these permissions at startup, and log a warning if they are looser than required.

### 13.6 SEC-006: API — No Authentication in v1

ShipRight v1 has no API authentication because it binds to localhost only. However, the routing layer must be architected so that authentication middleware can be inserted without changing endpoint code. A middleware placeholder (pass-through in v1) must be present in the middleware pipeline in `Program.cs`, with a comment noting where JWT/session auth will be added in v2.

---

## 14. Logging & Observability Requirements

### 14.1 Logging Framework

Use **Serilog** with the following sinks:
- **Console sink**: Human-readable output format (for the developer watching `run.sh`)
- **File sink**: JSON structured format, rolling daily, retained for 30 days

File sink path: `~/.shipright/logs/shipright-{Date}.log`

### 14.2 Log Configuration (appsettings.json)

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.Hosting.Lifetime": "Information",
        "System": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "/root/.shipright/logs/shipright-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 30,
          "formatter": "Serilog.Formatting.Json.JsonFormatter, Serilog"
        }
      }
    ]
  }
}
```

**Note:** The file path uses `/root/` as a placeholder. The actual path must be resolved at runtime using `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)` — do not hardcode the username.

### 14.3 What Must Be Logged

Every log entry must be structured (key-value pairs), not plain strings. Serilog's message template syntax achieves this automatically.

**Request logging (every HTTP request):**
```
[Info] HTTP {Method} {Path} responded {StatusCode} in {DurationMs}ms
Properties: { Method, Path, StatusCode, DurationMs, RequestId }
```

**Build pipeline step events:**
```
[Info] Build {BuildId} step {StepNumber} ({StepName}) started
[Info] Build {BuildId} step {StepNumber} ({StepName}) completed in {DurationMs}ms
[Error] Build {BuildId} step {StepNumber} ({StepName}) failed after {DurationMs}ms: {ErrorMessage}
Properties: { BuildId, ProjectId, StepNumber, StepName, DurationMs, ErrorMessage? }
```

**Subprocess invocations:**
```
[Debug] Process started: {Executable} {Arguments} in {WorkingDir}
[Debug] Process completed: {Executable} exited {ExitCode} in {DurationMs}ms
[Error] Process failed: {Executable} exited {ExitCode}: {LastErrorLines}
```

**SSH events:**
```
[Info] SSH connection established: {Username}@{Host}
[Info] SSH command executing: {Command} in {RemoteWorkingDir}
[Info] SSH command completed: exit {ExitCode} in {DurationMs}ms
[Error] SSH connection failed: {Host}: {ErrorMessage}
```

**State changes:**
```
[Info] Build {BuildId} status changed: {OldStatus} → {NewStatus}
```

**Security events:**
```
[Warning] SSH key file has insecure permissions {Permissions} at {KeyPath}
[Warning] Data directory has insecure permissions {Permissions} at {DataDir}
[Info] SSH host key accepted by user for {Host}: fingerprint {Fingerprint}
```

**Startup:**
```
[Info] ShipRight {Version} starting on port {Port}
[Info] Data directory: {DataDir}
[Info] {ProjectCount} projects loaded
[Info] {InterruptedCount} interrupted builds detected and marked
[Warning] Docker daemon unavailable: {ErrorMessage}  (if applicable)
[Warning] Git not found in PATH  (if applicable)
```

### 14.4 Correlation

Every build and deployment action must carry a `BuildId` property on all log entries produced during that operation. Use Serilog's `LogContext.PushProperty("BuildId", buildId)` to attach this automatically to all log calls within the pipeline execution context.

```csharp
using (LogContext.PushProperty("BuildId", build.Id))
using (LogContext.PushProperty("ProjectId", build.ProjectId))
{
    // All Log.* calls here automatically include BuildId and ProjectId
    await ExecutePipelineStepAsync(...);
}
```

This is non-negotiable. Without correlation IDs, debugging failures across concurrent builds (when multi-project support is added) becomes extremely difficult.

### 14.5 Log Queryability

The JSON-structured file logs are the primary queryability mechanism for v1. Each log entry is a self-contained JSON object on a single line (NDJSON format), enabling:

```bash
# Find all failures for a specific build
grep '"BuildId":"abc-123"' ~/.shipright/logs/shipright-20260604.log | grep '"Level":"Error"'

# Find all builds for a project
grep '"ProjectId":"sms-gateway"' ~/.shipright/logs/shipright-*.log | grep '"StepName":"BuildComplete"'
```

For v2, the log sink will be extended to write to a MariaDB `Logs` table or to a Seq/Loki instance for proper query UIs.

---

## 15. Error Handling & Recovery

### 15.1 Error Classification

| Category | Handling |
|---|---|
| **User error** (bad config, wrong branch) | Surface clearly in UI with actionable instructions. Do not log as ERROR — log as INFO with context |
| **Transient error** (network blip, Docker Hub timeout) | Log as WARN, surface to user with "retry" option where applicable |
| **Infrastructure error** (Docker daemon down, SSH refused) | Log as ERROR, surface to user with specific diagnosis |
| **Bug / unhandled exception** | Log as FATAL with full stack trace, surface generic "An unexpected error occurred — see logs" to user |

### 15.2 Unhandled Exception Handler

`Program.cs` must register a global exception handler middleware (`UseExceptionHandler`) that:
1. Logs the exception at FATAL level with full stack trace, request path, and request ID
2. Returns HTTP 500 with body: `{ "isError": true, "message": "An unexpected error occurred. Check logs for build ID {requestId}." }`
3. Never returns stack traces or internal exception messages to the HTTP response

### 15.3 Build Failure Recovery

When a build fails:
1. The `BuildRecord.status` is set to `BuildFailed`
2. The `BuildRecord.failedStep` is set to the step name
3. The `BuildRecord.errorSummary` is set to a human-readable description (1-2 sentences, not a stack trace)
4. The full log (including the error output) is in `BuildRecord.logOutput`
5. All successfully completed steps before the failure are NOT undone (e.g. if Docker push succeeded for Service 1 but failed for Service 2, Service 1's image remains pushed — this is logged and noted to the user)

### 15.4 Mid-Deploy SSH Disconnection

If the SSH connection drops during `rebuild.sh` execution:
1. Renci.SshNet raises an exception
2. ShipRight sets `BuildRecord.status = DeployFailed`
3. The error is logged: `"SSH connection lost during rebuild script execution at {Host}. Remote state is unknown — check server manually."`
4. The user is presented with the server address and a note to verify the container state manually

ShipRight must not attempt to re-connect and re-run `rebuild.sh` automatically — running `rebuild.sh` a second time during an already-running execution could leave the server in an inconsistent state.

---

## 16. Known Pitfalls & Anti-Patterns

This section is provided explicitly to prevent common mistakes. Each item has been identified from the design of the system and from general patterns in similar tooling.

---

**P-001: Using string concatenation for shell commands**

❌ Never pass user-supplied values into shell strings.

```csharp
// UNSAFE — buildContextPath could contain shell metacharacters
process.StartInfo.Arguments = $"-c 'docker build -t {image} {buildContextPath}'";
```

✅ Always use argument arrays:
```csharp
process.StartInfo.ArgumentList.Add("build");
process.StartInfo.ArgumentList.Add("-t");
process.StartInfo.ArgumentList.Add($"{image}:{version}");
process.StartInfo.ArgumentList.Add(buildContextPath);
```

---

**P-002: Logging credentials**

❌ Never log passwords, key file contents, or any secret.

```csharp
// LOGS THE PASSWORD — DO NOT DO THIS
Log.Information("Running docker login with password {Password}", password);
```

✅ Log that the action occurred, never the value:
```csharp
Log.Information("Running docker login for user {Username}", username);
```

---

**P-003: Disabling SSH host key verification**

❌ This silently removes a critical man-in-the-middle attack prevention:
```csharp
// FORBIDDEN
client.HostKeyReceived += (s, e) => e.CanTrust = true;
```

✅ Implement proper known-hosts verification (see §13.4).

---

**P-004: Buffering subprocess output instead of streaming**

❌ This causes the frontend log viewer to show nothing for minutes, then dump everything at once:
```csharp
// Waits until process exits — terrible UX for docker build
var output = await process.StandardOutput.ReadToEndAsync();
```

✅ Stream line by line:
```csharp
while (!process.StandardOutput.EndOfStream)
{
    var line = await process.StandardOutput.ReadLineAsync();
    if (line != null) await onOutput(line);
}
```

---

**P-005: Storing build state only in memory**

❌ If ShipRight is killed, all in-progress build state is lost:
```csharp
private readonly Dictionary<string, BuildRecord> _activeBuilds = new(); // WRONG
```

✅ Persist `BuildRecord` to disk before each step and after each step. The in-memory representation is a cache only.

---

**P-006: Not waiting for git push before proceeding to docker build**

The Docker build uses the source code at the version that was just committed. If the push hasn't completed before the build starts, the build may succeed but the tag won't be on the remote — the WSL pull in Step 5 will not reflect the new version.

✅ Always `await` the process runner result of `git push` and check exit code 0 before continuing.

---

**P-007: Serialising BuildStatus enum as integers**

If `BuildStatus.Pending = 0` and a new status is inserted before it, all serialised records become wrong.

✅ Always serialise enums as strings. Configure globally in `Program.cs`:
```csharp
builder.Services.AddControllers().AddNewtonsoftJson(options => {
    options.SerializerSettings.Converters.Add(new StringEnumConverter());
});
```

---

**P-008: Auto-merging branches without user consent**

If the developer is on a feature branch with in-progress work and ShipRight silently merges it to master, unfinished code ships to production. This is a critical mistake.

✅ ShipRight must only switch to the deploy branch (not merge). If a checkout fails due to uncommitted changes (which should have been caught in Step 2), abort with a clear message.

---

**P-009: Hardcoding ~/.shipright as a string path**

```csharp
// WRONG — breaks if running as a different user on EC2
var dataDir = "/home/ubuntu/.shipright";
```

✅ Resolve at runtime:
```csharp
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".shipright"
);
```

---

**P-010: Not handling version.txt trailing whitespace**

`version.txt` may contain a trailing newline or spaces. Failing to trim this causes a version parse failure.

✅ Always:
```csharp
var version = (await File.ReadAllTextAsync(path)).Trim();
```

---

**P-011: Binding to 0.0.0.0 in development**

This exposes the ShipRight API to the local network and potentially beyond. There is no authentication in v1.

✅ Bind to `127.0.0.1:5200` in development. Make the bind address configurable for EC2 deployment, but document the security implication of binding to `0.0.0.0`.

---

**P-012: Making BuildHub a Singleton**

SignalR group membership is per-connection. If `BuildHub` is a singleton, group state may bleed between connections.

✅ Register `BuildHub` as the default (transient) lifetime. Inject `IHubContext<BuildHub>` into `BuildOrchestrator` (singleton) for sending messages outside the hub.

---

**P-013: Not testing that the frontend works as SSG**

The frontend uses `output: 'export'` in `next.config.mjs`. Features that require a Node.js server at runtime (`getServerSideProps`, API routes, `next/image` optimisation) will silently fail or build error.

✅ Never use `getServerSideProps`. Use `getStaticProps` only. Do not use `next/image` with a remote `src` — use `<img>` directly or configure `unoptimized: true` in `next.config.mjs`.

---

## 17. Glossary

| Term | Definition |
|---|---|
| **Build** | The pipeline that increments version numbers, commits to git, builds Docker images, and pushes them to Docker Hub |
| **Deploy** | The subsequent operation that SSHes into the remote server and executes `rebuild.sh` to pull new images and restart containers |
| **BuildRecord** | The persistent record of a build (and optionally its subsequent deployment), stored in `~/.shipright/builds/` |
| **Project** | A configured deployable system, consisting of one or more Docker services, a git repository, and a remote server |
| **Service** | A single Docker container within a project (e.g. "API" or "Web UI") |
| **WSL** | Windows Subsystem for Linux — the Ubuntu environment within which ShipRight runs during development |
| **EC2** | Amazon Elastic Compute Cloud — the Linux server to which ShipRight deploys |
| **rebuild.sh** | A shell script maintained on the remote server that pulls the latest Docker images and restarts the Docker Compose stack |
| **version.txt** | A single-file versioning convention used by the client's projects. Contains one line: `MAJOR.MINOR.PATCH` |
| **deploy branch** | The git branch that is tagged and used as the basis for deployments (typically `master`) |
| **wslWorkingDir** | A Linux absolute path inside WSL to a local clone of the git repository containing the Docker Compose configuration |
| **SSG** | Static Site Generation — the Next.js build mode that produces plain HTML/JS/CSS files requiring no server-side rendering at runtime |
| **SignalR** | Microsoft's library for real-time bidirectional communication over WebSockets (with HTTP long-polling fallback) |
| **Provider pattern** | An architectural pattern where behaviour is defined by an interface and multiple implementations can be swapped without changing callers |
| **PEM key** | A private key file (typically `.pem` extension) used for SSH authentication to the remote server |
| **Annotated tag** | A git tag that includes metadata (author, date, message), as opposed to a lightweight tag. Used by ShipRight to mark build points |

---

## 18. Future Considerations (v2+)

The following capabilities are not in scope for v1 but must not be architecturally precluded by v1 implementation decisions.

| Feature | Architectural Precondition |
|---|---|
| MariaDB persistence | `IProjectStore` / `IBuildStore` interfaces must be intact |
| Multi-user support | Auth middleware slot must exist in pipeline (§13.6); CORS origin must be configurable |
| Slack/email notifications | `BuildOrchestrator` must have an injectable `INotificationService` (no-op in v1) |
| EC2 hosted deployment | No Windows-specific code anywhere; port and bind address configurable |
| Rollback (re-deploy previous tag) | `BuildRecord` must retain `gitTag` so a re-deploy can reference it |
| Parallel service builds | `BuildOrchestrator` pipeline steps are async and can be parallelised internally |
| Full-text log search | Logs are in structured JSON files; can be indexed by Loki/Elasticsearch later |
| Container health checks post-deploy | SSH runner is reusable for post-deploy verification commands |
| Web UI for `known_hosts` management | SSH known-hosts file is already maintained at `~/.shipright/known_hosts` |
| Scheduled / webhook-triggered builds | `BuildOrchestrator` is callable from any trigger; just add endpoints |

---

*End of Document*

*This specification is the authoritative source of truth for ShipRight v1. Any implementation decision that conflicts with this document must be escalated to the client before proceeding.*
