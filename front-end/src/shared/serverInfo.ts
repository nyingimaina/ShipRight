import { useEffect, useState } from 'react';

export type ServerMode = 'desktop' | 'cloud';

let cached: Promise<ServerMode> | null = null;

export function loadServerMode(): Promise<ServerMode> {
  if (!cached) {
    cached = fetch('/api/health')
      .then((res) => {
        if (!res.ok) throw new Error('Health check failed');
        return res.json() as Promise<{ mode?: string }>;
      })
      .then((data) => (data?.mode === 'cloud' ? 'cloud' : 'desktop'))
      .catch(() => 'desktop');
  }
  return cached;
}

let cachedVersion: Promise<string | null> | null = null;

/** The version of the running ShipRight, as the server itself reports it; null when it cannot say. */
export function loadServerVersion(): Promise<string | null> {
  if (!cachedVersion) {
    cachedVersion = fetch('/api/health')
      .then((res) => {
        if (!res.ok) throw new Error('Health check failed');
        return res.json() as Promise<{ serverVersion?: string }>;
      })
      .then((data) => (typeof data?.serverVersion === 'string' && data.serverVersion ? data.serverVersion : null))
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
