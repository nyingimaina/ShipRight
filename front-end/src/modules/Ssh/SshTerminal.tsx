import { useEffect, useRef, useState, KeyboardEvent } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import { api, sseUrl } from '@/shared/ApiService';
import { useElapsedTimer, fmtElapsed } from '@/shared/hooks/useElapsedTimer';
import styles from './Styles/SshTerminal.module.css';

interface Props {
  projectId: string;
  serverLabel: string;
}

interface OutputLine {
  id: number;
  kind: 'cmd' | 'stdout' | 'stderr' | 'done' | 'error';
  text: string;
}

export default function SshTerminal({ projectId, serverLabel }: Props) {
  const [command, setCommand]       = useState('');
  const [lines, setLines]           = useState<OutputLine[]>([]);
  const [running, setRunning]       = useState(false);
  const [history, setHistory]       = useState<string[]>([]);
  const [histIdx, setHistIdx]       = useState(-1);
  const esRef   = useRef<EventSource | null>(null);
  const endRef  = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const lineId  = useRef(0);
  const elapsed = useElapsedTimer(running);

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [lines]);

  useEffect(() => () => { esRef.current?.close(); }, []);

  const pushLine = (kind: OutputLine['kind'], text: string) =>
    setLines(prev => [...prev, { id: lineId.current++, kind, text }]);

  const run = async () => {
    const cmd = command.trim();
    if (!cmd || running) return;

    setRunning(true);
    setCommand('');
    setHistIdx(-1);
    setHistory(prev => [cmd, ...prev.filter(h => h !== cmd)].slice(0, 50));
    pushLine('cmd', `$ ${cmd}`);

    try {
      const { opId } = await api.post<{ opId: string }>(
        `/api/projects/${projectId}/ssh/exec`, { command: cmd });

      const es = new EventSource(sseUrl(`/api/projects/${projectId}/ssh/ops/${opId}/stream`));
      esRef.current = es;

      es.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data);
          if (data.type === 'log') {
            const src = data.data?.source === 'stderr' ? 'stderr' : 'stdout';
            pushLine(src, data.data?.line ?? '');
          } else if (data.type === 'done') {
            const code = data.data?.exitCode ?? 0;
            pushLine(code === 0 ? 'done' : 'error',
              code === 0 ? `✓ exit 0` : `✗ exit ${code}`);
            setRunning(false);
            es.close();
          } else if (data.type === 'error') {
            pushLine('error', `✗ ${data.data?.message ?? 'SSH error'}`);
            setRunning(false);
            es.close();
          }
        } catch { /* ignore malformed */ }
      };

      es.onerror = () => {
        pushLine('error', '✗ Connection lost');
        setRunning(false);
        es.close();
      };
    } catch (e: unknown) {
      pushLine('error', `✗ ${(e as { message?: string })?.message ?? 'Failed to start'}`);
      setRunning(false);
    }
  };

  const handleKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') { run(); return; }
    if (e.key === 'ArrowUp') {
      e.preventDefault();
      const next = Math.min(histIdx + 1, history.length - 1);
      setHistIdx(next);
      setCommand(history[next] ?? '');
    }
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      const next = Math.max(histIdx - 1, -1);
      setHistIdx(next);
      setCommand(next === -1 ? '' : history[next]);
    }
  };

  return (
    <div className={styles.terminal}>
      <div className={`${styles.output} ${running ? styles.outputRunning : ''}`}
           onClick={() => inputRef.current?.focus()}>
        {lines.length === 0 && (
          <span className={styles.empty}>
            Connected to {serverLabel}. Type a command and press Enter.
          </span>
        )}
        {lines.map(l => (
          <span key={l.id} className={`${styles.line} ${lineClass(l.kind)}`}>
            {l.text}
          </span>
        ))}
        {running && <span className={`${styles.line} ${styles.lineStdout}`}>▌</span>}
        <div ref={endRef} />
      </div>

      <div className={styles.inputRow}>
        <span className={styles.prompt}>$</span>
        <input
          ref={inputRef}
          className={styles.input}
          value={command}
          onChange={e => setCommand(e.target.value)}
          onKeyDown={handleKey}
          disabled={running}
          placeholder={running ? `running… ${fmtElapsed(elapsed)}` : 'enter command'}
          autoComplete="off"
          spellCheck={false}
        />
        <ZestButton
          onClick={run}
          disabled={!command.trim() || running}
          zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}>
          Run
        </ZestButton>
      </div>

      <div className={styles.statusBar}>
        <div className={`${styles.statusDot} ${running ? styles.statusDotRunning : ''}`} />
        <span>{running ? `Running… ${fmtElapsed(elapsed)}` : 'Ready'}</span>
        {lines.length > 0 && !running && (
          <ZestButton
            onClick={() => setLines([])}
            zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
            Clear
          </ZestButton>
        )}
      </div>

      {history.length > 0 && (
        <div className={styles.history}>
          {history.slice(0, 8).map((h, i) => (
            <button key={i} className={styles.historyChip}
              onClick={() => { setCommand(h); inputRef.current?.focus(); }}>
              {h}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function lineClass(kind: OutputLine['kind']): string {
  switch (kind) {
    case 'cmd':    return styles.lineCmd;
    case 'stderr': return styles.lineStderr;
    case 'done':   return styles.lineDone;
    case 'error':  return styles.lineDoneError;
    default:       return styles.lineStdout;
  }
}
