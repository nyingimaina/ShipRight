import { useCallback, useEffect, useRef, useState } from 'react';
import { api, sseUrl } from '@/shared/ApiService';
import type { StreamLine } from '@/shared/components/StreamLog/StreamLog';

export type ServerAction = 'bootstrap' | 'deploy';
export type OpOutcome = 'ok' | 'error' | null;

const MAX_LINES = 5000;

/** Starts a prepare/deploy op on a server and streams its output over SSE. */
export function useServerOperation(serverId?: string) {
  const [lines, setLines] = useState<StreamLine[]>([]);
  const [running, setRunning] = useState(false);
  const [action, setAction] = useState<ServerAction | null>(null);
  const [outcome, setOutcome] = useState<OpOutcome>(null);
  const esRef = useRef<EventSource | null>(null);
  const idRef = useRef(0);

  useEffect(() => () => { esRef.current?.close(); }, []);

  const push = (kind: NonNullable<StreamLine['kind']>, text: string) =>
    setLines(prev => {
      const next = [...prev, { id: idRef.current++, kind, text }];
      return next.length > MAX_LINES ? next.slice(next.length - MAX_LINES) : next;
    });

  // A ref, not state: repeat clicks in the same tick must be dropped before React re-renders.
  const startedRef = useRef(false);
  const finish = (result: OpOutcome) => { startedRef.current = false; setOutcome(result); setRunning(false); };

  const start = useCallback(async (act: ServerAction) => {
    if (!serverId || startedRef.current) return;
    startedRef.current = true;
    setRunning(true);
    setAction(act);
    setOutcome(null);
    setLines([]);
    try {
      const { opId } = await api.post<{ opId: string }>(`/api/servers/${serverId}/${act}`, {});
      const es = new EventSource(sseUrl(`/api/servers/${serverId}/ssh/ops/${opId}/stream`));
      esRef.current = es;
      es.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data);
          if (data.type === 'log') {
            push('log', data.data?.line ?? '');
          } else if (data.type === 'done') {
            const ok = (data.data?.exitCode ?? 0) === 0;
            push(ok ? 'ok' : 'error', `${ok ? '✓' : '✗'} ${data.data?.message ?? (ok ? 'Done' : 'Failed')}`);
            finish(ok ? 'ok' : 'error');
            es.close();
          } else if (data.type === 'error') {
            push('error', `✗ ${data.data?.message ?? 'Server error'}`);
            finish('error');
            es.close();
          }
        } catch { /* ignore malformed */ }
      };
      es.onerror = () => {
        push('error', '✗ Connection lost');
        finish('error');
        es.close();
      };
    } catch (e: unknown) {
      push('error', `✗ ${(e as { message?: string })?.message ?? 'Failed to start'}`);
      finish('error');
    }
  }, [serverId]);

  const clear = useCallback(() => { setLines([]); setOutcome(null); }, []);

  return { lines, running, action, outcome, start, clear };
}
