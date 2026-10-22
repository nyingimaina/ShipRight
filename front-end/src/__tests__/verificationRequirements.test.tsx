import { spawn } from 'child_process';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { render, screen, waitFor } from '@testing-library/react';
import AuthGuard from '@/shared/auth/AuthGuard';
import { useAuthStore } from '@/shared/auth';
import { authApi } from '@/shared/auth/AuthApi';

// Verification-only Jest instruments for this run: they name requirements R1, R2 and R3 and exercise the
// route gate in desktop (single-user) and cloud mode. R2 also drives the start-up boot proof through its
// SHIPRIGHT_SMOKE_SERVER seam so the not-ready path is proven by a suite the checks actually run.
// They add no application code and change no behaviour.

jest.mock('@/shared/auth/AuthApi', () => ({
  authApi: {
    me: jest.fn(),
  },
}));

const mockRouter = { pathname: '/projects', replace: jest.fn() };
jest.mock('next/router', () => ({ useRouter: () => mockRouter }));

let mockMode: 'desktop' | 'cloud' = 'cloud';
jest.mock('@/shared/serverInfo', () => ({ useServerMode: () => mockMode }));

const meMock = authApi.me as jest.Mock;

const sessionUser = {
  id: 'u1',
  email: 'a@b.c',
  name: 'A',
  companyId: 'c1',
  role: 'Admin',
  mustChangePassword: false,
};

function renderGuardedHome(path: string) {
  mockRouter.pathname = path;
  return render(
    <AuthGuard>
      <div data-testid="home">home</div>
    </AuthGuard>,
  );
}

// Every front-end test source under src, bounded so the sweep can never walk away with the process.
function collectFrontEndTestSources(): string[] {
  const sources: string[] = [];
  const stack = [path.resolve(__dirname, '..')];
  let visited = 0;
  while (stack.length > 0 && visited < 1000) {
    const dir = stack.pop() as string;
    visited += 1;
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (entry.name === 'node_modules' || entry.name.startsWith('.')) continue;
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) stack.push(full);
      else if (/\.(test|spec)\.(ts|tsx)$/.test(entry.name)) sources.push(fs.readFileSync(full, 'utf8'));
    }
  }
  return sources;
}

function collectRegistrations(sources: string[]): { title: string; modifier: string }[] {
  const registrations: { title: string; modifier: string }[] = [];
  const registration = /\b(it|test)((?:\.\w+)+)?\s*\(\s*(['"`])((?:\\.|(?!\3)[^\\])*)\3/g;
  for (const source of sources) {
    let match: RegExpExecArray | null;
    while ((match = registration.exec(source)) !== null) {
      registrations.push({ title: match[4], modifier: match[2] ?? '' });
    }
  }
  return registrations;
}

beforeEach(() => {
  mockMode = 'cloud';
  mockRouter.replace.mockClear();
  meMock.mockReset();
  useAuthStore.getState().clearAuth();
});

describe('verification requirements R1, R2 and R3', () => {
  it('R1 desktop mode loads the home page for the local user with no sign-in and no identity call', () => {
    mockMode = 'desktop';
    renderGuardedHome('/');

    expect(screen.getByTestId('home')).toBeInTheDocument();
    expect(meMock).not.toHaveBeenCalled();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('R2 cloud mode sends an unsigned-in person to sign in instead of showing a protected page', async () => {
    meMock.mockRejectedValue({ message: 'Sign in required.', isError: true });
    renderGuardedHome('/projects');

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
    expect(screen.queryByTestId('home')).not.toBeInTheDocument();
  });

  it('R2 cloud mode shows a protected page to a signed-in person', async () => {
    meMock.mockResolvedValue({ user: sessionUser });
    renderGuardedHome('/projects');

    expect(await screen.findByTestId('home')).toBeInTheDocument();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it(
    'R2 the boot check names the failing step and exits instead of hanging when the server never becomes ready',
    async () => {
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-r2-boot-'));
      const fake = path.join(dir, 'never-ready.mjs');
      fs.writeFileSync(
        fake,
        "import { createServer } from 'node:http';\n" +
          "const port = Number(process.argv.find((a) => a.startsWith('--port='))?.slice('--port='.length));\n" +
          "createServer((_req, res) => { res.writeHead(503, { 'content-type': 'text/plain' }); res.end('not ready'); })" +
          ".listen(port, '127.0.0.1');\n",
      );

      const runner = path.resolve(__dirname, '..', '..', '..', 'scripts', 'smoke', 'run.mjs');
      const started = Date.now();
      try {
        const result = await new Promise<{ code: number | null; out: string }>((resolveRun) => {
          const child = spawn(process.execPath, [runner], {
            env: { ...process.env, SHIPRIGHT_SMOKE_SERVER: fake, SHIPRIGHT_SMOKE_TIMEOUT_MS: '1000' },
            stdio: ['ignore', 'pipe', 'pipe'],
          });
          let out = '';
          child.stdout?.on('data', (chunk) => (out += chunk));
          child.stderr?.on('data', (chunk) => (out += chunk));
          child.on('exit', (code) => resolveRun({ code, out }));
        });

        expect(result.code).not.toBe(0);
        expect(result.out).toMatch(/did not become ready/);
        expect(Date.now() - started).toBeLessThan(15000);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    },
    20000,
  );

  it('R3 each requirement is named by a front-end test that is neither skipped nor focused', () => {
    const registrations = collectRegistrations(collectFrontEndTestSources());

    for (const id of ['R1', 'R2', 'R3']) {
      const named = registrations.some(
        (registration) => registration.modifier === '' && registration.title.includes(id),
      );
      if (!named) throw new Error(`requirement ${id} must be named by an automated test that actually runs`);
    }

    const dishonest = registrations.filter(
      (registration) => /R[123]\b/.test(registration.title) && /\.(skip|todo|only)$/.test(registration.modifier),
    );
    expect(dishonest).toEqual([]);
  });
});
