import { useEffect, useRef, useState, KeyboardEvent, ClipboardEvent } from 'react';
import { api, sseUrl } from '@/shared/ApiService';
import { useElapsedTimer, fmtElapsed } from '@/shared/hooks/useElapsedTimer';
import StreamLog, { StreamLine } from '@/shared/components/StreamLog/StreamLog';
import { presetsFor } from './presets';
import styles from './RemoteTerminal.module.css';

interface Props {
  /** Run on a project's server. */
  projectId?: string;
  /** Run on a registered server. */
  serverId?: string;
  serverLabel: string;
  /** Compose folder on the server; enables folder-based quick commands. */
  startDir?: string;
}

const HISTORY_KEY = 'shipright.terminal.history';
const MAX_LINES = 5000;

function apiBase(projectId?: string, serverId?: string): string | null {
  if (serverId) return `/api/servers/${serverId}`;
  if (projectId) return `/api/projects/${projectId}`;
  return null;
}

function loadHistory(): string[] {
  try {
    const parsed = JSON.parse(localStorage.getItem(HISTORY_KEY) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter(x => typeof x === 'string').slice(0, 50) : [];
  } catch { return []; }
}

function saveHistory(h: string[]) {
  try { localStorage.setItem(HISTORY_KEY, JSON.stringify(h)); } catch { /* storage unavailable */ }
}

/**
 * The one SSH terminal used for both servers and projects: one-command-at-a-time with a remembered working
 * directory, quick commands for common checks, persisted history, and the shared log toolbar for the output.
 */
export default function RemoteTerminal({ projectId, serverId, serverLabel, startDir }: Props) {
  const base = apiBase(projectId, serverId);
  const presets = presetsFor(startDir);

  const [command, setCommand] = useState('');
  const [lines, setLines]     = useState<StreamLine[]>([]);
  const [running, setRunning] = useState(false);
  const [cwd, setCwd]         = useState('~');
  const [history, setHistory] = useState<string[]>(loadHistory);
  const [histIdx, setHistIdx] = useState(-1);
  const esRef     = useRef<EventSource | null>(null);
  const inputRef  = useRef<HTMLInputElement>(null);
  const lineId    = useRef(0);
  const busyRef   = useRef(false);
  const elapsed   = useElapsedTimer(running);

  useEffect(() => () => { esRef.current?.close(); }, []);

  const push = (text: string, kind?: StreamLine['kind']) =>
    setLines(prev => [...prev, { id: lineId.current++, text, kind }].slice(-MAX_LINES));

  const finish = () => { busyRef.current = false; setRunning(false); };

  const fill = (text: string) => { setCommand(text); setHistIdx(-1); inputRef.current?.focus(); };

  const onPaste = (e: ClipboardEvent<HTMLInputElement>) => {
    const pasted = e.clipboardData.getData('text');
    if (!pasted.includes('\n')) return;
    e.preventDefault();
    const joined = pasted.trim().split(/\r?\n/).map(s => s.trim()).filter(Boolean).join(' && ');
    if (joined) setCommand(joined);
  };

  const run = async () => {
    const cmd = command.trim();
    if (!cmd || busyRef.current || !base) return;
    busyRef.current = true;
    setRunning(true);
    setCommand('');
    setHistIdx(-1);
    const nextHistory = [cmd, ...history.filter(h => h !== cmd)].slice(0, 50);
    setHistory(nextHistory);
    saveHistory(nextHistory);
    push(`$ ${cmd}`);

    try {
      const { opId } = await api.post<{ opId: string }>(`${base}/ssh/exec`, { command: cmd });
      const es = new EventSource(sseUrl(`${base}/ssh/ops/${opId}/stream`));
      esRef.current = es;

      es.onmessage = (event) => {
        try {
          const msg = JSON.parse(event.data);
          if (msg.type === 'cwd') {
            setCwd(msg.data?.cwd ?? '~');
          } else if (msg.type === 'log') {
            push(msg.data?.line ?? '');
          } else if (msg.type === 'done') {
            const code = msg.data?.exitCode ?? 0;
            if (msg.data?.cwd) setCwd(msg.data.cwd);
            push(code === 0 ? '✓ exit 0' : `✗ exit ${code}`, code === 0 ? 'ok' : 'error');
            finish();
            es.close();
          } else if (msg.type === 'error') {
            push(`✗ ${msg.data?.message ?? 'SSH error'}`, 'error');
            finish();
            es.close();
          }
        } catch { /* ignore malformed */ }
      };
      es.onerror = () => {
        push('✗ Connection lost', 'error');
        finish();
        es.close();
      };
    } catch (e: unknown) {
      push(`✗ ${(e as { message?: string })?.message ?? 'Failed to start'}`, 'error');
      finish();
    }
  };

  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') { run(); return; }
    if (e.key === 'ArrowUp') {
      e.preventDefault();
      const next = Math.min(histIdx + 1, history.length - 1);
      setHistIdx(next);
      setCommand(history[next] ?? '');
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      const next = Math.max(histIdx - 1, -1);
      setHistIdx(next);
      setCommand(next === -1 ? '' : history[next]);
    }
  };

  return (
    <div className={styles.terminal}>
      <div className={styles.presets} aria-label="Quick commands">
        <span className={styles.presetsLabel}>Quick commands</span>
        {presets.map(p => (
          <button key={p.label} type="button" className={styles.chip} title={`${p.hint}\n\n${p.resolved}`}
            onClick={() => fill(p.resolved)} disabled={running}>
            {p.label}
          </button>
        ))}
      </div>

      <div className={styles.output}>
        <StreamLog lines={lines} isLive={running} filename="terminal-output.log"
          emptyText={`Connected to ${serverLabel}. Type a command and press Enter, or pick a quick command above.`}
          onClear={() => setLines([])} />
      </div>

      <div className={styles.inputRow}>
        <span className={styles.prompt}>$</span>
        <input ref={inputRef} className={styles.input} aria-label="Command" value={command}
          onChange={e => setCommand(e.target.value)} onKeyDown={onKey} onPaste={onPaste}
          disabled={running} autoComplete="off" spellCheck={false}
          placeholder={running ? `running… ${fmtElapsed(elapsed)}` : 'enter command, e.g. docker ps'} />
        <button type="button" className={styles.runBtn} onClick={run} disabled={!command.trim() || running}>Run</button>
      </div>

      <div className={styles.statusBar}>
        <span className={`${styles.dot} ${running ? styles.dotRunning : ''}`} />
        <span>{running ? `Running… ${fmtElapsed(elapsed)}` : 'Ready'}</span>
        <span className={styles.cwd}>{cwd}</span>
      </div>

      {history.length > 0 && (
        <div className={styles.history} aria-label="Recent commands">
          {history.slice(0, 8).map(h => (
            <button key={h} type="button" className={styles.historyChip} onClick={() => fill(h)} disabled={running} title={h}>{h}</button>
          ))}
        </div>
      )}
    </div>
  );
}
