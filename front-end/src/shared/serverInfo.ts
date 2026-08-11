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
