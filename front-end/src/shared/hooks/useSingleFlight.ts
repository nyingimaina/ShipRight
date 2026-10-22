import { useCallback, useRef, useState } from 'react';

/**
 * Runs at most one async action at a time. Calls made while one is in flight are dropped (the guard is
 * a ref, so it holds even for several clicks before React re-renders). `busy` drives spinners/disabling.
 */
export function useSingleFlight() {
  const inFlight = useRef(false);
  const [busy, setBusy] = useState(false);

  const run = useCallback(async <T,>(work: () => Promise<T>): Promise<T | undefined> => {
    if (inFlight.current) return undefined;
    inFlight.current = true;
    setBusy(true);
    try { return await work(); }
    finally { inFlight.current = false; setBusy(false); }
  }, []);

  return { run, busy };
}
