import { useEffect, useMemo, useRef, useState } from 'react';
import {
  RiFileCopyLine, RiDownloadLine, RiPauseLine, RiPlayLine, RiTextWrap, RiErrorWarningLine, RiDeleteBinLine,
} from 'react-icons/ri';
import styles from './StreamLog.module.css';

export interface StreamLine {
  id: number;
  text: string;
  kind?: 'log' | 'ok' | 'error';
}

interface Props {
  lines: StreamLine[];
  isLive?: boolean;
  /** Download file name. */
  filename?: string;
  emptyText?: string;
  /** When given, a Clear button is shown. */
  onClear?: () => void;
}

const ERROR_RE = /✗|\bfatal\b|\berror\b|\bfailed\b|\bdenied\b/i;
const NEAR_BOTTOM_PX = 40;

/** Copy that also works on plain http (non-secure contexts have no navigator.clipboard). */
async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch { /* fall through to the legacy path */ }
  try {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    return ok;
  } catch { return false; }
}

/**
 * Reusable streaming-output viewer: fills its parent (flex), follows new output, auto-pauses when
 * the reader scrolls up, and offers copy-all / download / filter / errors-only / wrap / clear.
 */
export default function StreamLog({ lines, isLive, filename = 'shipright.log', emptyText = 'Waiting for output…', onClear }: Props) {
  const bodyRef = useRef<HTMLDivElement>(null);
  const [paused, setPaused] = useState(false);
  // Set by the Pause button: the reader asked to stay put, so reaching the bottom must not resume.
  const [locked, setLocked] = useState(false);
  const [pausedAtCount, setPausedAtCount] = useState(0);
  const [wrap, setWrap] = useState(true);
  const [errorsOnly, setErrorsOnly] = useState(false);
  const [query, setQuery] = useState('');
  const [copied, setCopied] = useState(false);

  const autoPause = () => { setPaused(true); setPausedAtCount(lines.length); };
  const pause = () => { autoPause(); setLocked(true); };
  const resume = () => { setPaused(false); setLocked(false); };

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase();
    return lines.filter(l =>
      (!q || l.text.toLowerCase().includes(q)) &&
      (!errorsOnly || l.kind === 'error' || ERROR_RE.test(l.text)));
  }, [lines, query, errorsOnly]);

  useEffect(() => {
    if (!paused && bodyRef.current) bodyRef.current.scrollTop = bodyRef.current.scrollHeight;
  }, [visible, paused]);

  // Scrolling up stops following; scrolling back to the bottom follows again unless the reader locked it.
  const onScroll = () => {
    const el = bodyRef.current;
    if (!el) return;
    const gap = el.scrollHeight - el.scrollTop - el.clientHeight;
    if (!paused) { if (gap > NEAR_BOTTOM_PX) autoPause(); }
    else if (!locked && gap <= NEAR_BOTTOM_PX) resume();
  };

  const allText = () => lines.map(l => l.text).join('\n');

  const copyAll = async () => {
    if (await copyText(allText())) {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    }
  };

  const download = () => {
    const url = URL.createObjectURL(new Blob([allText()], { type: 'text/plain' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL?.(url);
  };

  const newCount = Math.max(0, lines.length - pausedAtCount);

  return (
    <div className={styles.wrap}>
      <div className={styles.toolbar}>
        <span className={styles.status}>{isLive ? '● streaming' : `${lines.length} lines`}</span>
        <input className={styles.filter} placeholder="Filter…" value={query}
          onChange={e => setQuery(e.target.value)} aria-label="Filter log" />
        <div className={styles.buttons}>
          <button className={`${styles.iconBtn} ${paused ? styles.active : ''}`}
            onClick={paused ? resume : pause}
            aria-label={paused ? 'Resume auto-scroll' : 'Pause auto-scroll'}
            title={paused ? 'Resume auto-scroll' : 'Pause auto-scroll'}>
            {paused ? <RiPlayLine /> : <RiPauseLine />}
          </button>
          <button className={`${styles.iconBtn} ${errorsOnly ? styles.active : ''}`}
            onClick={() => setErrorsOnly(v => !v)} aria-label="Errors only" aria-pressed={errorsOnly} title="Errors only">
            <RiErrorWarningLine />
          </button>
          <button className={`${styles.iconBtn} ${wrap ? styles.active : ''}`}
            onClick={() => setWrap(v => !v)} aria-label="Wrap lines" aria-pressed={wrap} title="Wrap lines">
            <RiTextWrap />
          </button>
          <button className={styles.iconBtn} onClick={copyAll} aria-label="Copy all" title="Copy all">
            <RiFileCopyLine />{copied && <span className={styles.copied}>Copied</span>}
          </button>
          <button className={styles.iconBtn} onClick={download} aria-label="Download log" title="Download log">
            <RiDownloadLine />
          </button>
          {onClear && (
            <button className={styles.iconBtn} onClick={onClear} aria-label="Clear" title="Clear">
              <RiDeleteBinLine />
            </button>
          )}
        </div>
      </div>

      <div className={styles.bodyWrap}>
        <div className={`${styles.body} ${wrap ? styles.wrap_on : styles.wrap_off}`}
          ref={bodyRef} onScroll={onScroll} data-testid="stream-log-body">
          {visible.length === 0
            ? <span className={styles.empty}>{lines.length === 0 ? emptyText : 'No lines match the filter.'}</span>
            : visible.map(l => (
                <span key={l.id}
                  className={`${styles.line} ${l.kind === 'ok' ? styles.ok : (l.kind === 'error' || ERROR_RE.test(l.text)) ? styles.err : ''}`}>
                  {l.text}
                </span>
              ))}
        </div>
        {paused && newCount > 0 && (
          <button className={styles.jump} onClick={resume}>Jump to latest ({newCount} new)</button>
        )}
      </div>
    </div>
  );
}
