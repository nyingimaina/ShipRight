import { useEffect, useState } from 'react';

export type ServerMode = 'desktop' | 'cloud';

interface Health {
  mode?: string;
  webVersion?: string;
  serverVersion?: string;
}

let cachedHealth: Promise<Health> | null = null;

// A local Next dev server runs on a different origin from the desktop API, so health must follow the
// same configured API origin as the rest of the app. The value is read per call so tests and dev
// environments can change it without re-importing this module.
const healthUrl = () => `${(process.env.NEXT_PUBLIC_API_URL ?? '').replace(/\/$/, '')}/api/health`;

/** One request per session for everything the shell needs from the health endpoint. */
export function loadHealth(): Promise<Health> {
  if (!cachedHealth) {
    cachedHealth = fetch(healthUrl())
      .then((res) => {
        if (!res.ok) throw new Error('Health check failed');
        return res.json() as Promise<Health>;
      })
      .catch(() => ({}));
  }
  return cachedHealth;
}

/** The server serializes AppMode.Cloud as "Cloud", so compare without case. */
export function loadServerMode(): Promise<ServerMode> {
  return loadHealth().then((data) =>
    typeof data?.mode === 'string' && data.mode.toLowerCase() === 'cloud' ? 'cloud' : 'desktop');
}

/** The version of the running ShipRight, as the server itself reports it; null when it cannot say. */
export function loadServerVersion(): Promise<string | null> {
  return loadHealth().then((data) => {
    // serverVersion is the assembly version (1.0.0); webVersion is the release people actually installed.
    const version = data?.webVersion ?? data?.serverVersion;
    return typeof version === 'string' && version ? version : null;
  });
}

export function resetServerVersionCache(): void {
  cachedHealth = null;
}

export function resetServerModeCache(): void {
  cachedHealth = null;
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
