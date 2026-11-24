import { useEffect, useRef, useState } from 'react';
import Poller from 'jattac-web-poller';
import { api } from '@/shared/ApiService';
import type { IHostSwap } from './IHostSwap';

/** Polls the metal host's swap counters; the create action is separate and Admin-only. */
export function useHostSwap(intervalMs = 15_000) {
  const [swap, setSwap] = useState<IHostSwap | null>(null);
  const pollerRef = useRef<Poller<IHostSwap> | null>(null);

  useEffect(() => {
    const poller = new Poller<IHostSwap>({
      func: () => api.get<IHostSwap>('/api/system/host-swap'),
      pollIntervalMilliseconds: intervalMs,
      onResult: setSwap,
      // Keep the last good reading; a single failed poll must not blank the header.
      onError: () => undefined,
      maxConsecutiveErrors: 5,
    });
    pollerRef.current = poller;
    void poller.start();
    return () => {
      pollerRef.current = null;
      void poller.stop();
    };
  }, [intervalMs]);

  return { swap, refresh: () => pollerRef.current?.pollNow() };
}
