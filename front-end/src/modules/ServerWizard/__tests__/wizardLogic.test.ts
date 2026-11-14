import {
  authorizedKeysCommand, canFinish, ConnectionTestResponse, defaultFolder, isValidPublicKey, looksLikeTailnet,
  nextStep, prevStep, ROUTES, STEP_ORDER, stepNumber, statusSymbol, validateWhere,
} from '../wizardLogic';

const KEY = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGexample+key/with=chars shipright';

describe('steps', () => {
  it('has five steps in order', () => {
    expect(STEP_ORDER).toEqual(['route', 'where', 'identity', 'access', 'test']);
    expect(stepNumber('route')).toBe(1);
    expect(stepNumber('test')).toBe(5);
  });

  it('moves forwards and backwards, stopping at the ends', () => {
    expect(nextStep('route')).toBe('where');
    expect(nextStep('test')).toBe('test');
    expect(prevStep('where')).toBe('route');
    expect(prevStep('route')).toBe('route');
  });
});

describe('routes', () => {
  it('offers password, key file, tailscale and manual, each with plain words', () => {
    expect(ROUTES.map(r => r.id)).toEqual(['password', 'pemfile', 'tailscale', 'manual']);
    ROUTES.forEach(r => { expect(r.title.length).toBeGreaterThan(5); expect(r.blurb.length).toBeGreaterThan(20); });
  });
});

describe('validateWhere', () => {
  const ok = { name: 'Web', host: 'box.example.com', username: 'ubuntu', remoteWorkingDir: '' };

  it('accepts a good form, with the folder optional', () => {
    expect(validateWhere(ok)).toEqual({});
    expect(validateWhere({ ...ok, remoteWorkingDir: '/home/ubuntu/apps' })).toEqual({});
  });

  it('requires a name, host and username', () => {
    const e = validateWhere({ name: ' ', host: '', username: '', remoteWorkingDir: '' });
    expect(Object.keys(e).sort()).toEqual(['host', 'name', 'username']);
  });

  it('catches an address typed with a scheme, a user, a port or spaces', () => {
    expect(validateWhere({ ...ok, host: 'ssh://box.example.com' }).host).toMatch(/just the address/i);
    expect(validateWhere({ ...ok, host: 'ubuntu@box.example.com' }).host).toMatch(/username/i);
    expect(validateWhere({ ...ok, host: 'box.example.com:22' }).host).toMatch(/port/i);
    expect(validateWhere({ ...ok, host: 'my server' }).host).toBeDefined();
  });

  it('accepts a tailscale name and a 100.x address', () => {
    expect(validateWhere({ ...ok, host: 'box.tail1a2b3.ts.net' })).toEqual({});
    expect(validateWhere({ ...ok, host: '100.101.5.6' })).toEqual({});
  });

  it('rejects an odd username and a relative or dotted folder', () => {
    expect(validateWhere({ ...ok, username: 'bad name' }).username).toBeDefined();
    expect(validateWhere({ ...ok, remoteWorkingDir: 'apps' }).remoteWorkingDir).toMatch(/\//);
    expect(validateWhere({ ...ok, remoteWorkingDir: '/a/../b' }).remoteWorkingDir).toBeDefined();
    expect(validateWhere({ ...ok, remoteWorkingDir: '/' }).remoteWorkingDir).toBeDefined();
  });
});

describe('looksLikeTailnet', () => {
  it.each([
    ['box.tail1a2b3.ts.net', true], ['100.101.5.6', true], ['100.64.0.1', true], ['100.127.255.254', true],
    ['100.63.0.1', false], ['100.128.0.1', false], ['example.com', false], ['10.0.0.5', false], ['', false],
  ])('%s -> %s', (host, expected) => expect(looksLikeTailnet(host)).toBe(expected));
});

describe('defaultFolder', () => {
  it('suggests a folder in the home directory, and root has its own', () => {
    expect(defaultFolder('ubuntu')).toBe('/home/ubuntu/apps');
    expect(defaultFolder('root')).toBe('/root/apps');
    expect(defaultFolder('')).toBe('');
  });
});

describe('public key and the command that adds it', () => {
  it('recognises a public key and refuses anything with quotes or newlines', () => {
    expect(isValidPublicKey(KEY)).toBe(true);
    expect(isValidPublicKey('ssh-rsa AAAAB3NzaC1yc2E=')).toBe(true);
    expect(isValidPublicKey("ssh-ed25519 AAAA'; rm -rf / '")).toBe(false);
    expect(isValidPublicKey('ssh-ed25519 AAAA\nssh-ed25519 BBBB')).toBe(false);
    expect(isValidPublicKey('not a key')).toBe(false);
    expect(isValidPublicKey('')).toBe(false);
  });

  it('builds one safe, repeatable line that runs on the server', () => {
    const c = authorizedKeysCommand(KEY);
    expect(c).not.toContain('\n');
    expect(c).toContain('mkdir -p ~/.ssh && chmod 700 ~/.ssh');
    expect(c).toContain('chmod 600 ~/.ssh/authorized_keys');
    expect(c).toContain(`grep -qxF '${KEY}' ~/.ssh/authorized_keys`);
    expect(c).toContain(`echo '${KEY}' >> ~/.ssh/authorized_keys`);
    expect(c.toLowerCase()).not.toContain('shipright@');
  });

  it('refuses to build a command from something that is not a key', () => {
    expect(() => authorizedKeysCommand("x'; touch /tmp/pwned; '")).toThrow(/not a valid public key/i);
  });
});

describe('connection results', () => {
  const result = (statuses: string[]): ConnectionTestResponse => ({
    ready: statuses.every(s => s !== 'fail'),
    summary: 's',
    items: statuses.map((status, i) => ({ id: `c${i}`, title: `T${i}`, status: status as never, message: 'm', fix: null, command: null })),
  });

  it('can finish only when nothing failed; warnings are fine', () => {
    expect(canFinish(result(['pass', 'warn', 'skipped']))).toBe(true);
    expect(canFinish(result(['pass', 'fail']))).toBe(false);
    expect(canFinish(null)).toBe(false);
  });

  it('shows a different symbol, and never relies on colour alone', () => {
    const s = (['pass', 'warn', 'fail', 'skipped'] as const).map(statusSymbol);
    expect(new Set(s.map(x => x.symbol)).size).toBe(4);
    s.forEach(x => expect(x.label.length).toBeGreaterThan(2));
  });
});
