import { useState, useRef, useEffect } from 'react';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { vscDarkPlus } from 'react-syntax-highlighter/dist/esm/styles/prism';
import ZestButton from 'jattac.libs.web.zest-button';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { api } from '@/shared/ApiService';
import styles from './Styles/SqlQueryPanel.module.css';

interface Props {
  projectId: string;
}

type Phase = 'pick' | 'preview' | 'running' | 'done';

export default function SqlQueryPanel({ projectId }: Props) {
  const [phase, setPhase] = useState<Phase>('pick');
  const [sqlPath, setSqlPath] = useState('');
  const [sqlContent, setSqlContent] = useState('');
  const [loadingPreview, setLoadingPreview] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [opId, setOpId] = useState<string | null>(null);
  const [logs, setLogs] = useState<string[]>([]);
  const [resultStatus, setResultStatus] = useState<'success' | 'error' | null>(null);
  const [resultMessage, setResultMessage] = useState('');
  const logsEndRef = useRef<HTMLDivElement>(null);
  const esRef = useRef<EventSource | null>(null);

  useEffect(() => {
    logsEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [logs]);

  useEffect(() => {
    return () => { esRef.current?.close(); };
  }, []);

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

  const handleExecute = async () => {
    setLogs([]);
    setResultStatus(null);
    setResultMessage('');
    setPhase('running');

    try {
      const res = await api.post<{ opId: string }>(`/api/projects/${projectId}/db/query`, {
        localSqlPath: sqlPath,
      });
      const id = res.opId;
      setOpId(id);

      const es = new EventSource(`/api/projects/${projectId}/db/ops/${id}/stream`);
      esRef.current = es;

      es.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data);
          if (data.type === 'log') {
            setLogs(prev => [...prev, data.data.message]);
          } else if (data.type === 'complete') {
            setResultStatus('success');
            setResultMessage('Query executed successfully.');
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
        setResultStatus('error');
        setResultMessage('Connection lost.');
        setPhase('done');
        es.close();
      };
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
    setOpId(null);
    setResultStatus(null);
    setPhase('pick');
  };

  return (
    <div className={styles.panel}>
      {phase === 'pick' && (
        <>
          <FilePicker
            label="Select a .sql file to execute"
            onSelect={handleFilePick}
          />
          {loadingPreview && <p className={styles.hint}>Loading preview…</p>}
          {previewError && <p className={styles.error}>{previewError}</p>}
        </>
      )}

      {(phase === 'preview' || phase === 'running' || phase === 'done') && (
        <>
          <div className={styles.fileHeader}>
            <span className={styles.fileName}>{sqlPath.split(/[\\/]/).pop()}</span>
            {phase === 'preview' && (
              <ZestButton onClick={reset} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
                Change file
              </ZestButton>
            )}
          </div>

          {phase === 'preview' && (
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
                <ZestButton onClick={handleExecute} zest={{ visualOptions: { variant: 'standard' } }}>
                  Execute
                </ZestButton>
                <ZestButton onClick={reset} zest={{ buttonStyle: 'outline' }}>
                  Cancel
                </ZestButton>
              </div>
            </>
          )}

          {(phase === 'running' || phase === 'done') && (
            <>
              <div className={styles.logBox}>
                {logs.map((line, i) => (
                  <div key={i} className={styles.logLine}>{line}</div>
                ))}
                <div ref={logsEndRef} />
              </div>
              {resultStatus === 'success' && (
                <p className={styles.success}>{resultMessage}</p>
              )}
              {resultStatus === 'error' && (
                <p className={styles.error}>{resultMessage}</p>
              )}
              {phase === 'done' && (
                <ZestButton onClick={reset} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
                  Run another query
                </ZestButton>
              )}
            </>
          )}
        </>
      )}
    </div>
  );
}
