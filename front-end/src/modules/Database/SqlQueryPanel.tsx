import { useState, useRef, useEffect } from 'react';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { vscDarkPlus } from 'react-syntax-highlighter/dist/esm/styles/prism';
import ZestButton from 'jattac.libs.web.zest-button';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { api, sseUrl } from '@/shared/ApiService';
import styles from './Styles/SqlQueryPanel.module.css';

interface Props {
  projectId: string;
}

interface TableData { headers: string[]; rows: string[][]; }
type InputMode = 'file' | 'text';
type Phase = 'pick' | 'preview' | 'running' | 'done';

function tryParseTable(lines: string[]): TableData | null {
  const data = lines.filter(l => l.includes('\t'));
  if (data.length < 2) return null;
  return { headers: data[0].split('\t'), rows: data.slice(1).map(l => l.split('\t')) };
}

export default function SqlQueryPanel({ projectId }: Props) {
  const [inputMode, setInputMode] = useState<InputMode>('text');
  const [phase, setPhase] = useState<Phase>('pick');
  const [sqlPath, setSqlPath] = useState('');
  const [sqlContent, setSqlContent] = useState('');
  const [rawSql, setRawSql] = useState('');
  const [loadingPreview, setLoadingPreview] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [opId, setOpId] = useState<string | null>(null);
  const [logs, setLogs] = useState<string[]>([]);
  const [outputLines, setOutputLines] = useState<string[]>([]);
  const [tableData, setTableData] = useState<TableData | null>(null);
  const [resultStatus, setResultStatus] = useState<'success' | 'error' | null>(null);
  const [resultMessage, setResultMessage] = useState('');
  const logsEndRef = useRef<HTMLDivElement>(null);
  const esRef = useRef<EventSource | null>(null);
  const collectedRef = useRef<string[]>([]);

  useEffect(() => {
    logsEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [logs]);

  useEffect(() => {
    return () => { esRef.current?.close(); };
  }, []);

  const subscribeToStream = (id: string) => {
    const es = new EventSource(sseUrl(`/api/projects/${projectId}/db/ops/${id}/stream`));
    esRef.current = es;
    es.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data);
        if (data.type === 'log') {
          setLogs(prev => [...prev, data.data.message]);
        } else if (data.type === 'output') {
          collectedRef.current.push(data.data.line);
          setOutputLines(prev => [...prev, data.data.line]);
        } else if (data.type === 'complete') {
          const table = tryParseTable(collectedRef.current);
          setTableData(table);
          setResultStatus('success');
          setResultMessage(table ? '' : 'Query executed successfully.');
          setPhase('done');
          es.close();
        } else if (data.type === 'error') {
          setResultStatus('error');
          setResultMessage(data.data.message ?? 'Query failed.');
          setPhase('done');
          es.close();
        }
      } catch { /* ignore malformed */ }
    };
    es.onerror = () => {
      if (es.readyState === EventSource.CLOSED) {
        setResultStatus('error');
        setResultMessage('Connection lost.');
        setPhase('done');
        es.close();
      }
      // readyState CONNECTING = browser is auto-retrying; do nothing
    };
  };

  const prepareRun = () => {
    setLogs([]);
    setOutputLines([]);
    setTableData(null);
    setResultStatus(null);
    setResultMessage('');
    collectedRef.current = [];
    setPhase('running');
  };

  const handleFilePick = async (path: string) => {
    setSqlPath(path);
    setPreviewError(null);
    setLoadingPreview(true);
    setSqlContent('');
    try {
      const content = await api.getRaw(`/api/fs/read?path=${encodeURIComponent(path)}`);
      setSqlContent(content);
      setPhase('preview');
    } catch (e: unknown) {
      setPreviewError((e as { message?: string })?.message ?? 'Failed to read file.');
    } finally {
      setLoadingPreview(false);
    }
  };

  const handleExecuteFile = async () => {
    prepareRun();
    try {
      const res = await api.post<{ opId: string }>(`/api/projects/${projectId}/db/query`, {
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
      const res = await api.post<{ opId: string }>(`/api/projects/${projectId}/db/query-raw`, {
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
    setLogs([]);
    setOutputLines([]);
    setTableData(null);
    collectedRef.current = [];
    setOpId(null);
    setResultStatus(null);
    setPhase('pick');
  };

  const isRunningOrDone = phase === 'running' || phase === 'done';

  return (
    <div className={styles.panel}>
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

      {/* File input mode — pick phase */}
      {phase === 'pick' && inputMode === 'file' && (
        <>
          <FilePicker label="Select a .sql file to execute" onSelect={handleFilePick} />
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

      {/* Running / done */}
      {isRunningOrDone && (
        <>
          {/* Log: shown while running; hidden on SELECT success when table is rendered */}
          {(phase === 'running' || resultStatus === 'error' || !tableData) && (
            <div className={styles.logBox}>
              {logs.map((line, i) => <div key={i} className={styles.logLine}>{line}</div>)}
              {phase === 'done' && !tableData && outputLines.map((line, i) => (
                <div key={`o${i}`} className={styles.logLine}>{line}</div>
              ))}
              <div ref={logsEndRef} />
            </div>
          )}

          {/* Table result — replaces log on SELECT success */}
          {phase === 'done' && tableData && (
            <>
              <div className={styles.tableWrap}>
                <table className={styles.dataTable}>
                  <thead>
                    <tr>{tableData.headers.map((h, i) => <th key={i}>{h}</th>)}</tr>
                  </thead>
                  <tbody>
                    {tableData.rows.map((row, ri) => (
                      <tr key={ri}>{row.map((cell, ci) => <td key={ci}>{cell}</td>)}</tr>
                    ))}
                  </tbody>
                </table>
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
