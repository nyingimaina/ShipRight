# Design: Build-on-target ("JIT") deploy mode

Status: draft for review. No code written yet.

## 1. Goal

Deploy a project without a container registry. ShipRight syncs the project's git
repo onto the target machine, builds the images there, and swaps the running
containers for the new ones. Deploys are all-or-nothing. A bounded number of past
releases are kept, and these are the basis for rollback.

## 2. Principles

1. **Nothing ShipRight-specific in the deployed project.** The compose file, image
   names, tags, labels and env vars must look hand-made. The project must keep
   working, and be operable with plain `docker compose`, if ShipRight is removed.
2. **All or nothing.** Build everything first. Only then touch running containers.
   Any failure after that point rolls back to the previous release.
3. **Release = a set of images** (one per built service) at one commit. Retention
   and rollback work on releases, never on individual images.
4. **Additive.** This is a new deploy strategy beside the registry flow, behind a
   small interface. The existing pipeline is not edited.

## 3. Neutral conventions

| Thing | Convention | Configurable |
|---|---|---|
| Image tag | `r<counter>-<shortsha>`, e.g. `r42-ab8b4a9` | no |
| Tag env var in compose | existing per-service convention `<SERVICE>_TAG` (`image: ${<SERVICE>_TAG:-<image>:latest}` next to `build:`), already used by generated compose and EnvCompose deploys | no |
| Labels (optional) | `deploy.project`, `deploy.release`, `deploy.sha` | prefix yes, or off |
| State file | `<project-dir>/.deploy-state/state.json`, plain JSON, outside the git working tree | location yes |
| Counter | monotonic per project per target | no |

Manual operation must always work: `RELEASE_TAG=r41-1a2b3c4 docker compose up -d`.

Git tags are **not** pushed to the user's repo by default (needs write
credentials, collides across targets). Optional setting: push a git tag.

## 4. Target layout

```
<project-dir>/
  repo/            git working tree, disposable (reset --hard, clean -fdx)
  .deploy-state/   state.json, per-release metadata, logs
  <env files>      injected by the deployer, never committed, never baked into images
```

## 5. Deploy flow

```
PREFLIGHT -> SYNC -> BUILD -> QUIESCE -> START -> VERIFY -> COMMIT
                        \________ any failure after QUIESCE ________/
                                        -> ROLLBACK
```

### 5.1 Preflight (zero side effects)
- Lock acquired for (project, target). Second deploy is refused, not queued.
- Git reachable and credentials valid; docker available.
- Free disk >= estimate (last build size + margin).
- `docker compose config` valid for the target SHA; env files present.
- Compose dependency graph is acyclic (topological order computed here).
- No port or volume conflicts introduced by the new compose.
- If a previous release exists: all its images are present locally. If not, warn
  that rollback is unavailable and require explicit confirmation.
- Skip: if resolved SHA equals the current release SHA and the user did not force,
  report "nothing to deploy".

### 5.2 Sync
- First run: clone. Later runs: fetch.
- Resolve the deploy branch (default `master`) to a **SHA once**. Check out that
  detached SHA. All later steps use this SHA, never the branch head.
- Allow deploying a specific SHA or tag instead of a branch.
- Submodules/LFS: initialise if present, or reject with a clear message.

### 5.3 Build
- Build **all** services that have a `build:` section, tagged with the new
  release tag. Base images should be pinned (warn on `:latest`).
- Optional CPU/memory limits on the build to protect running containers.
- Any build failure: abort. Delete partial images for this release. The running
  release was never touched. Exit state: `failed-build`, nothing to roll back.

### 5.4 Quiesce (the point of no return)
- Compute the **affected set**: services with new images, plus dependents the
  user has marked *restart* (section 14.1), resolved transitively. Unaffected
  services keep running.
- Stop affected services in **reverse dependency order** (dependents first).
- Each stop uses a configurable grace timeout (default 30s) then kills.
- Record `old-stopped` in state after each service.

### 5.5 Start
- Start affected services in **forward dependency order** with
  `RELEASE_TAG=<new>`.
- Each service must pass the health gate before the next one starts.

### 5.6 Verify (health gate)
A service **fails** the gate if any of these hold:
- it exits;
- its healthcheck reports unhealthy;
- its healthcheck does not become healthy within the per-project timeout
  (default 60s);
- no healthcheck exists and it is not still running after N seconds (default 10);
- its restart count increases during the gate (crash loop).

### 5.7 Commit
- Write `current = new`, `previous = old` to state.
- Apply retention (section 7).
- Release the lock.

### 5.8 Rollback
Triggered by any failure in 5.4 to 5.6, immediately, with no prompt.
1. Stop every new-release container that started (reverse order).
2. Start the previous release's services with `RELEASE_TAG=<old>`, using the
   compose file from the previous release's commit, in forward order.
3. Health-gate them using the same rules.
4. Remove the failed release's images.
5. Record `rolled-back` with the failing service and reason.

If rollback itself fails: retry once, then **stop and report**. The report lists
what is running, what is stopped, and the exact manual command to bring the old
release up. Never loop.

If there was no previous release (first deploy): stop what started, report
`failed, nothing to restore`.

## 6. State machine (persisted after every transition)

`idle -> syncing -> building -> old-stopped -> new-started -> verified -> idle`

Failure branches: `failed-build -> idle`, and
`(old-stopped | new-started) -> rolling-back -> rolled-back | rollback-failed`.

On orchestrator restart, a non-idle state is reconciled first. If `old-stopped` or
`new-started` is found, finish the rollback before allowing any new deploy.

## 7. Retention

- Setting: **keep N releases** (default 3, minimum 2). Minimum 2 guarantees the
  current and previous releases always exist.
- Prune in whole releases, oldest first, after a successful commit only.
- Never prune the current release, the previous release, or the images of any
  running container.
- Pruning a release removes all of its images and its metadata together.
- Build cache and dangling layers are a separate cleanup, handled by the existing
  disk-cleanup features.

## 8. Rollback (user-initiated, later)

- List retained releases: tag, date, SHA, commit message, status.
- Roll back = the 5.4 to 5.8 flow with a different target release and no build.
- Warn: rollback restores code and images only. Database migrations and volume
  data are not reverted.

## 9. Failure table

| Failure | Running release | Action |
|---|---|---|
| Preflight fails | untouched | abort, report reason |
| Git sync fails | untouched | abort |
| Any build fails | untouched | abort, delete partial images |
| Stop times out | partially stopped | kill, continue; rollback if kill fails |
| New service fails to start / unhealthy | new partly up | rollback |
| Rollback gate fails | broken | retry once, then report with manual command |
| ShipRight dies mid-deploy | unknown | reconcile from state file on next start |
| Disk full during build | untouched | abort, partial images deleted |

## 10. Known limitations (document in the UI)

- Data and schema changes are not rolled back.
- Downtime between Quiesce and Verify (seconds to tens of seconds). Zero-downtime
  is out of scope for v1 but not designed out.
- Each target builds its own images, so two targets can differ byte-for-byte.
  Registry mode remains the better fit for fleets.
- Build load competes with running containers on the same machine.

## 11. Security notes

- Use a read-only deploy key or token for git on the target.
- Env files and secrets are injected at runtime, never passed as build args or
  copied into layers. `.dockerignore` must exclude `.git` and env files.

## 12. Code placement (proposed)

- New `IDeployStrategy`-style seam; existing registry flow becomes one
  implementation, JIT a second. Wrapping, not editing, the current flow.
- New classes: `JitDeployStrategy`, `ReleaseStateStore`, `DependencyOrderer`,
  `HealthGate`, `ReleaseRetention`, `RollbackExecutor`.
- Each is unit-testable with a fake docker/git runner. Tests come first.
- Remote execution reuses the existing Linux-server command runner, wrapped
  behind a narrow interface (run command, upload file, read file).

## 13. Decisions

1. **Target OS: Linux first.** All git, docker and file operations run through
   ShipRight's existing mechanism for authenticating to and running commands on
   Linux servers. No new transport. Windows-native docker is out of scope for v1.
   Command execution sits behind an interface so tests use a fake runner.
2. **Defaults are tweakable.** See section 14.
3. **Dependents are a per-project, per-service choice, not a global rule.** See
   section 14.1.
4. **First-ever deploy proceeds with no rollback available.** The log states
   plainly: "No previous release; rollback is unavailable for this deploy."

## 14. Configuration

All values are per project, with the defaults below, editable in the UI with a
HoverTip explaining each (what it is, when to change it).

| Setting | Default | Notes |
|---|---|---|
| Deploy branch | `master` | or a specific SHA/tag for a one-off deploy |
| Releases to keep | 3 | minimum 2, enforced |
| Stop grace timeout | 30s | per project, overridable per service |
| Health timeout | 60s | per project, overridable per service |
| No-healthcheck settle time | 10s | "still running after N seconds" |
| Tag env var name | `RELEASE_TAG` | must be neutral |
| Label prefix | `deploy` | or off |
| Build CPU/memory limits | none | optional |
| Push git tag | off | needs write credentials |

### 14.1 Dependents of replaced services

When service A is replaced, services that depend on it (C) may or may not need a
restart. ShipRight cannot know, so the user decides.

- During project setup, ShipRight reads the compose dependency graph and, for each
  service that has dependents, shows the list and asks:
  "When **A** is replaced, also restart **C**?" with options
  *Restart dependents* / *Leave running*.
- The answer is stored per (service, dependent) pair in project config. Default
  when unanswered: restart dependents (the safe choice).
- The deploy's affected set is: replaced services, plus dependents marked
  *restart*, plus *their* dependents marked *restart*, resolved transitively.
- Stop/start order within the affected set still follows the dependency graph.
- The deploy log lists the final affected set and why each service is in it.
- The setting lives in ShipRight's own project config, **not** in the user's
  compose file, keeping the project free of ShipRight-specific content.

## 15. Integration with the existing code (verified by reading, 2026-11-13)

What exists today and how JIT mode attaches to it.

| Existing piece | Finding | Consequence |
|---|---|---|
| `DeployMode { GitScript, GitCompose, EnvCompose }` on `ServerConfig`; `BuildOrchestrator.DeployAsync` switches on it to build one SSH command string | JIT is a multi-step flow with state, not one command | Do not add a case to that switch (editing an existing method body). Add a new `DeployMode` value only as data, and route it with a decorator (below) |
| Decorators `FailureCleaningBuildOrchestrator`, `SnapshotBuildOrchestrator` already wrap `BuildOrchestrator` | Established pattern for adding behaviour without editing it | `JitDeployOrchestrator` wraps the same way: JIT servers go to `JitDeployer`, everything else falls through untouched |
| `ISshRunner.RunAsync(host, user, key, command, onOutput, onStderr, ct)` plus many hand-rolled `FakeSsh` test doubles | Single command-in, exit-code-out seam. No file upload | Every JIT step is a shell command string. State and env files are written via base64 commands, as `RemoteEnvFileWriter` already does. Tests assert on the commands sent |
| `GeneratedCompose` renders `image: ${<SERVICE>_TAG:-<image>:latest}` and `EnvCompose` exports `<SERVICE>_TAG=<image>:<version>` | Neutral per-service tag convention already exists | Reuse it. JIT needs a compose that also has `build:`, so add a new `GeneratedCompose` method (new method on the class is allowed) |
| `RemoteServerDeployer`, `ResilientServerDeployer`, `RemoteDeployPrep`, `RemoteGitHygiene` (used only by `ServerBootstrapRouter`) | **Correction:** these deploy ShipRight itself onto a server. `RemoteDeployPrep` hardcodes `shipright-db/app/caddy` and its disk guard runs `docker image prune -af` | **Do not reuse.** They are ShipRight-specific, which JIT must not be, and they would delete unused images. JIT gets its own neutral preflight and disk guard |
| `BuildMachineImagePruner`, `PushAwareImagePruner`, `ProjectSpaceService` | **Correction:** these run on the ShipRight build machine's local docker, not on the deploy target, and are well covered by existing tests | Not a risk for a remote target. Only a risk when the target is the same docker daemon as ShipRight's build machine (see §17 risk 1). JIT-built tags are not in build history, so `docker rmi` of unused refs could remove retained releases there |
| `RollbackAsync` / `ExecuteRollbackAsync` | Registry-based: re-pulls old tags, rejects `GitScript` | Leave alone. JIT rollback is a different flow (images already local), done by `JitDeployer` |
| `PipelineStep.All`: PreconditionCheck, GitStatusCheck, BranchCheck, WriteVersionsAndTag, ComposeRepoSync, DockerBuild, BuildComplete; plus custom `PipelineResource` pipelines | The local pipeline builds and pushes on the ShipRight machine | **Not yet verified:** how a JIT project skips local DockerBuild/push and the compose-repo sync. Needs a read of `RunPipelineAsync` (about 470 lines) before the Phase 4 design is final |
| `ServiceConfig.ImageRetentionCount` / `LocalImageKeepCount` | Retention for the registry and for the ShipRight machine | Not reused: JIT retention is per release, lives on the server config |

## 16. Implementation plan (test-first, smallest safe slices)

Each phase: characterise existing behaviour, write failing tests, implement, run
the full suite (1395 C# + 479 Jest at last count), then move on.

**Phase 0: safety net (no behaviour change)** - DONE 2026-11-13
- `DeployBackwardCompatibilityTests` (18 tests, all passing on unchanged code) pins:
  the exact SSH command for GitScript, GitCompose (targeted and whole-stack) and
  EnvCompose; the `<SERVICE>_TAG` naming; the override rule; success, non-zero exit
  and lost-SSH outcomes and their messages; rollback rules (refused for GitScript,
  EnvCompose redeploy, failure message); and the persisted names/ordinals of
  `DeployMode` (a new mode must be appended, never renumbered).
- Existing prune/cleanup behaviour was already covered
  (`BuildMachineImagePrunerTests`, `PushAwareImagePrunerTests`,
  `ProjectSpaceServiceTests`, `DeployCleanSlateTests`), so nothing new was needed.

**Phase 1: pure logic (no SSH, no docker)** - DONE 2026-11-13 (79 tests, code in `ShipRight.Server/Modules/JitDeploy/`)
- `DependencyOrderer`: compose `depends_on` to topological order, cycle detection.
- `AffectedSet`: replaced services plus per-pair "restart dependents" choices, transitive.
- `ReleaseRetention`: given releases and running images, which to prune. Minimum 2.
- `HealthGate` decision rules (exited, unhealthy, timeout, settle, restart count).
- `ReleaseTag` generator: `r<counter>-<shortsha>`.

**Phase 2: state and command builders (assert on command strings)** - DONE 2026-11-13 (232 tests, 16 of them run real bash)

Built in `ShipRight.Server/Modules/JitDeploy/`: `ShellQuote`/`JitNames` (allow-lists, every
value validated then single-quoted), `JitTarget` (`<dir>/repo`, `<dir>/.deploy-state/state.json`),
`ReleaseState` + `ReleaseStateMachine` (transitions, crash-recovery actions), `ReleaseStateStore`
(atomic temp-file-then-rename write; a corrupt or newer-format file is an error, never "no state"),
`ComposeConfig` (parses `docker compose config --format json`; `CheckBuilds` explains a missing
`build:` or an `image:` line that ignores the tag variable), `JitCommands`, `ProbeParser`,
`ImageListParser`. Decisions taken while building:
- Compose is always driven with an explicit `-p <project>` so the project name is stable across releases.
- Start uses `up -d --no-deps --no-build --pull never <svc>`: it can only start images that already exist.
- Stop is `stop -t <grace> <svc>` and never removes a container.
- Image removal is exact-reference, release-tagged only, never forced, never fails the deploy.
- No command contains prune, `rm -rf`, `down`, volumes, or the word ShipRight (asserted by test).
- Git prompts are disabled (`GIT_TERMINAL_PROMPT=0`) and the connectivity failure message never repeats the URL (it may hold a token).
- `ShellSyntaxTests` run every builder through `bash -n`, round-trip hostile strings through a real shell, and
  execute the state write/read and disk check against a temp folder. Skipped on machines without bash.
- Not done here (Phase 3/4): writing the env file, the orchestration order, and wiring.
- `ReleaseStateStore`: read/write `state.json` via SSH commands; transition rules.
- Command builders: clone/fetch/checkout SHA, per-service build, ordered stop,
  ordered start, image listing, release prune.

**Phase 3: orchestration with fake SSH** - DONE 2026-11-13 (`JitDeployer`: deploy, recover, roll back to a kept release; 48 end-to-end tests against a pretend server)
- `JitDeployer` state machine: all paths in section 9's failure table, including
  failure at each stage, rollback success, rollback failure, first deploy,
  crash-recovery reconcile from persisted state.

**Phase 4: wiring** - DONE 2026-11-13 (see section 18)
- `DeployMode` value (data only), `JitDeployOrchestrator` decorator, router hook.
- Server settings (section 14) and validation, following `ProjectRetentionValidator`.
- Resolve the open item in section 15 about skipping local build/push.

**Phase 5: UI** - DONE 2026-11-13 (`front-end/src/modules/JitDeploy/`, mounted in the project page)
- Server settings form with HoverTips, dependent-restart prompts, releases list
  with rollback button, verbose deploy log (stdout must show every step).

**Phase 6: real-host verification** - NOT DONE (needs a Linux host; checklist in section 18)
- Run against a disposable Linux VM. Never stop ShipRight.Server by name; use PIDs.

## 17. Risks to watch

1. When the JIT target is the same docker daemon as ShipRight's own build machine
   (e.g. local WSL), the build-machine pruners and the Space "one-click clean" could
   `docker rmi` retained release images that no container uses. Needs a JIT-aware
   exclusion (by release-tag pattern or label) before JIT is allowed on that host.
2. Compose files that have `image:` only (registry style). JIT needs `build:`; the
   preflight must detect and explain this in plain language.
3. The pipeline's local build/push stages (open item in section 15).
4. Single-command SSH runner: long builds need streaming output and no idle timeout.


## 18. Status: what exists, how it is wired, how to use it (2026-11-13)

Phases 0 to 5 are built and tested: 2025 C# tests (78 skipped, as before) and 634 Jest tests green, `tsc` and lint clean, and a throwaway copy
of the real server was started to confirm the new routes answer and an existing route is unaffected. Phase 6 (a real
Linux host) is not done: everything was verified against a pretend server and a real local bash, never a real docker.

### How it stays 100% backward compatible
- No existing deploy mode, method body, route or table was changed. `DeployBackwardCompatibilityTests` pins today's
  commands, outcomes, rollback rules and the persisted `DeployMode` names/ordinals; they pass unchanged.
- The mode is **opt-in per project** and its settings live in their own file (`jit-deploy.json` in the data folder),
  not on `ProjectConfig`, so saving a project from any screen can never lose or alter them.
- Every deploy is recorded as an ordinary build record (`IsRollback` set for on-purpose rollbacks) and its log goes
  through the same event bus, so history and the live log viewer work with no change.
- Edits to existing files, all purely additive: `Program.cs` (+9 lines: a using, three registrations, and one
  route mapping that is skipped in cloud mode) and `ProjectDetailPanel.tsx` (+2 lines: an import and the card).
- In cloud mode or on an older server the card hides itself silently.

### Endpoints (desktop mode)
| Method and path | Purpose |
|---|---|
| `GET /api/jit/{id}/config` | settings, the image line each service needs, and what is still missing |
| `PUT /api/jit/{id}/config` | validate and save the settings |
| `POST /api/jit/{id}/deploy` | `{force, commit, allowWithoutRollback}`; returns `{buildId}` to stream |
| `POST /api/jit/{id}/recover` | finish or undo an interrupted deploy |
| `POST /api/jit/{id}/rollback` | `{tag}`: go back to a kept release |
| `GET /api/jit/{id}/releases` | releases on the server, which is current/previous, whether it needs attention |
| `GET /api/jit/{id}/dependents` | the per-case "also restart this one?" questions, read from the compose file on the server |

### What the user does
1. In the project page, switch on "Build on the server instead of pushing to a registry", enter the git address and branch.
2. Make each built service's compose entry have a `build:` section and the `image:` line the card shows (with a Copy button).
3. Save, then Deploy now. Releases, Roll back and Recover appear below the deploy button.

### Known limitations (deliberately not hidden)
- One deploy at a time per project is enforced inside one ShipRight process. Two ShipRight instances pointed at the same server are not coordinated.
- A deploy cannot be cancelled from the UI yet (the existing cancel endpoint does not know about these deploys). Closing the app mid-deploy is safe: the saved state on the server is recovered with the Recover button.
- If the target server shares a docker daemon with the ShipRight machine itself (for example a local WSL target), ShipRight's own image cleanup and the Space "one-click clean" can remove retained release images that no container is using. Use remote servers until a JIT-aware exclusion exists.
- Only services that ShipRight builds get a release tag; every deploy builds all of them (no "only what changed" optimisation yet).
- Per-service overrides of stop grace and health timeout (mentioned in section 14) are project-wide only for now.
- Database changes are never rolled back, only code and images (stated in the UI before a rollback).

### Phase 6 checklist (needs a disposable Linux VM with docker and git)
1. A small app with two services (one depending on the other), each with `build:` and the `${X_TAG:-image:latest}` image line.
2. First deploy succeeds; `docker compose -p <project> ps` shows both running from the `r1-...` images.
3. Push a change; deploy again: no downtime beyond the swap, `r2-...` running, `r1-...` kept.
4. Push a change that makes the new service fail its health check: automatic rollback to `r2-...`, failed images gone.
5. Break the Dockerfile: the build fails and the running release is untouched.
6. Kill the SSH connection mid-swap: Recover brings the previous release back.
7. Roll back on purpose to `r1-...` and forward again.
8. Fill the disk below the minimum: the deploy refuses before touching anything.
9. Confirm `docker image ls` after several deploys keeps only the configured number of releases.
10. Confirm no container, image, label or file on the server mentions ShipRight.

### Offering what is already known (step 1 of the follow-up plan)
The card no longer asks for things ShipRight already has. All three endpoints only read, and the server's connection always comes from the stored project.

| Method and path | Purpose |
|---|---|
| `GET /api/jit/{id}/hints` | the project's git addresses (each repo's `origin`, in SSH and HTTPS form, never with a password or token), its deploy branch, its env file and its default stack name |
| `GET /api/jit/{id}/stacks` | the compose stacks already running on the server; warns about a second copy (same project folder, different name) and about relative paths moving when a same-named stack is adopted from another folder |
| `POST /api/jit/{id}/branches` | `{url}`: the repository's real branches as the server sees them (needs the server to have access); refuses addresses carrying a secret |

On the card: a dropdown of known addresses (both forms, saying what each needs), a branch list with "Find branches on the server", the env file from the server settings, and existing stacks with a "take over" button. Turning the mode on for a never-configured project fills the empty fields from what is known (SSH form preferred; never a guess when there are several repositories). Nothing is looked up while the mode is off.

Still to do from that discussion: scrub addresses and tokens from sync logs and never save a token in the server's git remote; the deploy-key wizard step; "send from this computer"; per-service build recipes; rebuild only what changed.

### Secrets (step 2 of the follow-up plan)
- A git address carrying a password or token (`https://user:token@host/...`, `ssh://git:pw@host/...`) is refused everywhere: saving the settings (server and screen), every command, and every deploy, recover and roll back. The message never repeats it. Deploy keys (SSH form) are the supported way for a server to read a private repository.
- A setting saved before this rule that holds a secret blocks the deploy with a plain message, and the settings screen is sent the address with the secret removed.
- The server's saved git remote is rewritten to the clean address on every sync, so a token can never persist in `.git/config`.
- Every line of the live log and every final message passes through `GitAddressForms.Redact`, which hides `scheme://user:secret@` and well-known token shapes (GitHub `ghp_`/`github_pat_`, GitLab `glpat-`, AWS `AKIA...`) wherever they appear, including in git or build output.
- If personal access tokens are ever wanted for the server-side clone, the safe design is a temporary `chmod 600` credential file removed right after the clone, never an address or a saved remote.
- Not covered: other ShipRight modules (for example the compose repo's clone address) have their own logging and were not changed.
