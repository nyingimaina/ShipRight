# ShipRight — Novice-First Cloud Deployment and Security BRS

**Version:** 1.0  
**Status:** Draft for implementation  
**Audience:** product, design, engineering, support — and any new developer who needs to understand the whole picture without prior knowledge of Docker, git hosting, TLS or vulnerability management.  
**Supersedes:** nothing. This is a new specification. `SHIPRIGHT-BRS.md` remains the refactoring specification.

---

## How to read this document

This document uses three words deliberately:

- **MUST** — required. A release that does not meet it is not finished.
- **SHOULD** — strongly recommended. If it is skipped, the reason must be written down.
- **MAY** — optional or future work.

Every rule has a reason. Every default exists so a novice can press one button and get a safe result. Where there is a choice, this document states the chosen option and why. If you are unsure while implementing, choose the option marked **Recommended** and do not invent a third way.

The document is written so that a person who has never deployed software can follow the examples. Technical readers can jump to the data models and acceptance criteria.

### The one-sentence version

ShipRight lets a novice connect a git repository, choose where it should run, and get a safe, hardened deployment — then keeps watching that deployment every day and tells them, in plain language, what needs attention.

---

## Glossary for novices

| Word | What it means here |
|---|---|
| **Project** | One application ShipRight manages. It has a name, a source, a target and settings. |
| **Source** | Where the code lives. Today this can be a folder on the computer. In the cloud-first model it is a git repository. |
| **Repository** | A project stored in GitHub or Azure DevOps. |
| **Connection** | A company-owned permission to read (and sometimes write) repositories from one provider. It holds the token or app installation. |
| **Target** | The server or platform where the application runs. |
| **Environment** | `production`, `staging`, or `development`. Rules and options depend on it. |
| **Deployment** | One attempt to put a specific version of the project on the target. |
| **Release** | A version that is live, or kept for rollback. |
| **Dry run** | A rehearsal. ShipRight reads and checks everything but changes nothing. |
| **Provider** | A plugin that knows how to talk to GitHub, Azure DevOps, a container registry, or a deployment method. |
| **Rule** | A policy such as “never expose the database to the internet”. Rules can block or warn. |
| **Option** | A reviewed choice such as Caddy, Traefik or nginx for HTTPS. Options show their trade-offs. |
| **Finding** | A specific security problem in a specific deployment, with evidence and a fix. |
| **Advisory** | A published vulnerability record (CVE, GHSA, OSV, vendor advisory) used to create findings. |
| **SBOM** | Software bill of materials: the list of packages inside an image. |
| **Technical View** | A switch that reveals raw configuration, logs, command plans and IDs. |
| **Legacy** | A temporary switch that reveals the old deployment screens while the new ones are adopted. |
| **Tenant / account** | The billing or organisation boundary above companies. |
| **Company** | A workspace inside a tenant. Projects and connections belong to a company. |

---

## 1. Vision

ShipRight exists to remove the fear and guesswork from putting software on a server.

A novice should be able to answer four questions:

1. Where is my code?
2. Where should it run?
3. How should it go live?
4. Is it safe and healthy now?

ShipRight does the rest: it discovers the stack, proposes a deployment, explains the trade-offs, checks the result with a dry run, deploys with a rollback path, and then watches the running system every day for security and resilience problems.

The product is **cloud-first but desktop-friendly**:

- Cloud mode is always on, multi-company, provider-connected, and able to monitor deployments daily.
- Desktop mode remains useful for local folders, private machines and offline work.
- Both modes share the same domain model, rules, options and design language.

### What “smart” means

Smart is not “the model decides everything”. Smart is:

- deterministic discovery that explains what it found,
- a curated catalog of safe options,
- an AI advisor that fills gaps, ranks choices and explains trade-offs,
- deterministic rules that cannot be talked out of a security floor,
- real proof through a dry run and post-deploy checks,
- a human approval before anything irreversible.

---

## 2. Product principles

These principles apply to every screen, every API and every automated decision.

1. **Secure by default.** The safe option is the default. Convenience is a conscious exception, not a hidden setting.
2. **Outcome first, mechanism second.** Say “Put this app live on your server”, not “JIT build-on-server”.
3. **One primary action per screen.** Secondary actions live in a menu, a sheet or a details section.
4. **Show consequences before acting.** Downtime, cost, exposure, data changes and rollback must be visible in plain language.
5. **No dead ends.** Every error says what happened, why, and what to do next.
6. **Deterministic core, optional AI.** The wizard works with AI switched off. The AI cannot bypass a rule or a check.
7. **Propose, judge, prove, approve.** AI proposes; ShipRight judges and proves; a person approves anything irreversible.
8. **Progressive disclosure.** Novices see the simple path. Technical View reveals the raw details. Legacy reveals the old screens.
9. **Boring technology wins.** Prefer the simplest well-supported option that meets the requirement. Do not recommend Kubernetes to run one container.
10. **Everything is auditable.** Every rule change, waiver, AI decision, automated action and deployment records who, what, when and why.

---

## 3. Personas and journeys

### 3.1 Nusrat, the novice founder

She has a GitHub repository and a small VPS. She does not know what a reverse proxy is.

Her journey:

1. Connects GitHub with one button.
2. Picks the repository and the branch.
3. ShipRight detects a Next.js frontend and a .NET API.
4. ShipRight asks at most three blocking questions, with recommended answers.
5. She reviews a plain-language summary: “This will build two services, use Caddy for HTTPS, and keep your database private.”
6. She presses **Dry run**, sees “Everything checks out”, then **Deploy now**.
7. The deployment succeeds. ShipRight shows the URL, health and a rollback button.
8. Every morning she gets one short security digest. Most days it says “No action needed.”

### 3.2 Tunde, the technical operator

He knows Docker and wants control. He turns on **Technical View** and sees the compose file, Dockerfiles, command plan, logs, commit SHA, ports and volumes. He can edit advanced settings and use the classic screens through **Legacy** while migrating.

### 3.3 Amara, the company admin

She manages several companies and projects. She sets company rules: no public database ports, images must be pinned, critical findings fixed within 7 days. She approves waivers and sees a company-wide security posture.

### 3.4 Priya, the security reviewer

She reads findings, checks evidence, accepts or rejects VEX-style “not affected” claims, and audits automated actions. She never needs to read a raw CVE feed unless she opens Technical View.

---

## 4. Scope and non-goals

### In scope

- Connect GitHub and Azure DevOps repositories through a provider pattern.
- Create a project from a repository URL, branch and optional path prefix.
- Detect the stack and generate a deployment draft.
- Offer reviewed deployment methods, starting with build-on-server.
- Offer reviewed options (TLS, database, backups, secrets, monitoring) with trade-offs.
- Enforce layered rules with locked security baselines and time-bound waivers.
- Run a guided setup wizard with an optional AI advisor.
- Provide a new deployment screen with dry run, live status, history and rollback.
- Monitor deployed services daily for OS, framework, library and configuration security issues.
- Generate findings, internal advisories, notifications and safe remediations.
- Provide an Apple-esque design language with Technical View and Legacy switches.

### Out of scope for v1

- Writing to a repository without a reviewed branch/PR.
- Auto-deploying security fixes to production without explicit company opt-in.
- Replacing a full enterprise vulnerability management platform.
- Kubernetes orchestration as a first-class deployment method.
- Guaranteeing 24/7 desktop monitoring while the desktop app is closed.
- Being a CVE numbering authority. ShipRight reports findings; it does not issue CVEs.

---

## 5. Decisions at a glance

This table resolves every open question from the design discussion. If a future change contradicts one of these, it must be recorded as a new decision with a reason.

| # | Question | Chosen option | Why |
|---|---|---|---|
| 1 | Cloud shape | Multi-tenant cloud **and** self-hosted cloud mode; desktop remains supported | Covers SaaS, private cloud and local users with one domain model |
| 2 | What “account” means | **Tenant** (billing/organisation) above **Company** above **Project**, plus **Environment** | Prevents one person’s preferences from governing a shared company |
| 3 | Git auth first | **PAT through the existing credential resource** for v1; **GitHub App / Azure DevOps OAuth** next | Fastest safe path; long-lived PATs are a fallback, not the destination |
| 4 | Provider coverage v1 | **GitHub** and **Azure DevOps**; configurable base URL for Enterprise/Server | Covers the two requested providers without hardcoding SaaS URLs |
| 5 | Repository identity | Store the **provider repo ID**, not only the URL | Renames and transfers must not break projects |
| 6 | Source model | `ProjectSource` union: `LocalFolder` or `GitRepository` | Backward compatibility with desktop folders |
| 7 | Monorepos | **Required**: store a `pathPrefix` and detect candidate app roots | A repo URL alone is not a project |
| 8 | Multiple repos | **Allowed**: app source and compose repo, each with a role | Real projects often split code and compose |
| 9 | Control-plane source storage | **Metadata-first**; ephemeral archives/clones only when needed; no persistent source | Privacy, disk and legal safety |
| 10 | First cloud build path | **Build on the server** (target clones the repo) | Avoids a build worker; reuses the JIT engine |
| 11 | Cloud JIT routes | **Enable** in cloud with company scoping, authz and audit | Otherwise repo-URL projects cannot deploy on server |
| 12 | Watch/poll | Provider branch-head **polling first**; webhooks later | No public ingress required for v1 |
| 13 | Branch/commit | Store default branch and optional pinned commit; every deploy records the exact SHA | Reproducibility |
| 14 | AI default | **Opt-in**, never required | Privacy, cost and trust |
| 15 | AI provider v1 | OpenAI-compatible HTTP + Scripted for tests; optional OpenCode/Claude Code adapters if installed | Controllable, testable; no forced CLI install |
| 16 | AI file writes | **Propose diffs only**; ShipRight applies after approval | Safety and review |
| 17 | AI target access | **None**; only redacted facts | The model must never hold SSH keys |
| 18 | Question budget | Target **≤5 blocking questions** for common stacks; “use recommended for all” always available | Avoid interrogation fatigue |
| 19 | AI output | Strict JSON schema; unknown option/rule IDs are rejected | Prevents hallucinated tools |
| 20 | AI authority over rules | **None**; deterministic judge decides | Security cannot be prompted away |
| 21 | Rule scopes | Tenant → Company → Project → Environment; personal defaults are suggestions only | Clear governance |
| 22 | Rule weakening | **Monotonic tightening**: narrower scopes can add/tighten, never weaken a locked rule | Prevents local bypass |
| 23 | Locked rules | Security baseline is locked; resilience rules are blocking but waivable; cost rules are advisory | Security floor, operational flexibility |
| 24 | Unknown check result | **Fail closed** for critical security rules; advisory otherwise | Never claim safe when unverified |
| 25 | Waivers | Per project + environment; reason, approver, compensating control, expiry (max 90 days) | Auditable exceptions |
| 26 | Custom rule authoring | Catalog/DSL checks only; no arbitrary shell; AI may draft, a human approves | Prevents dangerous rules |
| 27 | Rule conflicts | Broader locked rule wins; otherwise the more specific scope wins; conflicts are shown | Predictable resolution |
| 28 | Rule evaluation points | Discovery, AI proposal, config validation, dry run, deploy gate, post-deploy, daily | Consistent enforcement |
| 29 | Default profiles | Production: Baseline + Resilience + Hardened Security. Staging: Baseline + Resilience. Development: Baseline | Secure by default, not noisy |
| 30 | Options catalog | Curated, versioned, 2–4 options per decision; AI selects and explains, never invents | Supply-chain safety |
| 31 | v1 decision points | TLS/reverse proxy, database, backups, secrets, monitoring, deployment method | Highest-value trade-offs |
| 32 | Recommendation default | **Simplicity + security floor**; filters for cheapest, simplest, most secure | Best for novices |
| 33 | Resource estimates | Ranges, labelled as estimates; measured actuals after deploy | Honest and improving |
| 34 | Existing stack | **Integrate over replace**; detect what is already running | Avoid breaking working systems |
| 35 | Managed service cost | Always show estimated monthly cost and lock-in | No surprise bills |
| 36 | Company standards | Companies can restrict the option catalog | Governance |
| 37 | Design memory location | `docs/design/design-language.md` canonical; root `AGENTS.md` short pointer | Applies to future work without affecting other projects |
| 38 | Technical View | Per-user, persisted, a lens not a permission; never reveals secrets | Expert access without a second app |
| 39 | Legacy | Per-user toggle; company admin can hide; routes stay reachable with a banner; removal 12 months after GA | Safe migration, no broken bookmarks |
| 40 | Deployment screen primary action | **Set up deployment** when unconfigured; **Deploy now** when ready | Always one obvious next step |
| 41 | Security monitoring availability | Cloud always-on; desktop on launch and every 24h while running; optional background service later | Honest about desktop limits |
| 42 | Security scan scope v1 | Container image/SBOM, app dependencies, host OS packages, TLS expiry, exposed ports | Highest-value coverage |
| 43 | Advisory sources v1 | OSV, distro trackers, GitHub Advisory, nginx/F5, Caddy, MariaDB, CISA KEV, FIRST EPSS; NVD enrichment | Accuracy over a single feed |
| 44 | Scanner | **Trivy** behind a provider; Grype optional | Broad, proven, SBOM-aware |
| 45 | Scan cadence | Daily + on deploy + on KEV/vendor critical + on demand | Daily is a floor, not a ceiling |
| 46 | Inventory dedupe | Shared server inventory + per-deployment inventory | Avoid five copies of the same OS finding |
| 47 | Notification channels v1 | In-app + email; webhook/desktop later | Enough signal without a new integration project |
| 48 | Digest time | Daily at **08:00** in the user’s time zone; urgent findings immediately | Predictable, not noisy |
| 49 | Auto-remediation default | Detect + notify + open PR/rebuild + dry run; **no production deploy** without approval | Safety first |
| 50 | Staging automation | Opt-in patch-only auto-deploy after rules, health and rollback checks | Earn trust in a safe environment |
| 51 | Fix SLAs | Critical/KEV 7 days; high 30; medium 90; low best effort | Industry-standard prioritisation |
| 52 | Retention | Findings 12 months; audit 24 months; SBOM life of image + 90 days | Auditable, bounded |
| 53 | Offline support | Signed advisory database snapshots | Air-gapped customers |
| 54 | Managed third-party services | Vendor advisories + configuration checks; label coverage limits | Do not pretend to scan what we cannot |
| 55 | Wording | “Finding” and “recommendation” internally; “advisory” only for published records | Do not imply ShipRight issues CVEs |
| 56 | Multi-company | Connections, projects, findings, notifications and audit are company-scoped; fail closed | Tenant isolation |
| 57 | Accessibility | WCAG 2.2 AA; keyboard; reduced motion; dark parity | Inclusive by default |
| 58 | Testing | TDD; contract tests for providers; scripted AI; fixture-based rules; visual regression | Correctness and regression safety |
| 59 | AI layer location | **`D:\work\nyingi\code\systems\hakiri`**, its own solution (`Hakiri.slnx`) and its own version | Shared by ShipRight, Tinkerdeck and DevTeam without coupling |
| 60 | AI package names | `Hakiri.*` | The layer is a product of its own, not a ShipRight submodule |
| 61 | Zero-config AI setup | `Hakiri.Toolkit` detects known tools, returns install/sign-in steps, and never installs silently | A novice can start with one call; a host still asks before changing the machine |
| 62 | Self-deploy target | The **production server stays on the legacy/manual path** until the new path is proven; all self-deploy testing happens on a **separate staging box** | A broken self-deploy must never take down the tool that fixes it |
| 63 | Bootstrap from scratch | The desktop app writes a complete **server + project + deploy configuration** from a reviewed plan; it previews before writing and never deploys | Proves the from-scratch path without hand-edited config and without production risk |
| 64 | Minimum to start a deployment | A **public address alone** is enough; the user, SSH key, folder and domain are optional and added later | Progressive disclosure; a server can be recorded before it is fully prepared |
| 65 | Compatibility spine is additive and dark | Every new contract is introduced beside the old path; flags default off and no existing route or behaviour changes | Old and new coexist until a wave proves itself |
| 66 | Shadow advice never acts | The shadow advisor records proposals only, is flag-gated, and works with AI switched off | Proves value with zero write risk |
| 67 | Repository inspection | Read-only GitHub metadata (default branch, branches, compose files, Dockerfiles) **without cloning**; token optional for private repos | Fast onboarding with no source copy |

---

## 6. Sources and git providers

### 6.1 The source union

A project **MUST** have exactly one source:

```text
ProjectSource
  LocalFolder   { path }
  GitRepository { connectionId, repoRef, branch, pathPrefix, pinnedCommit? }
```

Rules:

- Existing projects with `GitConfig.RepoPath` keep working unchanged.
- New cloud projects **MUST** use `GitRepository`.
- A project **MUST NOT** require a local folder when it has a `GitRepository` source.
- Detectors, setup, builds and AI evidence **MUST** read through an `ISourceTree`, not `Directory.Exists`.
- `ISourceTree` **MUST** provide: list directory, read file, file exists, file hash, and a stable revision identifier.

### 6.2 Git connection

A `GitConnection` is company-scoped and backed by a secret:

```text
GitConnection
  id
  companyId
  provider          github | azure-devops
  baseUrl           https://github.com, https://dev.azure.com/org, enterprise URL
  accountName
  credentialResourceId
  scopes            read-only | read-write
  status            connected | needs-attention | revoked
  lastCheckedAt
  createdBy, createdAt
```

Rules:

- Only a company admin **MUST** be able to create, rotate or revoke a connection.
- A project owner **MAY** request a connection.
- The token **MUST NOT** be returned by any API, log, AI prompt or export.
- A connection **MUST** be tested before it is saved.
- Revoking a connection **MUST** fail closed for projects that depend on it; they show “reconnect required”, not a silent failure.

### 6.3 Provider contract

```csharp
interface IGitProvider
{
    string Id { get; }
    bool CanHandle(RepoUrl url);

    Task<RepoInfo> GetRepoAsync(RepoRef repo, GitAuth auth, CancellationToken ct);
    Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(RepoRef repo, GitAuth auth, CancellationToken ct);
    Task<CommitInfo> GetCommitAsync(RepoRef repo, string reference, GitAuth auth, CancellationToken ct);
    Task<SourceTree> GetTreeAsync(RepoRef repo, string reference, string pathPrefix, GitAuth auth, CancellationToken ct);
    Task<byte[]> GetFileAsync(RepoRef repo, string path, string reference, GitAuth auth, CancellationToken ct);
    Task<Stream> DownloadArchiveAsync(RepoRef repo, string reference, string pathPrefix, GitAuth auth, CancellationToken ct);
    Task<WebhookRegistration> EnsureWebhookAsync(RepoRef repo, GitAuth auth, CancellationToken ct); // optional
}
```

Rules:

- The registry chooses the provider from the URL. Unknown providers **MUST** be rejected with a plain-language message.
- Providers **MUST** expose capabilities: `CanReadTree`, `CanDownloadArchive`, `CanWrite`, `CanWebhook`.
- Providers **MUST** use the provider’s stable repo ID.
- Providers **MUST** return typed errors: `NotFound`, `Unauthorized`, `Forbidden`, `RateLimited`, `Unavailable`, `InvalidUrl`.
- Providers **MUST** cache metadata with ETag/TTL and respect rate limits.
- Providers **MUST NOT** be given arbitrary URLs except allow-listed provider hosts or configured enterprise base URLs.

### 6.4 URL forms

**GitHub**

- `https://github.com/owner/repo`
- `https://github.com/owner/repo.git`
- `git@github.com:owner/repo.git`
- `ssh://git@github.com/owner/repo.git`
- GitHub Enterprise: `https://github.example.com/owner/repo`

**Azure DevOps**

- `https://dev.azure.com/org/project/_git/repo`
- `https://org@dev.azure.com/org/project/_git/repo`
- `git@ssh.dev.azure.com:v3/org/project/repo`
- Legacy: `https://org.visualstudio.com/project/_git/repo`
- Azure DevOps Server: configured base URL + `project/_git/repo`

Rules:

- A URL containing a password or token **MUST** be rejected and never echoed back. Reuse `GitAddressForms`.
- The provider **MUST** parse `owner`, `project`, `repo`, `baseUrl` and `webUrl`.
- If parsing is ambiguous, the wizard **MUST** ask the person to choose the provider rather than guessing.

### 6.5 Authentication

**v1 — PAT through a credential resource**

- Read-only scopes by default.
- Write scopes only when the company enables “ShipRight may open branches/PRs”.
- The connection tests access immediately.
- The UI shows “Connected as <account>, read-only, last checked <time>”.

**Next — GitHub App / Azure DevOps OAuth**

- Preferred for cloud production: short-lived, repo-scoped, revocable.
- GitHub App installation tokens are cached and refreshed; the private key is a company secret.
- Azure DevOps OAuth uses Entra ID; PAT remains the fallback for enterprise setups.

Rules:

- Tokens **MUST** be encrypted at rest.
- Tokens **MUST NOT** appear in prompts, diagnostics, URLs, logs or exports.
- Token use **MUST** be audited (connection, last used, failures).
- Rotation **MUST** be a one-click action with a test.

### 6.6 Monorepos and path prefixes

A repository is not a project. The wizard **MUST**:

1. List candidate app roots by looking for `docker-compose.yml`, `compose.yaml`, `Dockerfile`, `package.json`, `*.csproj`, `pyproject.toml`, `go.mod`, `Cargo.toml`.
2. Show each candidate with the evidence (file path).
3. Let the person choose one root.
4. Store it as `pathPrefix`.
5. Resolve all build contexts and Dockerfile paths relative to that prefix consistently.

Rules:

- `pathPrefix` **MUST** be empty for a repository-root project.
- `pathPrefix` **MUST NOT** contain `..`, absolute paths or backslashes.
- If more than one compose file is found, the wizard **MUST** ask which one is the deployment root.
- The dry run **MUST** show the final resolved paths.

### 6.7 Multiple repositories

A project **MAY** reference more than one repository:

```text
GitRepositoryRef
  role: Source | Compose
  connectionId, repoRef, branch, pathPrefix
```

Rules:

- The app source repository is required for build-on-server.
- The compose repository is optional.
- The wizard **MUST** detect when the compose file is in a different repository.
- Per-repo credentials **MUST** be supported; do not assume one token works for both.

### 6.8 Cloud build strategy

**v1 — build on the server**

- The target server clones the repository and builds there.
- The control plane needs only metadata, the plan and the dry run.
- This requires enabling the JIT routes in cloud mode with company scoping, authz and audit.

**Later — registry builds on a worker**

- The worker gets an ephemeral workspace.
- Use a shallow clone or provider archive for the exact commit.
- Clean up after the build.
- Optional bare mirror cache per company/repo with TTL.
- Never keep long-lived source on the control plane.

Rules:

- A cloud project **MUST NOT** depend on a folder on the control-plane machine.
- A build **MUST** record the exact commit SHA.
- A build **MUST** be reproducible from the recorded repo, commit and build settings.

### 6.9 Watch and polling

- v1 **MUST** poll the branch head through the provider API.
- Polling **MUST** use ETag/If-None-Match and back off on rate limits.
- Webhooks **MAY** be added later with signature verification, replay protection and repo→project mapping.
- A push event **MUST NOT** deploy automatically unless the project opts in; it may start a dry run or a build.

### 6.10 Backward compatibility and migration

- Existing `GitConfig.RepoPath` projects **MUST** continue to work.
- The wizard **SHOULD** offer “Use this folder’s origin as the repository” for existing projects.
- A project **MAY** have both a local folder and a repository reference during migration; the repository wins when present.
- Removing local-folder support is a separate, later decision.

### 6.11 Worked example — new project from GitHub

1. Nusrat chooses **Connect GitHub**.
2. She pastes a fine-grained PAT with read-only access to one repository.
3. ShipRight validates the token, shows the repository, default branch `main`, and last commit.
4. She chooses the repository and branch.
5. ShipRight detects `web/package.json` (Next.js) and `api/Api.csproj` (.NET), and two candidate compose files.
6. She chooses `deploy/docker-compose.yml` as the compose file and the repository root as the project root.
7. ShipRight stores `pathPrefix = ""`, `composeFile = "deploy/docker-compose.yml"`, `branch = "main"`.
8. The wizard proceeds to target, rules and options.

### 6.12 Pitfalls to avoid

- **Do not** ask for a repository URL and then assume the repository root is the app.
- **Do not** compare package versions across distros without the distro tracker.
- **Do not** pull a whole monorepo into the control plane to answer a metadata question.
- **Do not** store a PAT in a project field.
- **Do not** let a project use another company’s connection.
- **Do not** silently fall back to a local folder when the repository is unreachable. Say “reconnect required”.
- **Do not** treat a provider rename as a new repository; use the provider ID.
- **Do not** let the AI choose a repository or branch without showing the evidence.

## 7. Deployment methods

### 7.1 The method provider

Deployment methods are providers, not branches in a page:

```csharp
interface IDeploymentMethod
{
    string Id { get; }
    string Title { get; }             // novice-facing
    string PlainSummary { get; }      // one or two sentences
    MethodCapabilities Capabilities { get; }

    Task<MethodFit> CheckFitAsync(ProjectConfig project, CancellationToken ct);
    Task<DeploymentPlan> PlanAsync(ProjectConfig project, DeployOptions options, CancellationToken ct);
    Task<DeploymentResult> DeployAsync(DeploymentPlan plan, IProgress<DeployEvent> progress, CancellationToken ct);
    Task<RollbackResult> RollbackAsync(ReleaseRef release, CancellationToken ct);
}
```

`DeploymentPlan` **MUST** contain:

- the exact commit SHA
- the target and environment
- the services and images
- the command plan (available in Technical View)
- expected downtime
- resource impact
- cost impact
- rule results
- rollback plan
- warnings and required approvals

`MethodFit` **MUST** explain why a method is unavailable: no server, no repository access, no Docker, no registry credential, insufficient disk, unsupported platform.

### 7.2 Methods in v1

| Method | Novice label | Status |
|---|---|---|
| Build on the server | **Put it live on this server** | v1, recommended when a server is configured |
| Build here, push to a registry | **Build here and push an image** | v1.1 |
| Use a compose repository | **Use my compose repository** | v1.1 |
| Connect a platform | **Connect a platform** | Later |

Rules:

- **Build on the server MUST be the first method.** It needs no build worker, uses the target’s git access, and reuses the existing JIT engine.
- A project **MUST** have exactly one active method.
- Changing method **MUST** be a guided flow, not a raw dropdown.
- A method **MUST NOT** bypass rules, health checks, audit or rollback.
- A method that cannot run **MUST** say why in plain language and offer the next best option.

### 7.3 The new deployment screen

The screen **MUST** be a state machine:

```text
NotConfigured → Ready → Deploying → Live
                     ↘ NeedsAttention
                     ↘ RolledBack
```

**NotConfigured**

- Headline: “This project is not set up to deploy yet.”
- Primary action: **Set up deployment**.
- Show what is missing: source, target, method, credentials.

**Ready**

- Headline: “Ready to deploy <version>.”
- Primary action: **Deploy now** (split button: Dry run, Restart all services).
- Consequence summary: downtime, target, services, cost, rule results.
- Secondary: history, rollback, settings.

**Deploying**

- A calm progress timeline: preparing, fetching, building, stopping, starting, checking.
- One sentence per phase. No raw log by default.
- Technical View reveals the live log and commands.
- Cancel is available until the point of no return; after that, the action is “Roll back”.

**Live**

- Version, commit, URL, health, last deploy time.
- Primary action remains **Deploy now** for a new version.
- Rollback is visible, not buried.
- Security and rule status are visible.

**NeedsAttention**

- Plain-language cause: “The new version did not become healthy.”
- What ShipRight did: “The previous version was restored.”
- One primary action: **Try again** or **Run recovery**.
- Diagnostics download link.

### 7.4 Legacy

The old deployment pages **MUST** remain reachable through a **Legacy** switch:

- Default off for new users.
- Per-user setting; company admins **MAY** hide it company-wide.
- Direct links still work and show a banner: “Classic deployment screen. The new screen is recommended.”
- Legacy **MUST NOT** bypass rules, security checks or audit.
- Legacy usage **MUST** be measured.
- Removal date: 12 months after the new screen reaches general availability, announced in the banner and release notes.

### 7.5 Pitfalls

- **Do not** build a second deployment engine for the new screen. Wrap the existing JIT/registry/compose engines behind `IDeploymentMethod`.
- **Do not** hide rollback or history in Legacy.
- **Do not** show a raw log as the primary deployment experience.
- **Do not** let “Legacy” become permanent. Instrument it and set a date.
- **Do not** make method selection a technology quiz. Use outcome labels.

### 7.6 Bootstrap from the desktop

The desktop app can create a complete deployment configuration from nothing: no existing project, no hand-written
compose file and no hand-written `.env` on the server. The same flow works for **any deployment**, not only the
ShipRight preset.

- **A public address is the minimum.** The user, SSH key, folder and domain are optional. Missing pieces are shown as
  **deploy blockers** and can be added later; they never stop the configuration from being written.
- A **pure planner** turns a small request into a `BootstrapPlan` — a `ServerConfig`, a `ProjectConfig` and a
  `JitDeployConfig` — plus problems, warnings, deploy blockers and ordered steps. It writes nothing, so the result
  can be reviewed.
- **Preview** shows what will be written, what still needs attention, and what can wait.
- **Apply** writes the server, project and build-on-server settings through the existing stores. A plan with
  problems writes nothing.
- An existing server with the same address, and an existing project with the same id or name, are updated instead of
  duplicated, so adding a domain or a key later is another reviewed apply.
- When a user is present and no SSH key is supplied, ShipRight makes a managed key and returns the public key.
  Authorizing it stays a deliberate, visible step.
- The **ShipRight self preset** fills the repository, the `app` service, the compose file and a complete
  environment, including generated database secrets. Without a domain it serves over HTTP on the address and sets
  the public/CORS URLs to match.
- A **custom app** supplies its own name, repository, compose file, services and optional environment.
- It is mounted only in desktop mode, beside build-on-server. It never deploys; deploy stays a separate, confirmed
  action.

---

## 8. The reusable AI layer

### 8.1 What it is

The AI layer is a **standalone, product-agnostic library** for talking to AI runtimes and models. It is shared by ShipRight, Tinkerdeck and, later, DevTeam.

Working name: **`Hakiri`**. It lives at **`D:\work\nyingi\code\systems\hakiri`** as its own solution (`Hakiri.slnx`) with its own version and release cadence. ShipRight, Tinkerdeck and DevTeam consume its packages; they do not copy its source and they do not own its code.

It **MUST NOT** reference:

- ShipRight types (`ProjectConfig`, `HouseRule`, `SecurityFinding`, `DeploymentPlan`)
- DevTeam types (`AgentRequest` with `Role`/`Stage`/`Round`, `HouseRule`, pipeline stages)
- Tinkerdeck types (apps, terminals, sessions)
- any product-specific prompt or policy

The consuming application owns prompts, schemas, tools, rules, approvals and audit. The library owns transport, sessions, streaming, retries, limits, redaction and usage.

### 8.2 Packages

The layer **MUST** be published as small packages so a consumer takes only what it needs:

```text
Hakiri.Abstractions
Hakiri.Runtime.Process
Hakiri.Runtime.OpenCode
Hakiri.Runtime.ClaudeCode
Hakiri.Runtime.Codex
Hakiri.Runtime.Gemini
Hakiri.Provider.OpenAiCompatible
Hakiri.Provider.Ollama          (later)
Hakiri.Scripted                 (tests and demos)
Hakiri.Toolkit                  (catalog, detection, install/auth guidance)
Hakiri.Sidecar                  (JSON-RPC 2.0 sidecar for non-.NET hosts)
Hakiri.Diagnostics              (transcripts, redaction, usage)
```

Rules:

- `Abstractions` **MUST** have no dependency on a specific CLI, HTTP client or product.
- Adapter packages **MUST** depend only on `Abstractions` and their own transport.
- The library **MUST** be versioned independently with semantic versioning.
- ShipRight, Tinkerdeck and DevTeam **MUST** consume it as a package, not by copying source.
- A monorepo folder (`libs/Hakiri`) is acceptable initially, but the projects **MUST** be buildable and packable on their own. The current home is `D:\work\nyingi\code\systems\hakiri`.

### 8.3 Core contract

```csharp
interface IAiRuntime
{
    string Id { get; }
    AiRuntimeCapabilities Capabilities { get; }
    Task<AiResult> RunAsync(AiTask task, CancellationToken ct);
    IAsyncEnumerable<AiEvent> StreamAsync(AiTask task, CancellationToken ct);
}

sealed record AiTask
{
    string SystemPrompt;
    IReadOnlyList<AiMessage> Messages;
    IReadOnlyList<AiEvidence> Evidence;       // labelled, untrusted data
    AiResponseFormat? ResponseFormat;         // text or JSON schema
    IReadOnlyList<AiTool>? Tools;
    string? WorkingDirectory;
    string? Model;
    AiBudget Budget;                          // tokens, cost, wall clock
    TimeSpan Timeout;
    AiPolicy Policy;                          // tool allow-list, redaction, network
    IReadOnlyDictionary<string, string>? Metadata; // consumer-defined, never secrets
}

sealed record AiResult
{
    bool Success;
    string? Text;
    JsonElement? Structured;
    IReadOnlyList<AiToolCall> ToolCalls;
    AiUsage Usage;                            // input/output/cache tokens, cost
    string? SessionId;
    string? ErrorCode;                        // Unauthorized, RateLimited, ...
    string? ErrorMessage;
    TimeSpan Duration;
    IReadOnlyList<string> Models;
}
```

Rules:

- `AiTask.Metadata` **MUST NOT** contain secrets. It exists for correlation IDs and labels.
- `AiEvidence` **MUST** carry a trust label (`UntrustedRepositoryFile`, `TrustedSystemFact`, etc.) so consumers can delimit it.
- `AiResult.Structured` **MUST** be validated by the consumer against its schema. The library validates JSON syntax only.
- `AiResult.ErrorCode` **MUST** use a stable enum/string set so consumers can react consistently.
- The library **MUST NOT** decide whether a rule, deployment or file change is allowed.

### 8.4 Capabilities

```text
AiRuntimeCapabilities
  canStream
  canResumeSession
  canUseTools
  canEditFiles
  canRunCommands
  supportsJsonSchema
  supportsImages
  isLocalOnly
  reportsUsage
  reportsCost
  listsModels
```

A consumer **MUST** check capabilities before offering a feature. For example, ShipRight must not ask a text-only provider to edit files.

### 8.5 Runtimes in v1

| Runtime | Use | Notes |
|---|---|---|
| **OpenAI-compatible HTTP** | Structured analysis, ranking, explanations | Works with OpenAI, Azure OpenAI-compatible endpoints, local gateways; no file editing |
| **OpenCode** | Repo-aware agent with tools | Headless `opencode run`; JSON events; optional |
| **Claude Code** | Repo-aware agent with tools | Headless `claude -p`; stream-json; optional |
| **Scripted** | Tests and demos | Replays fixtures; no network; deterministic |
| **Ollama** | Local/offline models | Later; `isLocalOnly = true` |

Rules:

- The library **MUST** probe installed runtimes and report a plain-language status.
- The library **MUST NOT** install a runtime without the consumer’s explicit consent.
- The library **MUST** support per-task model selection.
- The library **MUST** tolerate CLI output changes and report a clear “unsupported output” error rather than crashing.

### 8.6 Streaming, sessions and cancellation

- `StreamAsync` **MUST** emit typed events: `Started`, `TextDelta`, `Thinking`, `ToolStarted`, `ToolFinished`, `Usage`, `Completed`, `Failed`.
- A consumer **MUST** be able to cancel at any point; cancellation **MUST** stop the underlying process tree and release resources.
- The library **MUST** support session resume where the runtime does; otherwise it **MUST** say so.
- The library **MUST** detect stalls (no output and no file activity for a configured window) and stop the process with a clear error.
- The library **MUST** enforce a hard ceiling and a per-task timeout.

### 8.7 Limits, retries and cost

- The library **MUST** support a token/cost budget per task and per session.
- The library **MUST** report usage when the runtime reports it; it **MUST NOT** invent numbers.
- Retries **MUST** be limited and **MUST NOT** retry authentication, permission, invalid-request or policy failures.
- Rate limits **MUST** be surfaced as `RateLimited` with a retry-after hint when available.
- The library **MUST** support a per-runtime concurrency limit and a start spacer for CLIs that collide on their own state.

### 8.8 Redaction and security

- The library **MUST** provide a redaction pipeline:
  - known secret values (tokens, keys, passwords)
  - URL user-info
  - common token shapes
  - consumer-supplied patterns
- The library **MUST** redact before writing logs, transcripts, diagnostics or audit records.
- The library **MUST NOT** send environment variables, SSH keys, provider tokens or `.env` values to a model.
- Tool-enabled runtimes **MUST** run with:
  - a working-directory jail
  - a tool allow-list
  - no network by default unless the consumer enables it
  - no host secrets in the process environment
- The library **MUST** expose a `ToolPolicy` so the consumer decides what the model may do.
- The library **MUST NOT** contain a default policy that grants shell or write access.

### 8.9 Prompts and domain logic

- The library **MUST NOT** contain ShipRight deployment prompts, DevTeam role prompts or Tinkerdeck terminal prompts.
- Each consumer **MUST** own its prompt builders and JSON schemas.
- The library **MAY** provide generic helpers: evidence formatting, schema validation, transcript rendering, token estimation.
- The library **MUST** support prompt/schema version labels supplied by the consumer, so decisions can be reproduced.

### 8.10 Consumers

**ShipRight**

- Uses the library for the deployment advisor: evidence pack → structured proposal → questions → explanations.
- Uses the Scripted runtime for tests.
- Never lets the model write files or run deployment commands.

**Tinkerdeck**

- Uses the library for “what is wrong with this app?” summaries, log explanations and suggested commands.
- Uses the same capabilities and redaction; its prompts and tools live in Tinkerdeck.

**DevTeam**

- Later replaces its inbuilt `DevTeam.Agents*` implementations with the shared package.
- Keeps a thin `IAgentRuntime` compatibility shim until the engine is migrated.
- Must not fork the OpenCode/Claude Code adapters; one implementation only.

### 8.11 Migration from DevTeam’s inbuilt layer

1. Extract the generic parts: process runner, stall detection, retries, provider-error classification, token/cost parsing, OpenCode and Claude Code adapters, scripted runtime.
2. Remove engine concepts from the contract (`Role`, `Stage`, `Round`, `ArtifactPath` stay in DevTeam).
3. Publish the packages.
4. ShipRight and Tinkerdeck adopt them.
5. DevTeam adds a compatibility adapter and deletes the duplicate adapters.
6. A conformance suite proves both old and new paths behave the same where it matters.

Rules:

- **Do not** copy the code into each app. One source of truth.
- **Do not** let the shared layer reference DevTeam’s engine or ShipRight’s modules.
- **Do not** change DevTeam’s behavior during extraction unless a test proves the change is intended.

### 8.12 Conformance tests

Every runtime adapter **MUST** pass a shared conformance suite:

- starts, streams and completes
- maps errors consistently
- honours cancellation and timeout
- reports usage or says it cannot
- handles an unsupported output format without crashing
- redacts a known secret
- rejects an oversized prompt with a clear error
- never leaks the token in logs or transcripts

### 8.13 Pitfalls

- **Do not** name the library after ShipRight. It is not a ShipRight component.
- **Do not** put deployment or code-review prompts in the library.
- **Do not** let one consumer’s tool policy become the default for another.
- **Do not** trust the model’s JSON. Validate it in the consumer.
- **Do not** log raw prompts before redaction.
- **Do not** make a CLI runtime mandatory for the library to work.

### 8.14 Ownership and change control

- The `hakiri` solution owns the contract, adapters, process runner, redaction and conformance tests.
- Consumers own their prompts, schemas, tool policies and approvals.
- The abstractions package **MUST** stay free of product names and product logic.
- A breaking contract change **MUST** be a major version.
- Until the contract reaches 1.0, all packages are versioned together.
- The release checklist **MUST** run the conformance suite and pack every package.
- ShipRight **MUST NOT** take a source copy of Hakiri. It references the package.
- DevTeam **MUST** keep its old `IAgentRuntime` as a compatibility shim only until it has migrated; the OpenCode and Claude Code implementations must not exist twice.
- Tinkerdeck **MUST** consume the same packages and keep its own prompts and tools.

Implementation status at the time of writing:

- `Hakiri.slnx` exists at `D:\work\nyingi\code\systems\hakiri` and is published at `https://github.com/nyingimaina/hakiri` (branch `master`).
- `Hakiri.Abstractions`, `Hakiri.Scripted`, `Hakiri.Provider.OpenAiCompatible`, `Hakiri.Runtime.Process`, `Hakiri.Runtime.OpenCode`, `Hakiri.Runtime.ClaudeCode`, `Hakiri.Runtime.Codex`, `Hakiri.Runtime.Gemini`, `Hakiri.Toolkit`, `Hakiri.Sidecar` and `Hakiri.Diagnostics` exist and compile.
- The contract tests pass (44 at the current commit), and every package packs.
- The `hakiri --stdio` sidecar speaks JSON-RPC 2.0 over stdin/stdout; its protocol is documented and tested against the code.
- A Rust example consumes the sidecar.
- The OpenAI-compatible provider streams SSE deltas and the final usage chunk; CLI adapters stream line-level text, reasoning and tool events.
- Initial commit: `b179362` on the local `hakiri` repository; published at `https://github.com/nyingimaina/hakiri`.
- The Toolkit detects Claude Code, opencode, Codex, Gemini, OpenAI-compatible APIs and Ollama; it returns install/sign-in steps and never installs silently.
- The OpenCode and Claude Code adapters default to plan/read-only behaviour; a consumer must opt into editing explicitly.

### 8.15 The zero-config toolkit

`Hakiri.Toolkit` is the “make it almost invisible” layer.

It **MUST**:

- provide a catalog of known tools: Claude Code, opencode, OpenAI Codex CLI, Gemini CLI, OpenAI-compatible APIs and Ollama;
- detect installed commands and versions;
- probe sign-in state where the CLI documents a safe status command;
- return exact install and sign-in steps, including the provider URL;
- select the highest-priority ready tool automatically;
- prefer a local model when asked;
- support an optional `hakiri.json` for preferred tools, command overrides and priorities;
- work with one call: `await HakiriBootstrap.SetupAsync()`;
- offer a full detect → confirm → install → detect flow through `SetupWithInstallAsync`;
- never install, sign in or change the machine silently.

It **MUST NOT**:

- automate credential entry;
- scrape a browser session;
- run an interactive sign-in;
- hide the command it is about to run;
- run an install step that is not marked `CanRun`;
- require a specific vendor.

The setup result **MUST** be usable by a CLI, a desktop app or a web UI:

```text
Summary: No AI tool was found. Install one of: Claude Code, opencode, Codex.

1. Install Claude Code
   npm install -g @anthropic-ai/claude-code
2. Sign in to Claude
   claude auth login
   https://docs.claude.com/en/docs/claude-code/authentication
```

A host that wants to install runs `HakiriInstaller.RunAsync(step)` only after the person says yes.

### 8.16 Non-.NET hosts

`Hakiri.Sidecar` is a `hakiri` executable that exposes the toolkit over JSON-RPC 2.0 on stdin/stdout.

- stdout is protocol only; stderr is logs.
- Methods: `hello`, `setup`, `refresh`, `ask`, `cancel`, `install`, `shutdown`, `ping`.
- Events: `started`, `text`, `thinking`, `tool-started`, `tool-finished`, `usage`, `completed`, `failed`.
- The protocol document and the code constants are checked by a test.
- A Rust example shows a persistent client.

This keeps non-.NET hosts free of a .NET dependency while reusing the same Hakiri implementation.

---

## 9. The guided setup wizard

### 9.1 The promise

A novice **MUST** be able to go from “I have a repository” to “my app is live” without knowing Docker, YAML, TLS or which reverse proxy to choose.

The wizard **MUST**:

- prefill everything it can discover,
- ask only what it cannot discover,
- explain every recommendation,
- show a dry run before the first deploy,
- never change a file without showing the diff and getting approval.

### 9.2 Steps

```text
0. Start            What are we setting up?
1. Source           Repository, provider connection, branch, path prefix
2. Target           Server or platform, environment
3. Detect           Stack, services, ports, compose, Dockerfiles, env keys
4. Method           How should it go live?
5. Options          TLS, database, backups, secrets, monitoring
6. Rules            What is required, what is recommended, what is waived
7. Review           Plain-language summary + diffs + technical view
8. Dry run          Prove it without changing anything
9. Save             Store the draft; enable Deploy
```

Rules:

- The wizard **MUST** be resumable. Closing the browser or app **MUST NOT** lose progress.
- The wizard **MUST** be idempotent. Running it again **MUST** reconcile, not duplicate.
- The wizard **MUST** work with AI switched off. The deterministic path is the baseline.
- The wizard **MUST** work with a local folder as the source, not only a repository.

### 9.3 Step 1 — Source

Ask:

- “Where is your code?” (repository or folder)
- If repository: connect a provider or paste a URL.
- “Which branch should go live?”
- “Which folder inside the repository is the app?” (only if more than one candidate)

Show:

- provider, account, repository, visibility
- default branch and last commit
- candidate app roots with evidence
- detected compose file(s)

Rules:

- A private repository **MUST** show “Connected as …” before continuing.
- A URL with embedded credentials **MUST** be rejected.
- A missing path prefix **MUST** be detected, not guessed silently.
- The wizard **MUST** show the exact commit the first deploy would use.

### 9.4 Step 2 — Target

Ask:

- “Where should this run?” (server or platform)
- “Is this production, staging or development?”

Show:

- server name, OS, Docker/Compose availability, disk/RAM headroom
- existing stacks and ports that might clash
- whether the server already has a reverse proxy

Rules:

- A target that cannot run the chosen method **MUST** be disabled with a reason.
- Production **MUST** be labelled clearly and **MUST NOT** be the default for a first-time user without confirmation.
- The wizard **MUST** warn when the target is already serving a stack with the same name.

### 9.5 Step 3 — Detect

Detection **MUST** be deterministic and show evidence:

- language/framework (from manifests and lockfiles)
- services (from compose or project files)
- build contexts and Dockerfiles
- exposed ports
- environment variable **names** (never values)
- database usage (provider, connection string keys)
- health endpoints if discoverable
- static assets, uploads and volumes

Output:

```text
Detected
  Next.js 15 in web/          package.json
  .NET 10 API in api/         Api.csproj
  MariaDB 10.5                docker-compose.yml:12
  Compose file                deploy/docker-compose.yml
  Public ports                80, 443
  Env keys                    DB_HOST, DB_USER, DB_PASSWORD (values hidden)
```

Rules:

- Detection **MUST NOT** send file contents to the AI by default. It sends only what is needed and redacted.
- Every detected fact **MUST** show its source (file and line where possible).
- A low-confidence detection **MUST** be labelled “Likely” and confirmed.
- The wizard **MUST** let the person correct any detected fact.

### 9.6 Step 4 — Method

Ask one outcome question:

> “How should this go live?”

Show 2–4 method cards:

- Put it live on this server (recommended when available)
- Build here and push an image
- Use my compose repository
- Connect a platform (later)

Each card shows:

- one-sentence outcome
- requirements
- expected time
- downtime
- resource impact
- risk
- “Recommended” badge and why

Rules:

- The wizard **MUST NOT** show a method that cannot run.
- The wizard **MUST** explain why a method is unavailable.
- Changing method later **MUST** be a guided flow.

### 9.7 Step 5 — Options

Only decision points with a real trade-off are shown. See section 11.

Rules:

- Default recommendation **MUST** be preselected.
- “Use recommended for all” **MUST** be available.
- Each option **MUST** show resource, operational and cost trade-offs.
- The wizard **MUST** warn when the chosen options exceed the target budget.

### 9.8 Step 6 — Rules

Show:

- blocking rules that pass
- blocking rules that fail, with one fix each
- advisory rules
- active waivers and their expiry

Rules:

- A blocking failure **MUST** stop the wizard at the review step until fixed or waived by an authorised person.
- A locked rule **MUST NOT** show a toggle. It shows “Required by your company” or “Required by ShipRight”.
- A waiver **MUST** require a reason and an expiry.

### 9.9 Step 7 — Review

The review **MUST** be readable by a non-technical person:

```text
What will happen
  ShipRight will build 2 services from github.com/acme/shop at commit 3f2a9c1.
  It will put them on shop.example.com with automatic HTTPS.
  The database stays private.
  Expected downtime: about 10 seconds.
  Rollback: the previous version is kept.
  Estimated extra memory: about 60 MB.

Changes to your repository
  api/Dockerfile        new file        [View diff] [Approve]
  deploy/compose.yml    build stanza    [View diff] [Approve]

Checks
  12 rules passed · 0 blocking · 1 advisory
  [Technical View]
```

Rules:

- Every file change **MUST** show a diff.
- Approval **MUST** be per change or “approve all”.
- No file **MUST** be written before approval.
- The review **MUST** show the exact commit and the rollback path.

### 9.10 Step 8 — Dry run

The dry run **MUST**:

- run the same mapping and checks as a real deploy,
- read the target only with read-only commands,
- change nothing,
- show the command plan in Technical View,
- show rule results,
- produce a diagnostics file.

Rules:

- The first deploy **MUST** require a successful dry run, unless the person explicitly skips it with a warning.
- A dry-run failure **MUST** explain what would fail and how to fix it.
- The dry run **MUST** be safe against production.

### 9.11 Step 9 — Save

Saving **MUST**:

- store the project source, target, method and options,
- store the rule snapshot and waivers in force,
- store the exact commit for the first deploy,
- enable **Deploy now**.

Rules:

- Saving **MUST NOT** deploy.
- Saving **MUST** be undoable.
- Re-running the wizard **MUST** show the current state and only ask about drift.

### 9.12 Worked example — Nusrat

1. **Source** — connects GitHub, chooses `acme/shop`, branch `main`, root `""`.
2. **Target** — chooses `shop-prod`, environment `production`.
3. **Detect** — sees Next.js in `web/`, .NET in `api/`, MariaDB in `deploy/docker-compose.yml`.
4. **Method** — “Put it live on this server” is recommended; she accepts.
5. **Options** — Caddy for TLS (recommended), existing MariaDB (detected), ShipRight backups (recommended), secrets in a server env file (recommended), Uptime Kuma skipped for now.
6. **Rules** — one advisory: “no healthcheck on the frontend”. She fixes it from the suggested diff.
7. **Review** — sees two file changes, approves both.
8. **Dry run** — passes; 16 commands, no changes.
9. **Save** — project is ready. She presses **Deploy now**.
10. **Live** — the app is at `https://shop.example.com`; rollback is available.

### 9.13 Pitfalls

- **Do not** ask a novice to choose a reverse proxy cold. Ask about outcomes.
- **Do not** ask more than five blocking questions for a common stack.
- **Do not** hide a blocking rule behind “advanced”.
- **Do not** write files before the review step.
- **Do not** make the dry run optional by default.
- **Do not** forget the “use recommended for all” path.
- **Do not** lose a draft when the app closes.
- **Do not** treat a detected fact as certain when confidence is low.

---

## 10. Rules engine

### 10.1 What a rule is

A rule is a typed, checkable policy:

```text
RuleDefinition
  id, version, title
  statement (plain language)
  rationale (why it matters)
  category: security | resilience | operability | cost | compliance
  defaultSeverity: blocking | advisory
  locked: true | false
  appliesTo: stack/service/environment predicates
  checks: static | live | plan | runtime
  remediation: what to change
  profiles: Baseline | Resilience | HardenedSecurity
```

A rule **MUST** have at least one check or be explicitly marked `advisory-guidance`. A rule without a check is not allowed to block a deploy.

### 10.2 Scopes

```text
Tenant  (billing/organisation)
  Company
    Project
      Environment (production, staging, development)
```

Personal preferences are a separate “My defaults” layer. They **MUST NOT** enforce anything on a shared company’s projects.

Precedence:

1. Built-in profile defaults.
2. Tenant settings.
3. Company settings.
4. Project settings.
5. Environment settings.

### 10.3 Monotonic tightening

- A narrower scope **MAY** add a rule.
- A narrower scope **MAY** make a rule stricter.
- A narrower scope **MUST NOT** make a locked rule weaker or switch it off.
- Weakening requires a waiver at the scope that owns the rule or higher.

Example:

- Tenant: “No public database ports” — locked.
- Company: adds “Images must be pinned by digest.”
- Project: adds “Healthcheck required.”
- Project **cannot** switch off the tenant rule.

### 10.4 Statuses and gate

A rule result is one of:

- `pass`
- `fail`
- `not-applicable`
- `unknown`
- `waived`

Gate rules:

- A blocking `fail` **MUST** stop the deploy.
- A blocking `unknown` **MUST** stop the deploy for critical security rules; otherwise it is shown as a warning.
- An advisory `fail` **MUST NOT** stop the deploy.
- A `waived` rule **MUST** show the waiver reason and expiry in the deploy plan.

### 10.5 Waivers

```text
RuleWaiver
  ruleId, projectId, environment
  reason
  compensatingControl
  approvedBy
  createdAt
  expiresAt (maximum 90 days)
```

Rules:

- A waiver **MUST** have a reason and an expiry.
- A waiver **MUST** be approved by the owner of the scope where the rule is defined, or higher.
- An expired waiver **MUST** become a failure again.
- A waiver **MUST** be visible in the project, the deploy plan and the audit log.
- A waiver **MUST NOT** be used to bypass a locked rule without a compensating control.

### 10.6 Built-in profiles

**Baseline** (all environments)
- healthcheck for long-running services
- restart policy
- no `latest` tags
- persistent data uses named volumes
- backups configured
- rollback path exists

**Resilience** (staging and production)
- resource limits and log rotation
- dependency health gating
- graceful shutdown / stop grace period
- minimum free disk headroom
- backup before schema migration
- previous release retained

**Hardened Security** (production, locked subset)
- no public database/cache ports
- non-root user
- read-only root filesystem where possible
- drop capabilities; add only what is needed
- no privileged containers, host networking or Docker socket mounts
- secrets never in compose, Dockerfiles, git or build args
- TLS only
- least-privilege database user
- pinned images/digests
- network segmentation
- `.dockerignore` excludes `.env`, `.git` and keys

### 10.7 Check catalog

Rules **MUST** use a curated catalog, not arbitrary shell:

| Check | Example |
|---|---|
| `compose.noPublishedPort` | service `db` must not publish 3306 |
| `compose.hasHealthcheck` | service must define `healthcheck` |
| `compose.restartPolicy` | service must set `unless-stopped` |
| `compose.imagePinned` | image must not end in `:latest` |
| `compose.noPrivileged` | `privileged: true` is forbidden |
| `compose.noDockerSocket` | `/var/run/docker.sock` is forbidden |
| `compose.envNoLiteralSecret` | `PASSWORD=abc` is forbidden |
| `dockerfile.nonRoot` | final stage must `USER` a non-root user |
| `dockerfile.basePinned` | `FROM` must use a digest |
| `server.portNotPublic` | host port must bind to localhost |
| `server.tlsValid` | certificate must be valid and auto-renewing |
| `backup.recent` | latest backup must be under N hours |
| `runtime.healthy` | container must be healthy |
| `finding.noCritical` | no critical/KEV finding in production |

Rules:

- A custom rule **MUST** choose from this catalog or a documented extension.
- A custom rule **MUST NOT** run arbitrary shell on a server.
- A custom rule **MUST** be previewed against sample configs before activation.

### 10.8 Evaluation points

- discovery
- AI proposal
- config validation
- dry run
- deploy gate
- post-deploy
- daily security monitor

The same rule **MUST** produce the same result at each point for the same evidence.

### 10.9 RBAC

- Tenant rule: tenant owner.
- Company rule: company admin.
- Project rule: project owner.
- Waiver: owner of the rule’s scope or higher.
- Security reviewer: may approve VEX/“not affected” findings and view everything.

### 10.10 Conflicts

- Duplicate or contradictory rules **MUST** be detected before saving.
- A broader locked rule wins.
- Otherwise the more specific scope wins.
- A conflict that cannot be resolved **MUST** be shown to the person, not guessed.
- A rule change **MUST** show an impact preview: how many projects would fail.

### 10.11 Audit

Every rule change, waiver, evaluation and override **MUST** record:

- actor
- timestamp
- rule ID and version
- scope
- before/after
- reason
- project/deployment

### 10.12 Pitfalls

- **Do not** let a project disable a company security rule.
- **Do not** treat an unenforceable rule as blocking.
- **Do not** create a “security vs convenience” slider.
- **Do not** let waivers live forever.
- **Do not** evaluate rules only in the UI; the API and deploy gate must enforce them.
- **Do not** send every rule to the AI. Send only applicable rules.

---

## 11. Option catalog

### 11.1 What an option is

An option is a reviewed, versioned choice in a decision point:

```text
Option
  id, version, category
  title, plainSummary, howItWorks
  appliesWhen
  resources: ramMb {min,max}, cpu, diskMb, startupSeconds
  operationalLoad: low | medium | high
  securityPosture
  cost: free | paid | usage-based + notes
  complexity: low | medium | high
  lockIn: none | low | medium | high
  requirements
  pros[], cons[]
  alternatives[]
  migration
  rollback
  evidence[]
  recipe
```

The AI **MUST** select from this catalog. It **MUST NOT** invent an option or a version. An uncatalogued suggestion is a research note, not an actionable choice.

### 11.2 Decision points in v1

| Decision | Options | Default |
|---|---|---|
| TLS / reverse proxy | Caddy, existing nginx + certbot, Traefik, Cloudflare Tunnel, managed LB | Caddy if nothing exists; integrate if a proxy exists |
| Database | existing container, new self-hosted, managed database | existing if detected; self-hosted MariaDB/Postgres otherwise |
| Backups | ShipRight native, restic/borg to object storage, provider snapshot | ShipRight native |
| Secrets | server env file, Docker secrets, external secret manager | server env file for v1, with a warning |
| Monitoring | none, Uptime Kuma, hosted monitor | Uptime Kuma for production, none for development |
| Deployment method | build-on-server, registry push, compose repo | build-on-server when available |

### 11.3 TLS example

| Option | Best when | RAM | Operational load | Watch out for |
|---|---|---|---|---|
| **Caddy** | no proxy exists; 1–few domains; want automatic HTTPS | ~20–50 MB | Low | less familiar to nginx teams |
| **nginx + certbot** | nginx already serves something | ~5–15 MB | Medium | manual `.conf` files and renewal |
| **Traefik** | many services, Docker labels, frequent changes | ~30–80 MB | Medium–High | harder to debug label/middleware errors |
| **Cloudflare Tunnel** | cannot open ports; want to hide origin | ~10–30 MB | Low–Medium | Cloudflare account, DNS, external dependency |
| **Managed LB / PaaS** | willing to pay to avoid ops | external | Low | cost, lock-in, data path |

Rules:

- If a reverse proxy is already running, **integrate over replace**.
- If nothing is running and the server is small, Caddy is the default.
- If the app is multi-domain or multi-service with dynamic routing, Traefik may be better.
- If ports cannot be opened, Cloudflare Tunnel may be the only practical option.
- Every option **MUST** show the estimated monthly cost if it is paid.

### 11.4 Resource budget

Before recommending, ShipRight **MUST** collect:

- total RAM, free RAM, swap
- CPU count and load
- free disk
- running containers and their usage
- existing ports and services
- number of domains/services

Then it **MUST** show:

```text
Target: 908 MB RAM, 2 GB swap, 12 GB free
Current steady state: ~420 MB
Proposed additions: ~75 MB
Estimated steady state: ~495 MB
Headroom: ~413 MB
```

Rules:

- Warn when estimated steady state exceeds **70%** of RAM.
- Block production when it exceeds **90%** unless a waiver exists.
- Show ranges, not false precision.
- Measure actual usage after deploy and record the difference.
- Never recommend Kubernetes, a service mesh or a full observability stack for a small host without an explicit expert choice.

### 11.5 AI ranking rules

The AI may:

- rank options for this target and project,
- explain the trade-offs,
- identify missing information,
- propose a migration path.

The AI may not:

- invent an option,
- change a rule,
- ignore the resource budget,
- hide a cost,
- choose an option that fails a locked rule.

### 11.6 Existing stack

- Detect existing nginx, Caddy, Traefik, Cloudflare, databases and monitoring.
- Prefer adding to what exists over replacing it.
- If replacement is recommended, show the migration and downtime.
- If a port conflict exists, show it before the user chooses.

### 11.7 Company-approved catalog

A company **MAY**:

- allow only specific options,
- require a specific option,
- forbid an option.

The wizard **MUST** respect this before showing choices.

### 11.8 Decision records

Every choice **MUST** be recorded:

- decision point
- options shown
- chosen option
- why
- estimated resources/cost
- model/prompt version if AI helped
- who approved
- timestamp

### 11.9 Pitfalls

- **Do not** offer a “cheapest” option that violates a security rule.
- **Do not** recommend a tool the user cannot maintain.
- **Do not** hide monthly cost.
- **Do not** replace a working proxy without a migration plan.
- **Do not** pretend an estimate is a measurement.
- **Do not** ask about products; ask about outcomes.

---

## 12. Security monitor

### 12.1 Goal

Keep every deployment as hardened as reasonably possible by watching the OS, runtime, framework, libraries, configuration and third-party services, and producing actionable findings.

### 12.2 Layers

| Layer | What is checked |
|---|---|
| Host OS | distro/EOL, packages, pending security updates, reboot-required, SSH/firewall, exposed ports |
| Container/base image | digest, base OS, packages, age, EOL, root/privileged/capabilities, published ports |
| Runtime/framework | Node/.NET/Python/Java/Go/Rust version and EOL, framework version |
| Application libraries | lockfiles, SBOM packages, ecosystem advisories |
| Third-party services | nginx/F5, Caddy, MariaDB, PostgreSQL, Redis, Traefik advisories and config |
| Configuration | TLS, headers, default credentials, admin exposure, Docker socket, privileged mode |
| Supply chain | image signature, SBOM attestation, provenance |
| Data protection | backup age, encryption, public backup exposure |

### 12.3 Inventory and SBOM

- Every built image **MUST** have an SBOM stored by digest.
- Every deployment **MUST** record the image digest actually running.
- A shared server inventory **MUST** avoid duplicating OS findings across projects.
- A deployment inventory **MUST** cover the app image and configuration.
- Inventory **MUST** be collected read-only.

### 12.4 Advisory sources

v1 **MUST** use:

- OSV.dev (language packages, ecosystems, batch)
- distro trackers (Ubuntu, Debian, Alpine, RHEL, Amazon)
- GitHub Advisory Database (GHSA)
- vendor advisories for nginx/F5, Caddy, MariaDB
- CISA KEV
- FIRST EPSS
- NVD as enrichment only

Later:

- PostgreSQL, Redis, Traefik, Docker/Moby, containerd/runc
- commercial feeds if the customer provides them
- VEX feeds

Rules:

- Never rely on NVD alone for container OS packages.
- Respect rate limits and cache.
- Support signed offline snapshots for air-gapped deployments.
- Record the source and retrieval time for every advisory.

### 12.5 Scanner

- v1 **MUST** wrap **Trivy** behind `IVulnerabilityScanner`.
- Grype **MAY** be added as an alternative.
- The scanner **MUST** accept an SBOM so daily checks do not require pulling images.
- The scanner **MUST NOT** run with host write access.

### 12.6 Daily job

```text
1. Resolve active deployments (company scope)
2. Refresh inventories only where changed
3. Refresh advisory data incrementally
4. Match packages/images to advisories
5. Prioritise (KEV, EPSS, exposure, fix, CVSS, EOL)
6. Diff against yesterday
7. Create/update findings and internal advisories
8. Evaluate security rules
9. Plan remediations
10. Send digest / urgent notifications
11. Audit and record metrics
```

Cadence:

- daily full refresh
- on deploy: scan the exact image
- on KEV or vendor critical: immediate check
- on demand: “Check now”
- weekly: lower-severity digest

### 12.7 Findings and prioritisation

A finding **MUST** include:

- advisory ID and source
- package/image and installed/fixed version
- service, image digest, host
- severity, CVSS, EPSS, KEV
- exposure
- evidence
- remediation
- status and waiver

Priority order:

1. KEV
2. EPSS
3. exposure
4. fix available
5. CVSS
6. EOL/age

### 12.8 Notifications

- Daily digest at 08:00 in the user’s time zone.
- Urgent alerts only for KEV, critical exposed findings, or a new fix for a critical finding.
- Diff-based: new, worsened, fixed, unchanged.
- One action per finding.
- Channels v1: in-app and email. Webhook/desktop later.
- Deduplicate across projects and servers.
- Quiet hours and escalation preferences.
- Technical View for raw IDs and evidence.

### 12.9 Automated remediation

**Safe to automate**
- inventory/SBOM refresh
- rescan stored SBOMs
- create/update findings
- open a ticket or PR
- rebuild an image with a patched base
- run a dry run
- renew TLS certificates
- notify

**Gated**
- update an image tag/digest
- deploy a patch to staging
- change config or ports
- apply host security updates
- restart a service

**Never automatic by default**
- production deploy
- major version upgrades
- database migrations
- destructive volume/config changes
- firewall changes that could lock people out
- secret rotation that can break running apps

Policy:

```text
RemediationPolicy
  scope
  autoApply: none | staging | patch-only
  requireApproval
  canary, health check, rollback
  allowed sources
```

Every automated action **MUST** have preconditions, a dry run, a health gate, a rollback and an audit record.

### 12.10 Fix SLAs

- Critical/KEV: 7 days
- High: 30 days
- Medium: 90 days
- Low: best effort

### 12.11 Worked example

```text
Security · shop-prod
  1 known exploited · 2 critical · 4 high

  nginx:1.27  CVE-2026-xxxx  known exploited  public
  Fix: upgrade to 1.30.7 or later        [Plan upgrade] [Waive]

  mariadb:10.5  no known exploitable findings
  caddy:2.8     1 high · not exposed
```

### 12.12 Pitfalls

- **Do not** show a raw CVE list as the primary experience.
- **Do not** alert on packages that are not in the running image.
- **Do not** scan the same OS once per deployment.
- **Do not** claim “safe” when a check could not run.
- **Do not** auto-deploy security fixes to production by default.
- **Do not** let the monitor run with write credentials.
- **Do not** ignore EOL software just because it has no new CVE.

---

## 13. Design language

The canonical document is **`docs/design/design-language.md`**. The short, enforceable rules are also in the repository memory file (`AGENTS.md`).

### 13.1 Principles

- One primary action per screen.
- Outcome first, mechanism second.
- Consequence before action.
- No dead ends.
- Calm surfaces, generous space, purposeful motion.
- Mobile-first for status and monitoring.
- Technical detail available, never required.

### 13.2 Tokens

- Type: Manrope for UI; JetBrains Mono only in Technical View.
- Type scale: 12.5 / 14 / 16 / 20 / 24 / 32; line height 1.4–1.5.
- Spacing: 4/8/12/16/20/24/32/48; page max-width 1120px.
- Radius: 10/12/16/980 (pills); two shadow levels.
- Colour: navy/gold brand; semantic green/amber/red; AA contrast; dark parity.
- Motion: 150–250 ms; spring only for success; honour `prefers-reduced-motion`.
- Touch targets: 44 px minimum.

### 13.3 Components

Use shared `App*` components only:

`AppPage`, `AppHeader`, `AppCard`, `AppSection`, `AppButton`, `AppTabs`, `AppStatusPill`, `AppInlineAlert`, `AppEmptyState`, `AppSheet`, `AppSplitButton`, `AppTechnicalDetails`, `AppSwitchRow`, `AppProviderCard`, `AppProgressTimeline`, `AppDiffView`, `AppDiagnosticsLink`.

### 13.4 Page recipes

- **Wizard**: one blocking question at a time; progress; recommended answer; skip.
- **Status**: one headline, one primary action, consequence summary.
- **Error**: what happened, why, next step, diagnostics, try again, rollback.
- **Empty**: explain the value, one action, no technical prerequisites.
- **Settings**: grouped, sentence case, inline validation, visible save state.

### 13.5 Technical View

- Per-user, persisted.
- A lens, not a permission.
- Reveals raw config, plans, logs, IDs, SHA, ports, volumes, env key names.
- Never reveals secrets.
- Does not remove cost, downtime, exposure or risk from the novice view.

### 13.6 Legacy

- Per-user; company admin may hide.
- Old routes remain reachable with a banner.
- Legacy never bypasses rules, health checks or audit.
- Removal 12 months after GA, announced in the banner and release notes.

### 13.7 Copy

- Sentence case, active voice.
- No jargon in the novice view.
- Say what the person gets, not what the system does.
- Never blame the user.
- Never say “invalid” without saying what to do.
- Units: “about 30 seconds”, “~40 MB”, “2 of 3 services”.

### 13.8 Accessibility

- WCAG 2.2 AA.
- Keyboard paths, visible focus, labels, reduced motion, dark parity.
- Errors announced to screen readers.
- No colour-only status.

### 13.9 Review checklist

- One primary action?
- Consequence summary?
- Error/empty/loading designed?
- Technical View available and secret-free?
- Mobile checked?
- Tokens only, no one-off styles?
- Legacy handled or explicitly out of scope?
- Accessibility checked?
- Copy plain and specific?

---

## 14. Data model

All records are company-scoped unless explicitly tenant-scoped. Every record has `id`, `createdAt`, `updatedAt`, `createdBy`.

### 14.1 Source and connections

| Record | Key fields |
|---|---|
| `ProjectSource` | `projectId`, `kind` (`local`/`git`), `localPath`, `gitSourceId` |
| `GitConnection` | `companyId`, `provider`, `baseUrl`, `accountName`, `credentialResourceId`, `scopes`, `status`, `lastCheckedAt` |
| `GitRepositoryRef` | `projectId`, `connectionId`, `providerRepoId`, `owner`, `name`, `branch`, `pathPrefix`, `role`, `pinnedCommit` |

### 14.2 Deployments

| Record | Key fields |
|---|---|
| `DeploymentMethod` | `projectId`, `methodId`, `settings`, `active` |
| `DeploymentPlan` | `projectId`, `methodId`, `commitSha`, `target`, `environment`, `services`, `downtimeEstimate`, `resourceImpact`, `costImpact`, `ruleResults`, `commands` |
| `Release` | `projectId`, `tag`, `sha`, `images`, `createdAt`, `current`, `previous` |
| `Deployment` | `projectId`, `releaseId`, `status`, `startedAt`, `completedAt`, `initiator`, `diagnosticsId` |

### 14.3 Rules and options

| Record | Key fields |
|---|---|
| `RuleDefinition` | `id`, `version`, `title`, `statement`, `rationale`, `category`, `defaultSeverity`, `locked`, `appliesTo`, `checks`, `remediation`, `profiles` |
| `RuleSetting` | `scopeType`, `scopeId`, `ruleId`, `enabled`, `severity`, `lockedBy` |
| `RuleWaiver` | `ruleId`, `projectId`, `environment`, `reason`, `compensatingControl`, `approvedBy`, `expiresAt` |
| `RuleEvaluation` | `projectId`, `deploymentId`, `ruleId`, `ruleVersion`, `status`, `evidence`, `checkedAt` |
| `Option` | `id`, `version`, `category`, `title`, `summary`, `appliesWhen`, `resources`, `cost`, `complexity`, `pros`, `cons`, `recipe` |
| `DecisionRecord` | `projectId`, `decisionId`, `optionsShown`, `chosen`, `why`, `resourceImpact`, `costImpact`, `aiTranscriptId`, `approvedBy`, `createdAt` |

### 14.4 Security

| Record | Key fields |
|---|---|
| `SecurityInventory` | `scope` (`server`/`deployment`), `scopeId`, `os`, `packages`, `images`, `ports`, `tls`, `collectedAt` |
| `Sbom` | `imageDigest`, `format`, `content`, `createdAt` |
| `Advisory` | `id`, `source`, `publishedAt`, `modifiedAt`, `severity`, `cvss`, `epss`, `kev`, `affected`, `fixed`, `references` |
| `SecurityFinding` | `projectId`, `deploymentId`, `advisoryId`, `package`, `installedVersion`, `fixedVersion`, `exposure`, `status`, `evidence`, `remediation`, `waiverId` |
| `Remediation` | `findingId`, `kind`, `plan`, `status`, `approvedBy`, `executedAt`, `verifiedAt` |
| `NotificationPreference` | `userId`, `channel`, `severity`, `quietHours`, `timeZone` |
| `Notification` | `userId`, `projectId`, `kind`, `severity`, `summary`, `sentAt`, `readAt` |

### 14.5 AI and audit

| Record | Key fields |
|---|---|
| `AiTranscript` | `id`, `runtimeId`, `model`, `promptVersion`, `redactedPrompt`, `redactedOutput`, `usage`, `cost`, `createdAt` |
| `AuditEvent` | `actor`, `action`, `scope`, `before`, `after`, `reason`, `at` |
| `ProviderCall` | `provider`, `operation`, `status`, `duration`, `rateLimitRemaining`, `at` |

### 14.6 Rules for the data model

- Secrets are never stored in general records. They live in encrypted credential resources.
- SBOMs and findings are company-scoped.
- A project move between companies re-evaluates rules and does not move provider tokens.
- Deleting a project deletes its findings, plans and transcripts, but keeps the audit trail.
- Every record that influences a deploy is versioned or immutable.

---

## 15. Non-functional requirements

### 15.1 Security

- All provider tokens encrypted at rest and never returned by an API.
- All AI prompts/outputs redacted before persistence or display.
- SSRF protection for repository URLs: allow-listed providers or configured enterprise hosts; block private IPs for generic fetches.
- Webhooks: signature verification, replay protection, size limits.
- No model gets SSH keys, provider tokens, `.env` values or secrets.
- Tool-enabled AI runtimes run in a working-directory jail with an explicit allow-list.
- Automated actions require preconditions, a dry run, a health gate, a rollback and an audit record.
- The security monitor uses read-only credentials.

### 15.2 Privacy

- AI is opt-in per company.
- No secrets or full source trees are sent to a model by default.
- Evidence is minimised and redacted.
- Source archives are ephemeral and deleted after use.
- Customers can choose a local-only model.

### 15.3 Performance

- List/detail API reads: p95 under 500 ms at normal load.
- Dashboard first paint: LCP under 2.5 s on a mid-range phone.
- Dry run: under 60 s for a typical two-service project, excluding network waits.
- Daily security check: one job per server/deployment, bounded and resumable.
- No page blocks on a third-party provider; show a skeleton and a retry.

### 15.4 Availability and offline

- Cloud mode: always-on scheduler for daily checks.
- Desktop mode: checks on launch and every 24 h while running; optional background service later.
- Provider outage: show “temporarily unavailable”, retry with backoff, do not lose the draft.
- Air-gapped: signed advisory snapshots and local scanning.

### 15.5 Multi-company

- Every query is scoped to the active company.
- Provider connections, projects, findings, notifications and audit never cross companies.
- A missing active membership fails closed.
- A project move re-evaluates company rules and requires re-authorisation if the connection does not belong to the destination company.

### 15.6 Audit and retention

- Audit: 24 months.
- Findings: 12 months.
- SBOM: life of the image + 90 days.
- AI transcripts: 90 days by default, configurable; never contain secrets.
- Deployment logs: existing retention policy.

### 15.7 Accessibility

- WCAG 2.2 AA.
- Keyboard-only operation for every action.
- Visible focus.
- Screen-reader labels for status, errors and switches.
- Reduced-motion support.
- Dark/light parity.

### 15.8 Observability

- Structured logs for provider calls, AI calls, rule evaluations, security checks and automated actions.
- Metrics: time to first deploy, dry-run pass rate, question count, option acceptance, findings by severity, time to fix, auto-remediation success.
- Alerts for: provider auth failures, scheduler failures, a failed daily check, a failed automated action.

---

## 16. Acceptance criteria

Each criterion is written as Given/When/Then and **MUST** be covered by an automated test where practical.

1. **Repository project without a local folder**
   Given a company with a GitHub connection, when a project is created from a repository URL and branch, then the project has a `GitRepository` source, no local path is required, and the dashboard shows the repository and commit.

2. **Monorepo path prefix**
   Given a repository with two compose files, when the person chooses one, then only that root is used and all Dockerfile/context paths resolve inside it.

3. **Private repository access**
   Given an invalid or expired token, when the connection is tested, then the wizard says “reconnect required” and does not fall back to another company’s connection.

4. **Deterministic wizard without AI**
   Given AI is switched off, when the person completes the wizard, then they can reach a successful dry run and save a deployment.

5. **AI proposal validation**
   Given the AI returns an option ID that is not in the catalog, when the proposal is judged, then the option is rejected and the wizard shows the catalog choices.

6. **Rule lock**
   Given a tenant rule marked locked, when a project tries to switch it off, then the API rejects the change and the UI shows “Required by your tenant”.

7. **Waiver expiry**
   Given a waiver that expired yesterday, when rules are evaluated, then the rule fails again and the deploy is blocked if it is blocking.

8. **Option trade-offs**
   Given a small server and no existing proxy, when the TLS decision is shown, then Caddy is recommended, its RAM range is shown, and replacing an existing proxy is not the default.

9. **Dry run changes nothing**
   Given a production target, when a dry run runs, then only read-only commands are sent, no files/containers are changed, and a diagnostics file is produced.

10. **Deployment screen primary action**
    Given an unconfigured project, when the deployment screen opens, then the primary action is “Set up deployment”; given a ready project, the primary action is “Deploy now”.

11. **Technical View secrecy**
    Given a deployment with a provider token and env secrets, when Technical View is on, then no token or secret value is visible.

12. **Legacy compatibility**
    Given a bookmarked classic deploy URL, when Legacy is off, then the page still loads with a banner and rules/health checks are still enforced.

13. **Daily security check**
    Given a running image with a package affected by a KEV advisory, when the daily check runs, then a critical finding is created within one cycle and an urgent notification is sent.

14. **Notification digest**
    Given three new findings and two unchanged, when the daily digest is sent, then it summarises only the new/changed items and links to the project.

15. **No automatic production deploy**
    Given the default remediation policy, when a critical fix is found, then ShipRight may rebuild and open a PR but does not deploy to production.

16. **Multi-company isolation**
    Given two companies with repositories of the same name, when a user in company A lists connections, then company B’s connection and findings are not visible.

17. **Host OS dedupe**
    Given three projects on one server, when the daily check runs, then the OS inventory is collected once and findings are linked to all three deployments without duplicate alerts.

18. **Repository rename**
    Given a repository renamed on the provider, when the next check runs, then the project still resolves by provider repo ID and does not create a duplicate.

19. **Offline advisory snapshot**
    Given no internet access, when the security check runs with a signed snapshot, then findings are produced from the snapshot and the UI says the feed is offline.

20. **Accessibility**
    Given a keyboard-only user, when they complete the wizard, then every step, error and switch is reachable and announced.

---

## 17. Testing strategy

- **TDD** for every change: failing test first, then production code, then the full suite.
- **Unit tests** for rules, option ranking, path prefix, URL parsing, redaction, budget calculations.
- **Contract tests** for every git provider and AI runtime.
- **Fixture tests** for advisory matching, distro backports, VEX and false positives.
- **Scripted AI** for deterministic wizard and advisor tests.
- **Golden tests** for prompt/schema versions and decision records.
- **Integration tests** with a fake GitHub/Azure DevOps provider and a fake target server.
- **End-to-end tests** on 2–3 sample repositories: Next.js + .NET + MariaDB, static site + API, Python service.
- **Security tests**: secret leakage, SSRF, token redaction, path traversal, rule bypass, waiver expiry.
- **Visual regression** and **accessibility** tests for the new screens.
- **Performance tests** for dry run, daily check and large monorepos.
- **Hakiri conformance suite**: every runtime adapter passes the same contract tests.

---

## 18. Phased delivery

### Deploy targets (applies to every phase)

ShipRight's own production server (`https://shipright.jaribio.biz`, currently 4.12.3) stays on the legacy/manual
deploy path until the new path has been proven end to end.

- All self-deploy experiments — building and deploying ShipRight with ShipRight — run against a **separate staging
  box**, provided for that purpose, with its own domain, database and volumes.
- The production server is never the canary for the new deploy path.
- Even on the staging box, keep an out-of-band recovery (SSH + `docker compose` + the old installer) available and
  rehearsed before the first self-deploy.
- Promotion to the production server is a separate, explicit decision made only after the staging box has survived a
  full deploy-and-rollback cycle.

### D1 — Dark spine (built, not deployed)

The additive foundation every later wave sits on:

- `ProjectSource` + `ISourceTree` (local folders now; repository metadata for the provider work).
- `IDeploymentMethod` + registry (`jit`, `legacy`), where the JIT adapter calls the same mapper the deploy uses.
- `IRuleEngine` + `RuleEngine` with scopes, monotonic tightening, locks, waivers and a small check catalog.
- `IFeatureFlagStore` with global → tenant → company → project → user precedence; everything defaults off.
- `IDeploymentAdvisor` + a deterministic advisor + a flag-gated shadow advisor that records proposals and never writes.
- The bootstrap plan carries rule results, so the spine is exercised by a real screen.
- A read-only `GET /api/platform/status`.

Still owed before D1 is fully closed: persist flags and proposals, wire the AI advisor, and add the rules screen.

### D2 — Onboarding slice (in progress)

- **GitHub repository inspection**: read-only metadata, default branch, branches, compose files and Dockerfiles, without cloning. A token is optional for private repositories.
- The bootstrap screen can fill the branch and compose file from a repository address.
- Still to do: attach the inspected repository to a project, the full deployment screen, the guided wizard and the option catalog.

### Phase 0 — Hakiri
Extract the AI layer into `D:\work\nyingi\code\systems\hakiri`, publish packages, add conformance tests.
**Exit:** ShipRight can reference Hakiri; DevTeam has a migration plan; no duplicated adapter.

### Phase 1 — Source abstraction and GitHub
`ProjectSource`, `GitConnection`, `RepoRef`, GitHub read-only provider, URL parsing, monorepo path prefix.
**Exit:** a project can be created from a GitHub URL with no local folder and pass a dry run using provider metadata.

### Phase 2 — Deterministic wizard
Knowns, detection, method cards, review, dry run, save. AI off.
**Exit:** a novice can complete the wizard and reach a successful dry run without AI.

### Phase 3 — Rules and options
Rule engine, profiles, locks, waivers, check catalog, option catalog, resource budget.
**Exit:** a locked rule cannot be weakened; options show trade-offs; the deploy gate enforces rules.

### Phase 4 — New deployment screen and design language
New status/method/review screens, Technical View, Legacy switch, design tokens/components.
**Exit:** the new screen is the default; classic routes remain reachable; accessibility and visual tests pass.

### Phase 5 — Security monitor MVP
Inventory, SBOM, Trivy, OSV/distro/GitHub/vendor/KEV/EPSS, daily job, findings, project Security tab.
**Exit:** a real image with a known vulnerable package produces a prioritised finding and a fix.

### Phase 6 — Notifications and safe automation
Email/in-app digest, urgent alerts, PR/rebuild/dry-run automation, staging auto-deploy policy.
**Exit:** a critical finding notifies the right person and can be fixed with one approved action.

### Phase 7 — Azure DevOps, webhooks and worker builds
Azure DevOps provider, OAuth/App auth, webhooks, registry builds on a worker.
**Exit:** the second provider and the second build path meet the same acceptance criteria.

---

## 19. Consolidated pitfalls

**Product**
- Building a universal deployment abstraction that hides stack-specific truth.
- Letting the AI become the source of truth.
- Hiding consequences in the name of simplicity.
- Two parallel UIs forever.
- A “security vs convenience” slider.

**Source and providers**
- Assuming a repo URL is the project root.
- Ignoring monorepos.
- Storing tokens in project fields.
- Cross-company connection reuse.
- Trusting a URL with embedded credentials.
- Cloning entire repositories to answer metadata questions.
- Treating a provider rename as a new repo.

**Rules and options**
- Toggleable critical security rules.
- Unenforceable blocking rules.
- Permanent waivers.
- Option catalogs invented by the model.
- Recommending heavy stacks to small servers.
- Ignoring the existing reverse proxy/database.

**AI**
- Sending secrets or full source to a model.
- Letting the model write files or run commands without approval.
- Trusting model JSON.
- One global prompt for every consumer.
- Coupling the shared layer to ShipRight/DevTeam.

**Security monitoring**
- A raw CVE list as the UX.
- Duplicate OS findings per project.
- NVD-only matching for distro packages.
- Alert fatigue.
- Auto-deploying security fixes to production by default.
- A monitor with write credentials.

**Design**
- Technical View as a second app.
- Legacy as a permanent home.
- One-off components and styles.
- Colour-only status.
- Desktop-only monitoring.

---

## 20. Appendices

### A. Source list

- OSV.dev API — https://google.github.io/osv.dev/api
- GitHub Advisory Database — https://github.com/advisories and GraphQL `securityAdvisories`
- NVD API — https://nvd.nist.gov/developers
- CISA KEV — https://www.cisa.gov/known-exploited-vulnerabilities-catalog
- FIRST EPSS — https://api.first.org/data/v1/epss
- nginx security advisories — https://nginx.org/en/security_advisories.html
- Caddy advisories — https://github.com/caddyserver/caddy/security/advisories
- MariaDB security — https://mariadb.org/security/
- PostgreSQL security — https://www.postgresql.org/support/security/
- Trivy — https://trivy.dev
- Grype/Syft — https://github.com/anchore/grype and https://github.com/anchore/syft
- CycloneDX — https://cyclonedx.org
- SPDX — https://spdx.dev
- OpenVEX — https://openssf.org/projects/vex/

### B. Example rule definitions

```json
{
  "id": "SEC-DB-PUBLIC-PORT",
  "version": 1,
  "title": "Databases must not be reachable from the internet",
  "statement": "A database service must not publish a port on a public interface.",
  "rationale": "A public database is scanned and attacked continuously.",
  "category": "security",
  "defaultSeverity": "blocking",
  "locked": true,
  "appliesTo": { "serviceKind": "database", "environment": "production" },
  "checks": [{ "kind": "compose.noPublishedPort", "ports": [3306, 5432, 6379, 1433] }],
  "remediation": "Remove the published port and let the app reach the database over the compose network."
}
```

```json
{
  "id": "SEC-NO-CRITICAL-FINDINGS",
  "version": 1,
  "title": "No critical or known-exploited findings in production",
  "statement": "A production deployment must not run an image with a critical or KEV finding.",
  "rationale": "Known-exploited and critical findings are the highest-risk issues.",
  "category": "security",
  "defaultSeverity": "blocking",
  "locked": true,
  "appliesTo": { "environment": "production" },
  "checks": [{ "kind": "finding.noCritical", "kev": true }],
  "remediation": "Upgrade the affected package or base image, rebuild and redeploy."
}
```

### C. Example option catalog entry

```json
{
  "id": "tls-caddy",
  "version": 1,
  "category": "tls",
  "title": "Caddy",
  "plainSummary": "A small web server that gets and renews HTTPS certificates automatically.",
  "howItWorks": "Caddy listens on 80/443, obtains a certificate for your domain, and proxies requests to your app.",
  "appliesWhen": { "noExistingProxy": true },
  "resources": { "ramMb": { "min": 20, "max": 50 }, "diskMb": 25, "containers": 1 },
  "operationalLoad": "low",
  "securityPosture": "Terminates TLS; keep app ports private.",
  "cost": { "kind": "free" },
  "complexity": "low",
  "lockIn": "none",
  "requirements": ["A domain pointing at the server", "Ports 80 and 443 open"],
  "pros": ["automatic HTTPS", "small config", "one container"],
  "cons": ["new tool for nginx teams", "less flexible for very complex routing"],
  "alternatives": ["nginx + certbot", "Traefik", "Cloudflare Tunnel"],
  "migration": "Point DNS at the same host; keep the old proxy stopped until the new one is healthy.",
  "rollback": "Stop Caddy and restore the previous proxy configuration.",
  "evidence": ["No reverse proxy detected on the target"]
}
```

### D. Example AI task

```json
{
  "systemPrompt": "You are a deployment advisor. Propose only options whose ids are in the catalog. Explain trade-offs. Never invent a tool.",
  "messages": [{ "role": "user", "content": "Choose a TLS option for a 908 MB server with no existing proxy." }],
  "evidence": [
    { "label": "server", "trust": "systemFact", "content": "RAM 908 MB, free 480 MB; no proxy running" }
  ],
  "responseFormat": { "kind": "json", "jsonSchema": "{ ... }" },
  "policy": { "allowTools": false, "allowFileEdits": false, "allowCommands": false }
}
```

### E. Document control

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-11-27 | First complete draft; resolves all design questions; places the AI layer in `hakiri` |

**Reviewers required before implementation:** product, design, backend, frontend, security, operations.

**Definition of done for this BRS:** every MUST has an owner, an acceptance test and a release phase. Any deviation is recorded as a decision with a reason.

