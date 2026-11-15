/** Types and pure helpers for the "connect a server" wizard. They mirror the server's rules so mistakes show up at once. */

export type LoginRoute = 'password' | 'pemfile' | 'tailscale' | 'manual';

export interface RouteOption {
  id: LoginRoute;
  title: string;
  blurb: string;
}

export const ROUTES: RouteOption[] = [
  {
    id: 'password',
    title: 'I have a username and password',
    blurb: 'ShipRight logs in once with the password, installs its own key, and forgets the password straight away. Many cloud servers switch password login off; if yours does, pick one of the other choices.',
  },
  {
    id: 'pemfile',
    title: 'I have a key file (.pem), like AWS gives you',
    blurb: 'Pick the key file you saved in ShipRight. ShipRight uses it once to install its own key, so you do not need it again afterwards.',
  },
  {
    id: 'tailscale',
    title: 'The server is on Tailscale',
    blurb: 'Use the server\'s Tailscale name or its 100.x address. You will copy one line and run it in a terminal that already reaches the server.',
  },
  {
    id: 'manual',
    title: 'None of these: I can open a terminal on the server',
    blurb: 'You copy one line from ShipRight and run it on the server, in any way you can reach it (your cloud provider\'s web console works too).',
  },
];

export type StepId = 'route' | 'where' | 'identity' | 'access' | 'test';

export const STEP_ORDER: StepId[] = ['route', 'where', 'identity', 'access', 'test'];

export const STEP_TITLES: Record<StepId, string> = {
  route: 'How do you log in?',
  where: 'Where is the server?',
  identity: 'Is this the right server?',
  access: 'Give ShipRight access',
  test: 'Test the connection',
};

export const stepNumber = (s: StepId) => STEP_ORDER.indexOf(s) + 1;
export const nextStep = (s: StepId): StepId => STEP_ORDER[Math.min(STEP_ORDER.indexOf(s) + 1, STEP_ORDER.length - 1)];
export const prevStep = (s: StepId): StepId => STEP_ORDER[Math.max(STEP_ORDER.indexOf(s) - 1, 0)];

export interface WhereForm {
  name: string;
  host: string;
  username: string;
  remoteWorkingDir: string;
}

export function validateWhere(f: WhereForm): Partial<Record<keyof WhereForm, string>> {
  const e: Partial<Record<keyof WhereForm, string>> = {};
  const host = f.host.trim();
  if (!f.name.trim()) e.name = 'Give the server a name you will recognise.';
  if (!host) e.host = 'Enter the server\'s address.';
  else if (/^[a-z]+:\/\//i.test(host)) e.host = 'Enter just the address, without ssh:// or http://.';
  else if (host.includes('@')) e.host = 'Enter just the address. The username goes in the next box.';
  else if (/:\d+$/.test(host) && !host.includes('::')) e.host = 'ShipRight connects on port 22, so leave the port out.';
  else if (/\s/.test(host)) e.host = 'The address cannot contain spaces.';
  const user = f.username.trim();
  if (!user) e.username = 'Enter the username you log in with.';
  else if (!/^[A-Za-z_][A-Za-z0-9_.-]*\$?$/.test(user)) e.username = 'A username has letters, numbers, dots, dashes and underscores, and no spaces.';
  const dir = f.remoteWorkingDir.trim();
  if (dir) {
    if (!dir.startsWith('/')) e.remoteWorkingDir = 'Enter a full path that starts with /, for example /home/ubuntu/apps.';
    else if (dir.replace(/\/+$/, '') === '' ) e.remoteWorkingDir = 'Pick a folder, not the top of the disk.';
    else if (dir.split('/').includes('..')) e.remoteWorkingDir = 'The folder cannot contain "..".';
  }
  return e;
}

/** Wording only: a tailnet-style address gets Tailscale advice. Nothing depends on Tailscale. */
export function looksLikeTailnet(host: string): boolean {
  const h = host.trim().toLowerCase();
  if (!h) return false;
  if (h.endsWith('.ts.net')) return true;
  const m = /^100\.(\d{1,3})\.\d{1,3}\.\d{1,3}$/.exec(h);
  if (!m) return false;
  const second = Number(m[1]);
  return second >= 64 && second <= 127;
}

export function defaultFolder(username: string): string {
  const u = username.trim();
  if (!u) return '';
  return u === 'root' ? '/root/apps' : `/home/${u}/apps`;
}

const PUBLIC_KEY = /^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp\d+) [A-Za-z0-9+/=]+( [A-Za-z0-9@._:+-]+)?$/;

export const isValidPublicKey = (k: string) => PUBLIC_KEY.test(k.trim());

/** One line to run on the server: creates ~/.ssh, adds the key once, fixes the permissions. Safe to run again. */
export function authorizedKeysCommand(publicKey: string): string {
  const key = publicKey.trim();
  if (!isValidPublicKey(key)) throw new Error('That is not a valid public key.');
  return `mkdir -p ~/.ssh && chmod 700 ~/.ssh && (grep -qxF '${key}' ~/.ssh/authorized_keys 2>/dev/null || echo '${key}' >> ~/.ssh/authorized_keys) && chmod 600 ~/.ssh/authorized_keys && echo 'Key added.'`;
}

// ── server answers ───────────────────────────────────────────────────────────

export type CheckStatus = 'pass' | 'warn' | 'fail' | 'skipped';

export interface ConnectionCheck {
  id: string;
  title: string;
  status: CheckStatus;
  message: string;
  fix?: string | null;
  command?: string | null;
}

export interface ConnectionTestResponse {
  ready: boolean;
  summary: string;
  items: ConnectionCheck[];
}

export interface HostKeyResponse {
  sha256: string;
  algorithm: string;
  state: 'new' | 'trusted' | 'changed';
  verifyCommand: string;
  message: string;
}

export interface SshKeyStatus {
  exists: boolean;
  publicKey?: string | null;
  managedSshKey?: boolean;
  authorized?: boolean;
}

export const canFinish = (r: ConnectionTestResponse | null): boolean => !!r && r.items.every(i => i.status !== 'fail');

export function statusSymbol(s: CheckStatus): { symbol: string; label: string; color: string } {
  switch (s) {
    case 'pass': return { symbol: '✓', label: 'Passed', color: '#4CAF50' };
    case 'warn': return { symbol: '!', label: 'Needs attention, but will not stop you', color: '#C9A84C' };
    case 'fail': return { symbol: '✗', label: 'Failed', color: '#B84040' };
    default: return { symbol: '–', label: 'Not checked yet', color: '#8A93A5' };
  }
}

/** Stops waiting for a request that never answers. The slow request is not cancelled; its late answer is ignored by the caller. */
export function withTimeout<T>(promise: Promise<T>, ms: number, message: string): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject({ isError: true, message }), ms);
    promise.then(
      v => { clearTimeout(timer); resolve(v); },
      e => { clearTimeout(timer); reject(e); },
    );
  });
}
