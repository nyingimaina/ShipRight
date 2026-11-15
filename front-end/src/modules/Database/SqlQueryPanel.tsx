import { useState, useRef, useEffect } from 'react';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { vscDarkPlus } from 'react-syntax-highlighter/dist/esm/styles/prism';
import ZestButton from 'jattac.libs.web.zest-button';
import ResponsiveTable from 'jattac.libs.web.responsive-table';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { api, sseUrl } from '@/shared/ApiService';
import { useElapsedTimer, fmtElapsed } from '@/shared/hooks/useElapsedTimer';
import { formatBytes } from './formatBytes';
import styles from './Styles/SqlQueryPanel.module.css';

interface Props {
  /** API base path for DB operations, e.g. `/api/projects/{id}/db` or `/api/servers/{serverId}/db` */
  apiBase: string;
}

interface TableData { headers: string[]; rows: string[][]; }
type InputMode = 'file' | 'text';
type Phase = 'pick' | 'preview' | 'running' | 'done';

function tryParseTable(lines: string[]): TableData | null {
  const data = lines.filter(l => l.includes('\t'));
  if (data.length < 2) return null;
  return { headers: data[0].split('\t'), rows: data.slice(1).map(l => l.split('\t')) };
}

export default function SqlQueryPanel({ apiBase }: Props) {
  const [inputMode, setInputMode] = useState<InputMode>('text');
  const [phase, setPhase] = useState<Phase>('pick');
  const [sqlPath, setSqlPath] = useState('');
  const [sqlContent, setSqlContent] = useState('');
  const [rawSql, setRawSql] = useState('');
  const [loadingPreview, setLoadingPreview] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  /** Set when the file is over the preview limit: the person is asked whether to run it without a preview. */
  const [tooLarge, setTooLarge] = useState<{ sizeBytes: number } | null>(null);
  /** How far the upload, then the run, has got. Null when nothing is being measured. */
  const [progress, setProgress] = useState<{ stage: 'upload' | 'run'; percent: number; doneBytes: number; totalBytes: number } | null>(null);
  const [opId, setOpId] = useState<string | null>(null);
  const [logs, setLogs] = useState<string[]>([]);
  const [outputLines, setOutputLines] = useState<string[]>([]);
  const [tableData, setTableData] = useState<TableData | null>(null);
  const [resultStatus, setResultStatus] = useState<'success' | 'error' | null>(null);
  const [resultMessage, setResultMessage] = useState('');
  const logsEndRef = useRef<HTMLDivElement>(null);
  const esRef = useRef<EventSource | null>(null);
  const collectedRef = useRef<string[]>([]);

  const isRunning = phase === 'running';
  const elapsed = useElapsedTimer(isRunning);

  useEffect(() => {
    logsEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [logs]);

  useEffect(() => {
    return () => { esRef.current?.close(); };
  }, []);

  const subscribeToStream = (id: string) => {
    const es = new EventSource(sseUrl(`${apiBase}/ops/${id}/stream`));
    esRef.current = es;
    es.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data);
        if (data.type === 'log') {
          setLogs(prev => [...prev, data.data.message]);
        } else if (data.type === 'progress') {
          setProgress(data.data);
        } else if (data.type === 'output') {
          collectedRef.current.push(data.data.line);
          setOutputLines(prev => [...prev, data.data.line]);
        } else if (data.type === 'complete') {
          const table = tryParseTable(collectedRef.current);
          setTableData(table);
          setResultStatus('success');
          setResultMessage(table ? '' : 'Query executed successfully.');
          setProgress(null);
          setPhase('done');
          es.close();
        } else if (data.type === 'error') {
          setResultStatus('error');
          setResultMessage(data.data.message ?? 'Query failed.');
          setProgress(null);
          setPhase('done');
          es.close();
        }
      } catch { /* ignore malformed */ }
    };
    es.onerror = () => {
      if (es.readyState === EventSource.CLOSED) {
        setResultStatus('error');
        setResultMessage('Connection lost.');
        setProgress(null);
        setPhase('done');
        es.close();
      }
    };
  };

  const prepareRun = () => {
    setLogs([]);
    setOutputLines([]);
    setTableData(null);
    setResultStatus(null);
    setResultMessage('');
    collectedRef.current = [];
    setProgress(null);
    setPhase('running');
  };

  const handleFilePick = async (path: string) => {
    setSqlPath(path);
    setPreviewError(null);
    setTooLarge(null);
    setLoadingPreview(true);
    setSqlContent('');
    try {
      const content = await api.getRaw(`/api/fs/read?path=${encodeURIComponent(path)}`);
      setSqlContent(content);
      setPhase('preview');
    } catch (e: unknown) {
      const err = e as { message?: string; tooLarge?: boolean; sizeBytes?: number };
      if (err?.tooLarge && typeof err.sizeBytes === 'number') {
        setTooLarge({ sizeBytes: err.sizeBytes });   // not an error: ask whether to run it without a preview
        setPhase('preview');
      } else {
        setPreviewError(err?.message ?? 'Failed to read file.');
      }
    } finally {
      setLoadingPreview(false);
    }
  };

  const handleExecuteFile = async () => {
    prepareRun();
    try {
      const res = await api.post<{ opId: string }>(`${apiBase}/query`, {
        localSqlPath: sqlPath,
      });
      setOpId(res.opId);
      subscribeToStream(res.opId);
    } catch (e: unknown) {
      setResultStatus('error');
      setResultMessage((e as { message?: string })?.message ?? 'Failed to start query.');
      setPhase('done');
    }
  };

  const handleExecuteRaw = async () => {
    if (!rawSql.trim()) return;
    prepareRun();
    try {
      const res = await api.post<{ opId: string }>(`${apiBase}/query-raw`, {
        sql: rawSql,
      });
      setOpId(res.opId);
      subscribeToStream(res.opId);
    } catch (e: unknown) {
      setResultStatus('error');
      setResultMessage((e as { message?: string })?.message ?? 'Failed to start query.');
      setPhase('done');
    }
  };

  const reset = () => {
    esRef.current?.close();
    setSqlPath('');
    setSqlContent('');
    setTooLarge(null);
    setProgress(null);
    setLogs([]);
    setOutputLines([]);
    setTableData(null);
    collectedRef.current = [];
    setOpId(null);
    setResultStatus(null);
    setPhase('pick');
  };

  const isRunningOrDone = phase === 'running' || phase === 'done';

  // Build column definitions for ResponsiveTable from parsed TSV headers
  const tableCols = tableData?.headers.map((h, i) => ({
    columnId: `col_${i}`,
    displayLabel: h,
    cellRenderer: (row: string[]) => row[i] ?? '',
  })) ?? [];

  return (
    <div className={`${styles.panel}${isRunning ? ' alive' : ''}`}>
      {/* Mode tabs — shown only when picking input */}
      {phase === 'pick' && (
        <div className={styles.modeTabs}>
          <button
            className={`${styles.modeTab} ${inputMode === 'text' ? styles.modeTabActive : ''}`}
            onClick={() => setInputMode('text')}>
            Type SQL
          </button>
          <button
            className={`${styles.modeTab} ${inputMode === 'file' ? styles.modeTabActive : ''}`}
            onClick={() => setInputMode('file')}>
            From file
          </button>
        </div>
      )}

      {/* Text input mode */}
      {phase === 'pick' && inputMode === 'text' && (
        <>
          <textarea
            className={styles.sqlInput}
            value={rawSql}
            onChange={e => setRawSql(e.target.value)}
            placeholder={'SELECT * FROM users LIMIT 10;'}
            spellCheck={false}
          />
          <div className={styles.actions}>
            <ZestButton
              onClick={handleExecuteRaw}
              zest={{ visualOptions: { variant: 'standard' } }}
              disabled={!rawSql.trim()}>
              Execute
            </ZestButton>
          </div>
        </>
      )}

      {/* File input mode */}
      {phase === 'pick' && inputMode === 'file' && (
        <>
          <FilePicker storageKey="sql-query" label="Select a .sql file to execute" onSelect={handleFilePick} />
          {loadingPreview && <p className={styles.hint}>Loading preview…</p>}
          {previewError && <p className={styles.error}>{previewError}</p>}
        </>
      )}

      {/* File preview phase */}
      {phase === 'preview' && (
        <>
          <div className={styles.fileHeader}>
            <span className={styles.fileName}>{sqlPath.split(/[\\/]/).pop()}</span>
            <ZestButton onClick={reset} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
              Change file
            </ZestButton>
          </div>
          {tooLarge ? (
            <>
              <div className={styles.bigFile} role="alert">
                <p className={styles.bigFileTitle}>Run it without a preview?</p>
                <p className={styles.bigFileText}>
                  This file is {formatBytes(tooLarge.sizeBytes)}, which is too large to preview here. ShipRight sends the whole file to the
                  server and runs it against {'the database'} as it is. Make sure it is the file you meant: running it changes the data,
                  and a script this size can take a while.
                </p>
              </div>
              <div className={styles.actions}>
                <ZestButton onClick={handleExecuteFile} zest={{ semanticType: 'approve', busyOptions: { handleInternally: false } }}>
                  Run without preview
                </ZestButton>
                <ZestButton onClick={reset} zest={{ semanticType: 'cancel', buttonStyle: 'outline', busyOptions: { handleInternally: false } }}>
                  Choose another file
                </ZestButton>
              </div>
            </>
          ) : (
            <>
              <div className={styles.preview}>
                <SyntaxHighlighter
                  language="sql"
                  style={vscDarkPlus}
                  customStyle={{ margin: 0, borderRadius: 8, fontSize: 13, maxHeight: 400, overflow: 'auto' }}
                  showLineNumbers
                >
                  {sqlContent}
                </SyntaxHighlighter>
              </div>
              <div className={styles.actions}>
                <ZestButton onClick={handleExecuteFile} zest={{ visualOptions: { variant: 'standard' } }}>
                  Execute
                </ZestButton>
                <ZestButton onClick={reset} zest={{ buttonStyle: 'outline' }}>
                  Cancel
                </ZestButton>
              </div>
            </>
          )}
        </>
      )}

      {/* Running / done */}
      {isRunningOrDone && (
        <>
          {/* The file being run stays in view the whole time */}
          {sqlPath && (
            <div className={styles.fileHeader}>
              <span className={styles.fileName}>{sqlPath.split(/[\\/]/).pop()}</span>
            </div>
          )}
          {/* Elapsed timer — visible while running */}
          {isRunning && (
            <div className="elapsedBar">
              <span className="elapsedDot" />
              <span className="elapsedTime">{fmtElapsed(elapsed)}</span>
              <span>running query…</span>
            </div>
          )}

          {/* How far the upload, then the run, has got */}
          {isRunning && progress && (
            <div className={styles.progress}>
              <div className={styles.progressLabel}>
                <span>{progress.stage === 'upload' ? 'Uploading' : 'Running'}</span>
                <span>{progress.percent}% · {formatBytes(progress.doneBytes)} of {formatBytes(progress.totalBytes)}</span>
              </div>
              <progress
                className={styles.progressBar}
                aria-label={progress.stage === 'upload' ? 'Uploading the script' : 'Running the script'}
                aria-valuenow={progress.percent}
                value={progress.percent}
                max={100}
              />
            </div>
          )}

          {/* Log: shown while running; hidden on SELECT success when table is rendered */}
          {(phase === 'running' || resultStatus === 'error' || !tableData) && (
            <div className={`${styles.logBox}${isRunning ? ` ${styles.logBoxRunning}` : ''}`}>
              {logs.length === 0 && isRunning && (
                <span className={styles.logWaiting}>Waiting for output…</span>
              )}
              {logs.map((line, i) => <div key={i} className={styles.logLine}>{line}</div>)}
              {phase === 'done' && !tableData && outputLines.map((line, i) => (
                <div key={`o${i}`} className={styles.logLine}>{line}</div>
              ))}
              <div ref={logsEndRef} />
            </div>
          )}

          {/* Table result via ResponsiveTable */}
          {phase === 'done' && tableData && (
            <>
              <div className={styles.tableWrap}>
                <ResponsiveTable
                  columnDefinitions={tableCols}
                  data={tableData.rows}
                  animationProps={{ animateOnLoad: true }}
                />
              </div>
              <p className={styles.rowCount}>
                {tableData.rows.length} row{tableData.rows.length !== 1 ? 's' : ''}
              </p>
            </>
          )}

          {resultStatus === 'success' && resultMessage && (
            <p className={styles.success}>{resultMessage}</p>
          )}
          {resultStatus === 'error' && <p className={styles.error}>{resultMessage}</p>}
          {phase === 'done' && (
            <ZestButton onClick={reset} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
              Run another query
            </ZestButton>
          )}
        </>
      )}
    </div>
  );
}
