// Tests for the start-up smoke runner (R11 / BOOT-001).
//
// The runner is exercised end-to-end against a tiny fake server (plain Node) injected through
// its SHIPRIGHT_SMOKE_SERVER seam, so exit codes are proven without .NET, a database or secrets.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  EXIT,
  REPO_ROOT,
  buildEnv,
  createLineBuffer,
  pickFreePort,
  resolveContentRoot,
  resolveServerCommand,
  waitForReady,
} from '../run.mjs';

const RUNNER = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'run.mjs');

const FAKE_SERVER = `
import { writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
const infoFile = process.env.SMOKE_CHILD_INFO;
if (infoFile) writeFileSync(infoFile, JSON.stringify({ pid: process.pid, dataDir: process.env.SHIPRIGHT_DATA_DIR ?? '', cwd: process.cwd() }));
const portArg = process.argv.find((a) => a.startsWith('--port='));
const port = Number(portArg?.slice('--port='.length));
const mode = process.env.FAKE_MODE ?? 'ok';
if (mode === 'exit') process.exit(7);
const server = createServer((req, res) => {
  if (req.url === '/api/health') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ status: 'healthy', mode: 'Desktop' }));
  } else if (req.url === '/') {
    if (mode === 'badhome') { res.writeHead(500, { 'content-type': 'text/html' }); res.end('nope'); }
    else { res.writeHead(200, { 'content-type': 'text/html' }); res.end('<html>ok</html>'); }
  } else if (req.url === '/api/projects') {
    if (mode === 'badprojects') { res.writeHead(200, { 'content-type': 'text/plain' }); res.end('[]'); }
    else { res.writeHead(200, { 'content-type': 'application/json' }); res.end('[]'); }
  } else if (req.url === '/api/auth/providers') {
    if (mode === 'exposesignin') { res.writeHead(200, { 'content-type': 'application/json' }); res.end('{"providers":[]}'); }
    else { res.writeHead(404, { 'content-type': 'text/plain' }); res.end('not found'); }
  } else { res.writeHead(404); res.end(); }
});
server.listen(port, '127.0.0.1');
`;

function withFakeServer(mode, fn) {
  const dir = mkdtempSync(join(tmpdir(), 'smoke-test-'));
  const fake = join(dir, 'fake-server.mjs');
  writeFileSync(fake, FAKE_SERVER);
  return fn({ dir, fake, mode }).finally(() => rmSync(dir, { recursive: true, force: true }));
}

function runRunner({ fake, mode, timeoutMs = 5000, env = {} }) {
  return new Promise((resolveRun) => {
    const child = spawn(process.execPath, [RUNNER], {
      env: {
        ...process.env,
        SHIPRIGHT_SMOKE_SERVER: fake,
        FAKE_MODE: mode,
        SHIPRIGHT_SMOKE_TIMEOUT_MS: String(timeoutMs),
        ...env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let out = '';
    child.stdout.on('data', (c) => (out += c));
    child.stderr.on('data', (c) => (out += c));
    child.on('exit', (code) => resolveRun({ code, out }));
  });
}

function isProcessAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === 'EPERM';
  }
}

async function waitForProcessGone(pid, timeoutMs = 3000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline && isProcessAlive(pid)) {
    await new Promise((r) => setTimeout(r, 40));
  }
  return !isProcessAlive(pid);
}

test('R11 pickFreePort returns a usable loopback port', async () => {
  const port = await pickFreePort();
  assert.ok(Number.isInteger(port) && port > 0 && port <= 65535);
});

test('R11 buildEnv strips cloud, mail, Google and removed settings', () => {
  const env = buildEnv(
    {
      PATH: '/usr/bin',
      SHIPRIGHT__MODE: 'Cloud',
      SHIPRIGHT__DB_CONNECTION: 'Server=db;Password=secret',
      SHIPRIGHT__SMTP_PASSWORD: 'secret',
      SHIPRIGHT__GOOGLE_CLIENT_SECRET: 'secret',
      SHIPRIGHT__JWT_SECRET: 'legacy',
    },
    { port: 1234, dataDir: '/tmp/data' },
  );
  assert.equal(env.PATH, '/usr/bin');
  assert.equal(env.SHIPRIGHT_PORT, '1234');
  assert.equal(env.SHIPRIGHT_DATA_DIR, '/tmp/data');
  assert.equal(env.ASPNETCORE_ENVIRONMENT, 'Production');
  for (const key of Object.keys(env)) {
    assert.ok(!key.startsWith('SHIPRIGHT__'), `cloud/removed setting leaked: ${key}`);
  }
});

test('R11 resolveServerCommand runs a .mjs fake, a .dll via dotnet and falls back to dotnet run', () => {
  const fakeMjs = resolveServerCommand({ SHIPRIGHT_SMOKE_SERVER: 'C:/tmp/fake.mjs' }, REPO_ROOT);
  assert.equal(fakeMjs.command, process.execPath);
  assert.deepEqual(fakeMjs.args, [resolve('C:/tmp/fake.mjs')]);

  const dll = resolveServerCommand({ SHIPRIGHT_SMOKE_SERVER: 'C:/tmp/ShipRight.Server.dll' }, REPO_ROOT);
  assert.equal(dll.command, 'dotnet');
  assert.deepEqual(dll.args, [resolve('C:/tmp/ShipRight.Server.dll')]);

  const repo = mkdtempSync(join(tmpdir(), 'smoke-repo-'));
  try {
    const fallback = resolveServerCommand({}, repo);
    assert.equal(fallback.command, 'dotnet');
    assert.ok(fallback.args.includes('run'), 'falls back to dotnet run when nothing is built');

    const binDir = join(repo, 'back-end', 'ShipRight.Server', 'bin', 'Debug', 'net10.0');
    mkdirSync(binDir, { recursive: true });
    writeFileSync(join(binDir, 'ShipRight.Server.dll'), 'stub');
    const built = resolveServerCommand({}, repo);
    assert.equal(built.command, 'dotnet');
    assert.ok(built.args[0].endsWith('ShipRight.Server.dll'), 'prefers the built server DLL');
  } finally {
    rmSync(repo, { recursive: true, force: true });
  }
});

test('R11 resolveContentRoot points at a directory that holds wwwroot/index.html', () => {
  const dir = mkdtempSync(join(tmpdir(), 'smoke-content-'));
  try {
    const { root } = resolveContentRoot(REPO_ROOT, dir);
    assert.ok(existsSync(join(root, 'wwwroot', 'index.html')), 'content root must contain wwwroot/index.html');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('R11 waitForReady gives up within the time limit instead of hanging', async () => {
  const never = async () => ({ status: 503, contentType: 'text/plain', body: '' });
  const started = Date.now();
  await assert.rejects(
    () => waitForReady('http://127.0.0.1:1', { timeoutMs: 300, intervalMs: 50, request: never }),
    (error) => error.code === EXIT.serverNotReady,
  );
  assert.ok(Date.now() - started < 2000, 'must not hang');
});

test('R11 runner exits 0 when the app starts, the home page loads and projects are readable', async () => {
  await withFakeServer('ok', async ({ fake }) => {
    const { code, out } = await runRunner({ fake, mode: 'ok' });
    assert.equal(code, EXIT.ok, out);
    assert.match(out, /PASS/);
  });
});

test('R11 runner fails with a plain message when the app does not start', async () => {
  await withFakeServer('exit', async ({ fake }) => {
    const { code, out } = await runRunner({ fake, mode: 'exit', timeoutMs: 1200 });
    assert.equal(code, EXIT.serverNotReady, out);
    assert.match(out, /did not become ready/);
  });
});

test('R11 runner fails when the home page does not load', async () => {
  await withFakeServer('badhome', async ({ fake }) => {
    const { code, out } = await runRunner({ fake, mode: 'badhome' });
    assert.equal(code, EXIT.homePage, out);
    assert.match(out, /home page/);
  });
});

test('R11 runner fails when projects data cannot be read', async () => {
  await withFakeServer('badprojects', async ({ fake }) => {
    const { code, out } = await runRunner({ fake, mode: 'badprojects' });
    assert.equal(code, EXIT.projectsRead, out);
    assert.match(out, /projects data/);
  });
});

test('R11 runner fails when a sign-in route is exposed in desktop mode', async () => {
  await withFakeServer('exposesignin', async ({ fake }) => {
    const { code, out } = await runRunner({ fake, mode: 'exposesignin' });
    assert.equal(code, EXIT.signInRouteExposed, out);
    assert.match(out, /sign-in route/);
  });
});

test('R11 createLineBuffer keeps only the most recent lines in bounded memory', () => {
  const buffer = createLineBuffer(3);
  for (let i = 1; i <= 10; i++) buffer.push(`line-${i}`);

  const kept = buffer.toArray();
  assert.deepEqual(kept, ['line-8', 'line-9', 'line-10']);
  assert.ok(kept.length <= 3, 'the buffer must never grow past its cap');
});

test('R11 the runner leaves no app process running and keeps all data in a temp dir it removes', async () => {
  for (const mode of ['ok', 'badhome']) {
    await withFakeServer(mode, async ({ dir, fake }) => {
      const infoFile = join(dir, 'child.json');
      const { code } = await runRunner({ fake, mode, env: { SMOKE_CHILD_INFO: infoFile } });

      assert.ok(code === EXIT.ok || code === EXIT.homePage, `unexpected exit ${code} in ${mode} mode`);
      assert.ok(existsSync(infoFile), 'the fake app never started');

      const child = JSON.parse(readFileSync(infoFile, 'utf8'));
      assert.ok(child.dataDir.startsWith(tmpdir()), `app data must live under the OS temp dir, got ${child.dataDir}`);
      assert.equal(existsSync(child.dataDir), false, 'the runner must delete its temp data dir');
      assert.equal(child.cwd, dir, 'the app must run outside the project tree');
      assert.ok(await waitForProcessGone(child.pid), 'the runner must leave no app process running');
    });
  }
});
