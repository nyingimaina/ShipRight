/** Types and pure helpers for "build on the server" deploys. They mirror the server's rules so mistakes show up instantly. */

export interface JitDependentChoice {
  service: string;
  dependent: string;
  restart: boolean;
}

/** A service to build, named in the settings (taken from the compose file) when the project has none of its own. */
export interface JitConfiguredService {
  name: string;
  imageRef: string;
  tagVariable: string;
  /** Paths inside the repository. */
  dockerfile?: string | null;
  context?: string | null;
  target?: string | null;
  args?: Record<string, string> | null;
}

export interface JitConfig {
  enabled: boolean;
  repoUrl: string;
  branch: string;
  composeProject: string;
  envFile: string;
  releasesToKeep: number;
  stopGraceSeconds: number;
  healthTimeoutSeconds: number;
  settleSeconds: number;
  minFreeDiskMb: number;
  buildNice: number | null;
  /** Read the repository with a key made on the server (never a password or token). */
  useDeployKey: boolean;
  /** A saved access token (Resources, then Credentials) for reading a private https repository. Never part of the address. */
  credentialResourceId: string | null;
  /** The compose file inside the repository. Empty = the usual file in its top folder. */
  composeFile: string;
  composeServices: JitConfiguredService[];
  dependentChoices: JitDependentChoice[];
}

export const DEFAULT_JIT_CONFIG: JitConfig = {
  enabled: false,
  repoUrl: '',
  branch: 'master',
  composeProject: '',
  envFile: '',
  releasesToKeep: 3,
  stopGraceSeconds: 30,
  healthTimeoutSeconds: 60,
  settleSeconds: 10,
  minFreeDiskMb: 3000,
  buildNice: null,
  useDeployKey: false,
  credentialResourceId: null,
  composeFile: '',
  composeServices: [],
  dependentChoices: [],
};

export interface JitServiceHint {
  name: string;
  tagVariable: string;
  imageRef: string;
  composeImageLine: string;
}

export interface JitConfigResponse {
  config: JitConfig;
  services: JitServiceHint[];
  problems: string[];
}

export interface JitRelease {
  tag: string;
  sha: string;
  createdUtc: string;
  message?: string | null;
  isCurrent: boolean;
  isPrevious: boolean;
}

export interface JitReleases {
  phase: string;
  currentTag: string | null;
  previousTag: string | null;
  needsAttention: boolean;
  attention: string | null;
  releases: JitRelease[];
  error: string | null;
}

export interface JitDependents {
  pairs: JitDependentChoice[];
  note: string | null;
}

// ── validation ───────────────────────────────────────────────────────────────

const BRANCH = /^[A-Za-z0-9][A-Za-z0-9/_.-]*$/;
const PROJECT = /^[a-z0-9][a-z0-9_-]*$/;

function inRange(v: number, min: number, max: number): boolean {
  return Number.isInteger(v) && v >= min && v <= max;
}

/** Field name -> message. Empty when everything is fine. Required fields only matter once the mode is switched on. */
/** Mirrors the server: a password or token inside an address is never wanted (https with a user part, ssh with a password). */
export const addressHasSecret = (url: string): boolean =>
  /^(https?:\/\/[^/\s@]+@|ssh:\/\/[^/\s@]+:[^/\s@]*@)/i.test(url.trim());

export const SECRET_IN_ADDRESS =
  'This address contains a password or token. ShipRight does not keep secrets inside an address. Use the SSH form (git@host:owner/repo.git) with a deploy key.';

export function validateJitConfig(c: JitConfig): Record<string, string> {
  const e: Record<string, string> = {};

  if (!c.repoUrl.trim()) {
    if (c.enabled) e.repoUrl = 'Enter the git address the server should fetch your code from.';
  } else if (addressHasSecret(c.repoUrl)) {
    e.repoUrl = SECRET_IN_ADDRESS;
  } else if (c.repoUrl.startsWith('-') || /[\s\u0000-\u001f]/.test(c.repoUrl) || c.repoUrl.includes('::')) {
    e.repoUrl = 'That git address does not look right. Paste the address you use to clone your repository, with no spaces.';
  }

  if (!c.branch.trim()) {
    if (c.enabled) e.branch = 'Enter the branch the server should deploy (for example master).';
  } else if (!BRANCH.test(c.branch) || c.branch.includes('..') || c.branch.includes('//') || c.branch.endsWith('/') || c.branch.endsWith('.lock')) {
    e.branch = 'That branch name is not valid. Use letters, digits and / . _ - only.';
  }

  const file = c.composeFile.trim();
  if (file && (!/^[A-Za-z0-9._][A-Za-z0-9._/-]*\.(yml|yaml)$/.test(file) || file.split('/').some(p => p === '' || p === '..')))
    e.composeFile = 'Give a path inside your repository ending in .yml or .yaml, for example deploy/docker-compose.yml (or leave it empty).';

  const safePath = (p: string) => /^[A-Za-z0-9._][A-Za-z0-9._/-]*$/.test(p) && !p.split('/').some(s => s === '' || s === '..');
  for (const s of c.composeServices) {
    const df = (s.dockerfile ?? '').trim();
    const ctx = (s.context ?? '').trim() || '.';
    if (!df || !safePath(df) || (ctx !== '.' && !safePath(ctx))) {
      e.composeServices = df
        ? `Pick the Dockerfile for '${s.name}' again: its path is not usable. Use a file inside your repository, without spaces.`
        : `Pick the Dockerfile for '${s.name}'.`;
      break;
    }
  }

  if (c.credentialResourceId && c.useDeployKey) e.credentialResourceId = 'Choose either a deploy key or a saved credential, not both.';
  else if (c.credentialResourceId && c.repoUrl.trim() && !/^https:\/\//i.test(c.repoUrl.trim()))
    e.credentialResourceId = 'A saved credential only works with an https address. Use the SSH form with a deploy key instead.';

  if (c.composeProject.trim() && !PROJECT.test(c.composeProject))
    e.composeProject = 'Use lower-case letters, digits, _ and - only (or leave it empty).';

  if (c.envFile.trim() && (!c.envFile.startsWith('/') || c.envFile.split('/').includes('..') || /[\u0000-\u001f]/.test(c.envFile)))
    e.envFile = 'Give the full path on the server, starting with / (or leave it empty).';

  if (!inRange(c.releasesToKeep, 0, 100)) e.releasesToKeep = 'Enter a whole number from 0 to 100.';
  if (!inRange(c.stopGraceSeconds, 0, 600)) e.stopGraceSeconds = 'Enter a whole number of seconds from 0 to 600.';
  if (!inRange(c.healthTimeoutSeconds, 5, 3600)) e.healthTimeoutSeconds = 'Enter a whole number of seconds from 5 to 3600.';
  if (!inRange(c.settleSeconds, 0, 600)) e.settleSeconds = 'Enter a whole number of seconds from 0 to 600.';
  if (!inRange(c.minFreeDiskMb, 100, 1_000_000)) e.minFreeDiskMb = 'Enter a whole number of megabytes, at least 100.';
  if (c.buildNice !== null && !inRange(c.buildNice, 0, 19)) e.buildNice = 'Enter a whole number from 0 to 19, or leave it empty.';

  return e;
}

// ── small helpers ────────────────────────────────────────────────────────────

/** Records one answer to "also restart this one?" without mutating the list. */
export function setDependentChoice(
  choices: JitDependentChoice[], service: string, dependent: string, restart: boolean,
): JitDependentChoice[] {
  return [...choices.filter(c => !(c.service === service && c.dependent === dependent)), { service, dependent, restart }];
}

export function describeAge(iso: string, now: Date = new Date()): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '';
  const seconds = Math.floor((now.getTime() - then) / 1000);
  if (seconds < 60) return 'just now';
  const plural = (n: number, unit: string) => `${n} ${unit}${n === 1 ? '' : 's'} ago`;
  if (seconds < 3600) return plural(Math.floor(seconds / 60), 'minute');
  if (seconds < 86400) return plural(Math.floor(seconds / 3600), 'hour');
  return plural(Math.floor(seconds / 86400), 'day');
}

export function shortSha(sha?: string | null): string {
  return (sha ?? '').slice(0, 7);
}

export type Tone = 'ok' | 'warn' | 'bad';

export function phaseSummary(phase: string): { label: string; tone: Tone } {
  switch (phase) {
    case 'Idle': return { label: 'Ready', tone: 'ok' };
    case 'Syncing':
    case 'Building':
    case 'FailedBuild': return { label: 'A deploy was cut off before it changed anything', tone: 'warn' };
    case 'Verified':
    case 'RolledBack': return { label: 'Finishing up an earlier deploy', tone: 'warn' };
    case 'OldStopped':
    case 'NewStarted':
    case 'RollingBack': return { label: 'A deploy was interrupted half-way', tone: 'bad' };
    case 'RollbackFailed': return { label: 'Needs attention', tone: 'bad' };
    default: return { label: 'Unknown', tone: 'warn' };
  }
}
