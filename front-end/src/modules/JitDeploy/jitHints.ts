import { addressHasSecret, DEFAULT_JIT_CONFIG, JitConfig, SECRET_IN_ADDRESS } from './jitDeploy';

/** What ShipRight already knows about a project, offered instead of asking the person to type it. */

export interface JitRepoHint {
  id: string;
  label: string;
  url: string;
  ssh: string | null;
  https: string | null;
  source: string;
  branch: string | null;
  hadCredentials: boolean;
}

export interface JitHintsResponse {
  repos: JitRepoHint[];
  envFile: string | null;
  branch: string | null;
  composeProject: string;
  notes: string[];
}

export interface JitStackInfo {
  name: string;
  status: string;
  configFiles: string[];
}

export interface JitStacksResponse {
  stacks: JitStackInfo[];
  projectName: string;
  match: 'none' | 'same' | 'different';
  warnings: string[];
  suggestedName: string | null;
  error: string | null;
}

export interface JitBranchesResponse {
  branches: string[];
  defaultBranch: string | null;
  error: string | null;
}

export interface AddressOption {
  value: string;
  label: string;
  branch: string | null;
}

export function addressOptions(hints: JitHintsResponse | null): AddressOption[] {
  if (!hints) return [];
  const seen = new Set<string>();
  const out: AddressOption[] = [];
  const add = (value: string | null, label: string, branch: string | null) => {
    if (!value || seen.has(value)) return;
    seen.add(value);
    out.push({ value, label, branch });
  };
  for (const r of hints.repos) {
    if (r.ssh && r.https && r.ssh !== r.https) {
      add(r.ssh, `${r.label}, SSH (needs a deploy key): ${r.ssh}`, r.branch);
      add(r.https, `${r.label}, HTTPS (needs a token): ${r.https}`, r.branch);
    } else {
      add(r.url, `${r.label}: ${r.url}`, r.branch);
    }
  }
  return out;
}

/** SSH when there is one: a deploy key keeps every secret off the server. */
export const preferredAddress = (r: JitRepoHint): string => r.ssh ?? r.url;

/**
 * The fields to fill in when the person first turns building on the server on. Only empty fields, only for a project that
 * was never set up, and never a guess when there are several repositories.
 */
export function prefillPatch(cfg: JitConfig, saved: JitConfig, hints: JitHintsResponse | null): Partial<JitConfig> {
  if (!hints || saved.repoUrl.trim() !== '') return {};
  const patch: Partial<JitConfig> = {};
  if (cfg.repoUrl.trim() === '' && hints.repos.length === 1) patch.repoUrl = preferredAddress(hints.repos[0]);
  if ((cfg.branch.trim() === '' || cfg.branch === DEFAULT_JIT_CONFIG.branch) && hints.branch) {
    if (hints.branch !== cfg.branch) patch.branch = hints.branch;
  }
  return patch;
}

/** Picking a known repository brings its branch along, but never replaces a branch the person chose. */
export function branchAfterPick(cfg: JitConfig, picked: AddressOption | undefined): Partial<JitConfig> {
  if (!picked?.branch) return {};
  return cfg.branch.trim() === '' || cfg.branch === DEFAULT_JIT_CONFIG.branch ? { branch: picked.branch } : {};
}

/** When the typed branch is not in the repository's list, offer the repository's real default instead. */
export function defaultBranchSuggestion(cfg: JitConfig, list: JitBranchesResponse | null): string | null {
  if (!list?.defaultBranch || list.branches.length === 0) return null;
  return list.branches.includes(cfg.branch) ? null : list.defaultBranch;
}

/** Mirrors the server: a password or token inside an https address is never wanted. The warning does not repeat it. */
export function addressWarning(url: string): string | null {
  return addressHasSecret(url) ? SECRET_IN_ADDRESS : null;
}
