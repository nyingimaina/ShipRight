import { DEFAULT_JIT_CONFIG, JitConfig } from '../jitDeploy';
import {
  addressOptions, addressWarning, branchAfterPick, defaultBranchSuggestion, JitHintsResponse, JitRepoHint, preferredAddress, prefillPatch,
} from '../jitHints';

const repo = (over: Partial<JitRepoHint> = {}): JitRepoHint => ({
  id: 'project-repo:github.com/o/shop', label: 'Source code (shop)', url: 'git@github.com:o/shop.git',
  ssh: 'git@github.com:o/shop.git', https: 'https://github.com/o/shop.git', source: 'project-repo', branch: 'main', hadCredentials: false, ...over,
});

const hints = (over: Partial<JitHintsResponse> = {}): JitHintsResponse => ({
  repos: [repo()], envFile: '/home/u/shop/.env', branch: 'main', composeProject: 'shop', notes: [], ...over,
});

const cfg = (over: Partial<JitConfig> = {}): JitConfig => ({ ...DEFAULT_JIT_CONFIG, ...over });

describe('addressOptions', () => {
  it('offers both ways of writing an address, SSH first, each saying what it needs', () => {
    const o = addressOptions(hints());
    expect(o.map(x => x.value)).toEqual(['git@github.com:o/shop.git', 'https://github.com/o/shop.git']);
    expect(o[0].label).toMatch(/SSH.*deploy key/i);
    expect(o[1].label).toMatch(/HTTPS.*token/i);
    expect(o[0].label).toContain('Source code (shop)');
    expect(o[0].branch).toBe('main');
  });

  it('offers a single option for an address with no other form', () => {
    const o = addressOptions(hints({ repos: [repo({ ssh: null, https: null, url: '/srv/git/x.git', label: 'Compose repository' })] }));
    expect(o).toHaveLength(1);
    expect(o[0].value).toBe('/srv/git/x.git');
  });

  it('never lists the same address twice', () => {
    const o = addressOptions(hints({ repos: [repo(), repo({ id: 'compose-repo:x', label: 'Compose repository' })] }));
    expect(new Set(o.map(x => x.value)).size).toBe(o.length);
  });

  it('is empty when nothing is known', () => {
    expect(addressOptions(hints({ repos: [] }))).toEqual([]);
    expect(addressOptions(null)).toEqual([]);
  });
});

describe('preferredAddress', () => {
  it('prefers SSH, since a deploy key keeps every secret off the server', () => {
    expect(preferredAddress(repo())).toBe('git@github.com:o/shop.git');
    expect(preferredAddress(repo({ ssh: null, url: 'https://git.corp:8443/o/r.git' }))).toBe('https://git.corp:8443/o/r.git');
  });
});

describe('prefillPatch', () => {
  it('fills a project that has never been set up: the address and the branch (never the env file: the hint holds the server\'s variables, not a path)', () => {
    expect(prefillPatch(cfg(), cfg(), hints())).toEqual({
      repoUrl: 'git@github.com:o/shop.git', branch: 'main',
    });
  });

  it('does not guess an address when there are several repositories', () => {
    const p = prefillPatch(cfg(), cfg(), hints({ repos: [repo(), repo({ id: 'b', url: 'git@github.com:o/web.git', ssh: 'git@github.com:o/web.git' })] }));
    expect(p.repoUrl).toBeUndefined();
    expect(p.branch).toBe('main');
  });

  it('never overwrites what the person already typed', () => {
    const typed = cfg({ repoUrl: 'git@x:y/z.git', branch: 'release', envFile: '/mine/.env' });
    expect(prefillPatch(typed, cfg(), hints())).toEqual({});
  });

  it('leaves an already configured project alone', () => {
    const saved = cfg({ enabled: true, repoUrl: 'git@x:y/z.git', branch: 'master' });
    expect(prefillPatch(cfg({ repoUrl: '' }), saved, hints())).toEqual({});
  });

  it('does nothing when nothing is known', () => {
    expect(prefillPatch(cfg(), cfg(), null)).toEqual({});
    expect(prefillPatch(cfg(), cfg(), hints({ repos: [], envFile: null, branch: null }))).toEqual({});
  });

  it('keeps the standard branch when the project does not say otherwise', () => {
    expect(prefillPatch(cfg(), cfg(), hints({ branch: null })).branch).toBeUndefined();
  });
});

describe('branchAfterPick', () => {
  it('adopts the picked repository\'s branch while the branch is still the untouched default', () => {
    expect(branchAfterPick(cfg(), { value: 'x', label: 'x', branch: 'main' })).toEqual({ branch: 'main' });
    expect(branchAfterPick(cfg({ branch: '' }), { value: 'x', label: 'x', branch: 'main' })).toEqual({ branch: 'main' });
  });

  it('never replaces a branch the person chose', () => {
    expect(branchAfterPick(cfg({ branch: 'release' }), { value: 'x', label: 'x', branch: 'main' })).toEqual({});
  });

  it('does nothing without a known branch', () => {
    expect(branchAfterPick(cfg(), { value: 'x', label: 'x', branch: null })).toEqual({});
    expect(branchAfterPick(cfg(), undefined)).toEqual({});
  });
});

describe('defaultBranchSuggestion', () => {
  it('suggests the repository\'s real default when the typed branch does not exist there', () => {
    expect(defaultBranchSuggestion(cfg({ branch: 'master' }), { branches: ['main', 'dev'], defaultBranch: 'main', error: null })).toBe('main');
  });

  it('says nothing when the branch exists or nothing is known', () => {
    expect(defaultBranchSuggestion(cfg({ branch: 'dev' }), { branches: ['main', 'dev'], defaultBranch: 'main', error: null })).toBeNull();
    expect(defaultBranchSuggestion(cfg({ branch: 'master' }), { branches: [], defaultBranch: null, error: 'x' })).toBeNull();
    expect(defaultBranchSuggestion(cfg(), null)).toBeNull();
  });
});

describe('addressWarning', () => {
  it('warns about a password or token inside an address, without repeating it', () => {
    const w = addressWarning('https://me:ghp_secret@github.com/o/r.git');
    expect(w).toMatch(/password or token/i);
    expect(w).not.toContain('ghp_secret');
    expect(addressWarning('https://ghp_tok@github.com/o/r.git')).toMatch(/password or token/i);
    expect(addressWarning('ssh://git:hunter2@host/o/r.git')).toMatch(/password or token/i);
  });

  it('is quiet for ordinary addresses', () => {
    expect(addressWarning('git@github.com:o/r.git')).toBeNull();
    expect(addressWarning('https://github.com/o/r.git')).toBeNull();
    expect(addressWarning('ssh://git@host/o/r.git')).toBeNull();
    expect(addressWarning('')).toBeNull();
  });
});
