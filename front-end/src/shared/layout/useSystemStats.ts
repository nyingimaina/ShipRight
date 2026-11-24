import { useEffect, useState } from 'react';
import Poller from 'jattac-web-poller';
import { api } from '@/shared/ApiService';
import type { ISystemStats } from './ISystemStats';

/** Polls the host's own counters about every 10s — no SSH and no per-server metrics call. */
export function useSystemStats(intervalMs = 10_000) {
  const [stats, setStats] = useState<ISystemStats | null>(null);

  useEffect(() => {
    const poller = new Poller<ISystemStats>({
      func: () => api.get<ISystemStats>('/api/system/stats'),
      pollIntervalMilliseconds: intervalMs,
      onResult: setStats,
      // Keep the last good reading; a single failed poll must not blank the header.
      onError: () => undefined,
      maxConsecutiveErrors: 5,
    });
    void poller.start();
    return () => { void poller.stop(); };
  }, [intervalMs]);

  return { stats };
}
