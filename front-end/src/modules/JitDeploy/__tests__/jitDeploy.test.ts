import {
  DEFAULT_JIT_CONFIG, validateJitConfig, setDependentChoice, describeAge, shortSha, phaseSummary, JitConfig,
} from '../jitDeploy';

const valid = (over: Partial<JitConfig> = {}): JitConfig => ({
  ...DEFAULT_JIT_CONFIG, enabled: true, repoUrl: 'git@github.com:o/r.git', ...over,
});

describe('defaults', () => {
  it('start switched off, with the same sensible values the server uses', () => {
    expect(DEFAULT_JIT_CONFIG).toMatchObject({
      enabled: false, repoUrl: '', branch: 'master', composeProject: '', envFile: '',
      releasesToKeep: 3, stopGraceSeconds: 30, healthTimeoutSeconds: 60, settleSeconds: 10, minFreeDiskMb: 3000,
      buildNice: null, dependentChoices: [],
    });
  });
});

describe('validateJitConfig', () => {
  it('has nothing to say about a complete, valid configuration', () => {
    expect(validateJitConfig(valid())).toEqual({});
  });

  it('refuses an address with a password or token in it, without repeating it', () => {
    for (const url of ['https://me:ghp_secret@github.com/o/r.git', 'https://ghp_tok@github.com/o/r.git', 'ssh://git:hunter2@host/o/r.git']) {
      const e = validateJitConfig(valid({ repoUrl: url }));
      expect(e.repoUrl).toMatch(/password or token/i);
      expect(e.repoUrl).toMatch(/deploy key/i);
      expect(e.repoUrl).not.toMatch(/ghp_|hunter2|me:/);
    }
  });

  it('accepts a compose file inside the repository and refuses anything that could leave it', () => {
    for (const f of ['', 'docker-compose.yml', 'deploy/docker-compose.yml', 'ops/prod/compose.yaml'])
      expect(validateJitConfig(valid({ composeFile: f })).composeFile).toBeUndefined();
    for (const f of ['../x.yml', '/etc/x.yml', 'x.txt', 'my compose.yml', 'deploy//c.yml', 'a;b.yml'])
      expect(validateJitConfig(valid({ composeFile: f })).composeFile).toMatch(/inside your repository/i);
  });

  it('only lets a saved credential go with an https address, and not together with a deploy key', () => {
    expect(validateJitConfig(valid({ repoUrl: 'git@github.com:o/r.git', credentialResourceId: 'c1' })).credentialResourceId).toMatch(/https/i);
    expect(validateJitConfig(valid({ repoUrl: 'https://github.com/o/r.git', credentialResourceId: 'c1' })).credentialResourceId).toBeUndefined();
    expect(validateJitConfig(valid({ repoUrl: 'https://github.com/o/r.git', credentialResourceId: 'c1', useDeployKey: true })).credentialResourceId).toMatch(/either/i);
  });

  it('accepts ordinary addresses', () => {
    for (const url of ['git@github.com:o/r.git', 'https://github.com/o/r.git', 'ssh://git@host/o/r.git'])
      expect(validateJitConfig(valid({ repoUrl: url })).repoUrl).toBeUndefined();
  });

  it('lets a switched-off configuration be incomplete', () => {
    expect(validateJitConfig(DEFAULT_JIT_CONFIG)).toEqual({});
  });

  it('asks for the git address and the branch once it is switched on', () => {
    const e = validateJitConfig(valid({ repoUrl: '', branch: '' }));
    expect(e.repoUrl).toMatch(/git address/i);
    expect(e.branch).toMatch(/branch/i);
  });

  it.each([
    ['branch', '--delete'], ['branch', 'a b'], ['branch', 'a..b'], ['branch', 'feature/'],
    ['repoUrl', '--upload-pack=x'], ['repoUrl', 'has space'],
    ['composeProject', 'Bad Name'], ['envFile', 'relative/.env'],
  ] as const)('refuses an unsafe %s (%s)', (field, value) => {
    expect(validateJitConfig(valid({ [field]: value }))[field]).toBeTruthy();
  });

  it('accepts ordinary branch names, git addresses and paths', () => {
    expect(validateJitConfig(valid({ branch: 'release/1.2', repoUrl: 'https://github.com/o/r.git', envFile: '/home/me/.env', composeProject: 'my-shop' }))).toEqual({});
  });

  it.each([
    ['releasesToKeep', -1], ['releasesToKeep', 101],
    ['stopGraceSeconds', -1], ['stopGraceSeconds', 601],
    ['healthTimeoutSeconds', 4], ['healthTimeoutSeconds', 3601],
    ['settleSeconds', -1], ['settleSeconds', 601],
    ['minFreeDiskMb', 99],
    ['buildNice', -1], ['buildNice', 20],
  ] as const)('refuses %s = %s', (field, value) => {
    expect(validateJitConfig(valid({ [field]: value }))[field]).toBeTruthy();
  });

  it('refuses numbers that are not whole', () => {
    expect(validateJitConfig(valid({ stopGraceSeconds: 1.5 })).stopGraceSeconds).toBeTruthy();
    expect(validateJitConfig(valid({ releasesToKeep: Number.NaN })).releasesToKeep).toBeTruthy();
  });

  it('accepts a keep count below two: the server raises it to the minimum', () => {
    expect(validateJitConfig(valid({ releasesToKeep: 1 }))).toEqual({});
  });
});

describe('setDependentChoice', () => {
  it('adds an answer', () => {
    expect(setDependentChoice([], 'web', 'proxy', false)).toEqual([{ service: 'web', dependent: 'proxy', restart: false }]);
  });

  it('replaces an earlier answer for the same pair, and keeps the others', () => {
    const before = [
      { service: 'web', dependent: 'proxy', restart: false },
      { service: 'api', dependent: 'cron', restart: true },
    ];
    const after = setDependentChoice(before, 'web', 'proxy', true);
    expect(after).toEqual([
      { service: 'api', dependent: 'cron', restart: true },
      { service: 'web', dependent: 'proxy', restart: true },
    ]);
    expect(before[0].restart).toBe(false);   // not mutated
  });
});

describe('describeAge', () => {
  const now = new Date('2026-11-13T12:00:00Z');
  it.each([
    ['2026-11-13T11:59:40Z', 'just now'],
    ['2026-11-13T11:55:00Z', '5 minutes ago'],
    ['2026-11-13T11:59:00Z', '1 minute ago'],
    ['2026-11-13T09:00:00Z', '3 hours ago'],
    ['2026-11-13T11:00:00Z', '1 hour ago'],
    ['2026-11-11T12:00:00Z', '2 days ago'],
    ['2026-11-12T12:00:00Z', '1 day ago'],
  ])('%s is %s', (iso, text) => expect(describeAge(iso, now)).toBe(text));

  it('copes with a time in the future or nonsense', () => {
    expect(describeAge('2026-11-14T12:00:00Z', now)).toBe('just now');
    expect(describeAge('not a date', now)).toBe('');
  });
});

describe('shortSha', () => {
  it('shows seven characters', () => expect(shortSha('ab8b4a9f3c1d2e5f')).toBe('ab8b4a9'));
  it('copes with short or missing values', () => {
    expect(shortSha('abc')).toBe('abc');
    expect(shortSha(undefined)).toBe('');
  });
});

describe('phaseSummary', () => {
  it('says "ready" when nothing is going on', () => {
    expect(phaseSummary('Idle')).toEqual({ label: 'Ready', tone: 'ok' });
  });
  it('flags a server that needs attention', () => {
    expect(phaseSummary('RollbackFailed').tone).toBe('bad');
    expect(phaseSummary('OldStopped').tone).toBe('bad');
    expect(phaseSummary('NewStarted').tone).toBe('bad');
    expect(phaseSummary('RollingBack').tone).toBe('bad');
  });
  it('is calm about a deploy that was cut off before it changed anything', () => {
    expect(phaseSummary('Building').tone).toBe('warn');
  });
  it('copes with a phase it has never heard of', () => {
    expect(phaseSummary('Whatever')).toEqual({ label: 'Unknown', tone: 'warn' });
  });
});
