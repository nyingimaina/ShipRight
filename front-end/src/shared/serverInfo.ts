import { useEffect, useState } from 'react';

export type ServerMode = 'desktop' | 'cloud';

let cached: Promise<ServerMode> | null = null;

// A local Next dev server runs on a different origin from the desktop API, so health must follow the
// same configured API origin as the rest of the app. The value is read per call so tests and dev
// environments can change it without re-importing this module.
const healthUrl = () => `${(process.env.NEXT_PUBLIC_API_URL ?? '').replace(/\/$/, '')}/api/health`;

export function loadServerMode(): Promise<ServerMode> {
  if (!cached) {
    cached = fetch(healthUrl())
      .then((res) => {
        if (!res.ok) throw new Error('Health check failed');
        return res.json() as Promise<{ mode?: string }>;
      })
      // The server serializes AppMode.Cloud as "Cloud". Comparing case-sensitively made hosted deployments look like
      // desktop mode, which skipped the auth guard and showed the project page without a sign-in prompt.
      .then((data) => (typeof data?.mode === 'string' && data.mode.toLowerCase() === 'cloud' ? 'cloud' : 'desktop'))
      .catch(() => 'desktop');
  }
  return cached;
}

let cachedVersion: Promise<string | null> | null = null;

/** The version of the running ShipRight, as the server itself reports it; null when it cannot say. */
export function loadServerVersion(): Promise<string | null> {
  if (!cachedVersion) {
    cachedVersion = fetch(healthUrl())
      .then((res) => {
        if (!res.ok) throw new Error('Health check failed');
        return res.json() as Promise<{ webVersion?: string; serverVersion?: string }>;
      })
      // serverVersion is the assembly version (1.0.0); webVersion is the release people actually installed.
      .then((data) => {
        const version = data?.webVersion ?? data?.serverVersion;
        return typeof version === 'string' && version ? version : null;
      })
      .catch(() => null);
  }
  return cachedVersion;
}

export function resetServerVersionCache(): void {
  cachedVersion = null;
}

export function useServerVersion(): string | null {
  const [version, setVersion] = useState<string | null>(null);

  useEffect(() => {
    let mounted = true;
    loadServerVersion().then((v) => {
      if (mounted) setVersion(v);
    });
    return () => {
      mounted = false;
    };
  }, []);

  return version;
}

export function resetServerModeCache(): void {
  cached = null;
}

export function useServerMode(): ServerMode | null {
  const [mode, setMode] = useState<ServerMode | null>(null);

  useEffect(() => {
    let mounted = true;
    loadServerMode().then((resolved) => {
      if (mounted) setMode(resolved);
    });
    return () => {
      mounted = false;
    };
  }, []);

  return mode;
}
