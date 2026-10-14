#!/usr/bin/env node
// Start-up smoke runner (R11 / BOOT-001).
//
// Starts the built ShipRight.Server in desktop/local mode (no cloud settings, no sign-in),
// waits for it to become ready, loads the home page and reads projects data with no
// credentials, then stops the app. Exits 0 on success, or non-zero with the failing step.
//
// Plain Node built-ins only: no npm package, no database and no secrets are needed.
// Everything it opens (child process, temp data dir, temp content dir, sockets) is released.

import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { createServer as createHttpServer } from 'node:http';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

// Exit codes: one per failing step, so the failing step is visible from the code alone.
export const EXIT = {
  ok: 0,
  serverNotReady: 1,
  homePage: 2,
  projectsRead: 3,
  signInRouteExposed: 4,
  serverError: 5,
};

// Cloud-only setting group. Desktop mode must start without any of these, and the runner
// must never need (or print) mail or Google settings.
const CLOUD_SETTINGS = [
  'SHIPRIGHT__MODE',
  'SHIPRIGHT__TIME_ZONE',
  'SHIPRIGHT__DB_CONNECTION',
  'SHIPRIGHT__CORS_ORIGINS',
  'SHIPRIGHT__PUBLIC_URL',
  'SHIPRIGHT__SMTP_HOST',
  'SHIPRIGHT__SMTP_PORT',
  'SHIPRIGHT__SMTP_USERNAME',
  'SHIPRIGHT__SMTP_PASSWORD',
  'SHIPRIGHT__SMTP_FROM',
  'SHIPRIGHT__GOOGLE_CLIENT_ID',
  'SHIPRIGHT__GOOGLE_CLIENT_SECRET',
];

// A setting someone may have left in their environment before the switch to Identity;
// it is no longer read, and leaving it in must not break start-up (R13).
const REMOVED_SETTINGS = ['SHIPRIGHT__JWT_SECRET', 'SHIPRIGHT__ADMIN_EMAIL', 'SHIPRIGHT__ADMIN_PASSWORD'];

/** Pick a free loopback TCP port by letting the OS choose one, then releasing it. */
export async function pickFreePort() {
  return await new Promise((resolvePort, reject) => {
    const server = createServer();
    server.unref();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolvePort(port));
    });
  });
}

/** Build the clean desktop-mode environment: no cloud, mail, Google or removed settings. */
export function buildEnv(baseEnv, { port, dataDir }) {
  const env = { ...baseEnv };
  for (const key of [...CLOUD_SETTINGS, ...REMOVED_SETTINGS]) delete env[key];
  if (env.SHIPRIGHT_SMOKE_SERVER) delete env.SHIPRIGHT_SMOKE_SERVER;
  env.SHIPRIGHT_PORT = String(port);
  env.SHIPRIGHT_DATA_DIR = dataDir;
  env.ASPNETCORE_ENVIRONMENT = 'Production';
  env.DOTNET_NOLOGO = '1';
  env.DOTNET_CLI_TELEMETRY_OPTOUT = '1';
  return env;
}

/**
 * Resolve the server command. A test seam (SHIPRIGHT_SMOKE_SERVER) lets the runner be
 * exercised without .NET; otherwise the built DLL is used, falling back to `dotnet run`.
 */
export function resolveServerCommand(env, repoRoot) {
  const override = env.SHIPRIGHT_SMOKE_SERVER;
  if (override) {
    const target = resolve(override);
    const dir = dirname(target);
    if (target.endsWith('.dll')) return { command: env.DOTNET ?? 'dotnet', args: [target], cwd: dir };
    if (target.endsWith('.mjs') || target.endsWith('.js')) return { command: process.execPath, args: [target], cwd: dir };
    return { command: target, args: [], cwd: dir };
  }

  const projectDir = join(repoRoot, 'back-end', 'ShipRight.Server');
  const builtDll = findBuiltServerDll(projectDir);
  if (builtDll) return { command: env.DOTNET ?? 'dotnet', args: [builtDll], cwd: dirname(builtDll) };

  return {
    command: env.DOTNET ?? 'dotnet',
    args: ['run', '--project', projectDir, '-c', 'Debug', '--no-launch-profile', '--'],
    cwd: repoRoot,
  };
}

/** Look for a previously built server, newest first, bounded to the Debug tree. */
export function findBuiltServerDll(projectDir) {
  const candidates = [
    join(projectDir, 'bin', 'Debug', 'net10.0', 'ShipRight.Server.dll'),
    join(projectDir, 'bin', 'Release', 'net10.0', 'ShipRight.Server.dll'),
  ];
  return candidates.find((candidate) => existsSync(candidate)) ?? null;
}

/**
 * Resolve a web content root that has wwwroot/index.html (MapFallbackToFile needs it).
 * Uses the project's own wwwroot when present; otherwise stages a minimal copy under the
 * temp data dir (never touching tracked files).
 */
export function resolveContentRoot(repoRoot, dataDir) {
  const projectDir = join(repoRoot, 'back-end', 'ShipRight.Server');
  if (existsSync(join(projectDir, 'wwwroot', 'index.html'))) return { root: projectDir, staged: false };

  const staged = join(dataDir, 'content');
  const wwwroot = join(staged, 'wwwroot');
  mkdirSync(wwwroot, { recursive: true });
  const exported = join(repoRoot, 'front-end', 'out', 'index.html');
  if (existsSync(exported)) {
    copyFileSync(exported, join(wwwroot, 'index.html'));
  } else {
    writeFileSync(
      join(wwwroot, 'index.html'),
      '<!doctype html><html lang="en"><head><meta charset="utf-8"><title>ShipRight</title></head>' +
        '<body><div id="__next">ShipRight</div></body></html>\n',
    );
  }
  return { root: staged, staged: true };
}

/** One HTTP request with a hard timeout; returns status, content type and body text. */
export function httpRequest(url, { method = 'GET', timeoutMs = 5000 } = {}) {
  return new Promise((resolveRequest, reject) => {
    const request = fetch(url, { method, signal: AbortSignal.timeout(timeoutMs) });
    request.then(
      async (response) => {
        const body = await response.text();
        resolveRequest({ status: response.status, contentType: response.headers.get('content-type') ?? '', body });
      },
      (error) => reject(error),
    );
  });
}

/** Poll /api/health until it answers 200, or fail after a fixed time limit (never hangs). */
export async function waitForReady(baseUrl, { timeoutMs = 60000, intervalMs = 250, request = httpRequest } = {}) {
  const deadline = Date.now() + timeoutMs;
  let lastDetail = 'no response yet';
  while (Date.now() < deadline) {
    try {
      const response = await request(`${baseUrl}/api/health`);
      if (response.status === 200) return;
      lastDetail = `HTTP ${response.status}`;
    } catch (error) {
      lastDetail = error?.message ?? String(error);
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new SmokeStepError(EXIT.serverNotReady, 'server did not become ready in time', lastDetail);
}

export class SmokeStepError extends Error {
  constructor(code, step, detail) {
    super(`${step}${detail ? `: ${detail}` : ''}`);
    this.name = 'SmokeStepError';
    this.code = code;
    this.step = step;
    this.detail = detail;
  }
}

/** The home page must be 200 HTML. */
export function checkHomePage(response) {
  if (response.status !== 200) throw new SmokeStepError(EXIT.homePage, 'home page did not load', `HTTP ${response.status}`);
  if (!response.contentType.includes('text/html')) {
    throw new SmokeStepError(EXIT.homePage, 'home page was not HTML', response.contentType || 'no content type');
  }
}

/** Projects data must be readable with no credentials and come back as JSON. */
export function checkProjectsRead(response) {
  if (response.status !== 200) throw new SmokeStepError(EXIT.projectsRead, 'projects data was not readable', `HTTP ${response.status}`);
  if (!response.contentType.includes('application/json')) {
    throw new SmokeStepError(EXIT.projectsRead, 'projects data was not JSON', response.contentType || 'no content type');
  }
}

/** Desktop/local mode must not expose a sign-in endpoint. */
export function checkNoSignInRoute(response) {
  const exposedAsJson = response.status === 200 && response.contentType.includes('application/json');
  if (exposedAsJson) {
    throw new SmokeStepError(EXIT.signInRouteExposed, 'a sign-in route was exposed in desktop mode', `HTTP ${response.status}`);
  }
}

/** Stop the server and its children; guarantees no app process is left running. */
export async function stopServer(child, { timeoutMs = 5000 } = {}) {
  if (!child || child.exitCode !== null || child.killed) return;
  const exited = new Promise((resolveExit) => {
    child.once('exit', () => resolveExit(true));
    setTimeout(() => resolveExit(false), timeoutMs);
  });
  child.kill();
  const done = await exited;
  if (done) return;
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
  } else {
    try {
      process.kill(-child.pid, 'SIGKILL');
    } catch {
      try { child.kill('SIGKILL'); } catch { /* already gone */ }
    }
  }
}

/**
 * A fixed-size ring buffer for captured process output: memory stays bounded no matter how
 * chatty or looping the server is, while still keeping the most recent lines for the report.
 */
export function createLineBuffer(maxLines = 200) {
  const lines = new Array(maxLines);
  let count = 0;
  let next = 0;
  return {
    push(line) {
      lines[next] = line;
      next = (next + 1) % maxLines;
      if (count < maxLines) count++;
    },
    toArray() {
      const start = count < maxLines ? 0 : next;
      const out = [];
      for (let i = 0; i < count; i++) out.push(lines[(start + i) % maxLines]);
      return out;
    },
  };
}

function tail(buffer, max = 20) {
  return buffer.toArray().slice(-max);
}

async function main({ log = console.log, env = process.env } = {}) {
  const port = await pickFreePort();
  const baseUrl = `http://127.0.0.1:${port}`;
  const dataDir = mkdtempSync(join(tmpdir(), 'shipright-smoke-'));
  const timeoutMs = Number(env.SHIPRIGHT_SMOKE_TIMEOUT_MS) || 60000;
  const { root: contentRoot, staged } = resolveContentRoot(REPO_ROOT, dataDir);
  const { command, args, cwd } = resolveServerCommand(env, REPO_ROOT);
  const serverArgs = [
    ...args,
    `--port=${port}`,
    `--data-dir=${dataDir}`,
    `--contentRoot=${contentRoot}`,
  ];

  log(`[smoke] 1/6 starting server: ${command} (port ${port}, ${staged ? 'staged' : 'project'} content)`);
  log(`[smoke] data dir: ${dataDir}`);

  const child = spawn(command, serverArgs, {
    cwd,
    env: buildEnv(env, { port, dataDir }),
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });

  const output = createLineBuffer(200);
  const capture = (chunk) => {
    for (const line of String(chunk).split(/\r?\n/)) {
      if (line.trim()) output.push(line.trimEnd());
    }
  };
  child.stdout?.on('data', capture);
  child.stderr?.on('data', capture);
  child.on('error', (error) => capture(`[spawn error] ${error.message}`));

  try {
    log('[smoke] 2/6 waiting for /api/health ...');
    await waitForReady(baseUrl, { timeoutMs });
    log('[smoke] 3/6 server ready');

    log('[smoke] 4/6 loading home page /');
    checkHomePage(await httpRequest(`${baseUrl}/`));

    log('[smoke] 5/6 reading projects data /api/projects');
    checkProjectsRead(await httpRequest(`${baseUrl}/api/projects`));

    log('[smoke] 6/6 checking no sign-in route is exposed');
    checkNoSignInRoute(await httpRequest(`${baseUrl}/api/auth/providers`));

    log('[smoke] PASS: app starts, home page loads and projects data is readable without sign-in');
    return EXIT.ok;
  } catch (error) {
    log(`[smoke] FAIL: ${error.message}`);
    const recent = tail(output);
    if (recent.length) {
      log('[smoke] last server output:');
      for (const line of recent) log(`[smoke]   ${line}`);
    }
    if (error instanceof SmokeStepError) return error.code;
    return EXIT.serverError;
  } finally {
    log('[smoke] stopping server and cleaning up');
    await stopServer(child);
    rmSync(dataDir, { recursive: true, force: true });
  }
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href;
if (isMain) {
  main().then(
    (code) => process.exit(code),
    (error) => {
      console.error(`[smoke] FAIL: ${error?.message ?? error}`);
      process.exit(EXIT.serverError);
    },
  );
}

export { main };
