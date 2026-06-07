import { useEffect, useRef, useState } from 'react';

export function useElapsedTimer(active: boolean): number {
  const [elapsed, setElapsed] = useState(0);
  const ref = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => {
    if (active) {
      setElapsed(0);
      ref.current = setInterval(() => setElapsed(e => e + 1), 1000);
    } else {
      if (ref.current) { clearInterval(ref.current); ref.current = null; }
    }
    return () => { if (ref.current) clearInterval(ref.current); };
  }, [active]);

  return elapsed;
}

export function fmtElapsed(s: number): string {
  const m = Math.floor(s / 60).toString().padStart(2, '0');
  const sec = (s % 60).toString().padStart(2, '0');
  return `${m}:${sec}`;
}
