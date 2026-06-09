import { useEffect, useRef, useState } from 'react';
import dynamic from 'next/dynamic';
import ZestButton from 'jattac.libs.web.zest-button';
import LogViewer from '@/modules/BuildWizard/LogViewer';
import { api, sseUrl } from '@/shared/ApiService';
import { useElapsedTimer, fmtElapsed } from '@/shared/hooks/useElapsedTimer';
import { IDatabaseConfig } from '@/shared/types/IProject';
import styles from '@/pages/projects/[id]/Styles/ProjectDetail.module.css';

const SqlQueryPanel = dynamic(() => import('./SqlQueryPanel'), { ssr: false });

interface BackupInfo {
  fileName: string;
  filePath: string;
  sizeBytes: number;
  createdAt: string;
}

interface Props {
  apiBase: string;
  dbConfig: IDatabaseConfig;
}

type OpStatus = 'idle' | 'running' | 'success' | 'error';

function toLogEntries(lines: string[]) {
  return lines.map((line, i) => ({ id: i, source: 'shipright' as const, line }));
}

export default function DbOperationsPanel({ apiBase, dbConfig }: Props) {
  const [backups, setBackups] = useState<BackupInfo[]>([]);
  const [dbRunning, setDbRunning] = useState(false);
  const [dbStatus, setDbStatus] = useState<OpStatus>('idle');
  const [dbLogs, setDbLogs] = useState<string[]>([]);
  const [dbMessage, setDbMessage] = useState('');
  const [deletingBackup, setDeletingBackup] = useState<string | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState<string | null>(null);
  const [deleteCountdown, setDeleteCountdown] = useState(0);
  const deleteTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const esRef = useRef<EventSource | null>(null);
  const elapsed = useElapsedTimer(dbStatus === 'running');

  useEffect(() => {
    return () => {
      esRef.current?.close();
      if (deleteTimerRef.current) clearInterval(deleteTimerRef.current);
    };
  }, []);

  const refreshBackups = () =>
    api.get<BackupInfo[]>(`${apiBase}/backups`).catch(() => []).then(setBackups);

  const subscribeToOp = (opId: string, onDone: () => void) => {
    esRef.current?.close();
    const es = new EventSource(sseUrl(`${apiBase}/ops/${opId}/stream`));
    esRef.current = es;
    es.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data);
        if (data.type === 'log') {
          setDbLogs(prev => [...prev, data.data.message]);
        } else if (data.type === 'complete') {
          setDbStatus('success');
          setDbMessage(`Complete: ${data.data.fileName ?? data.data.status ?? ''}`);
          setDbRunning(false);
          es.close();
          onDone();
        } else if (data.type === 'error') {
          setDbStatus('error');
          setDbMessage(data.data.message ?? 'Operation failed.');
          setDbRunning(false);
          es.close();
        }
      } catch { /* ignore */ }
    };
    es.onerror = () => {
      if (es.readyState === EventSource.CLOSED) {
        setDbStatus('error');
        setDbMessage('Connection lost.');
        setDbRunning(false);
      }
    };
  };

  const startOp = async (endpoint: string, body: object, done: () => void) => {
    setDbLogs([]);
    setDbStatus('running');
    setDbMessage('');
    setDbRunning(true);
    esRef.current?.close();
    try {
      const res = await api.post<{ opId: string }>(`${apiBase}/${endpoint}`, body);
      subscribeToOp(res.opId, done);
    } catch (e: unknown) {
      setDbStatus('error');
      setDbMessage((e as { message?: string })?.message ?? 'Failed to start operation.');
      setDbRunning(false);
    }
  };

  const startBackup = () => startOp('backup', dbConfig, () => refreshBackups());

  const startRestore = (filePath: string) =>
    startOp('restore', { dbConfig, backupFile: filePath }, () => {});

  const deleteBackup = async (filePath: string) => {
    setDeletingBackup(filePath);
    try {
      await api.delete(
        `${apiBase}/backups?file=${encodeURIComponent(filePath)}` +
        `&container=${encodeURIComponent(dbConfig.containerName)}` +
        `&database=${encodeURIComponent(dbConfig.databaseName)}` +
        `&provider=${dbConfig.provider}` +
        `&rootUser=${encodeURIComponent(dbConfig.rootUser)}` +
        `&backupRetainCount=${dbConfig.backupRetainCount}`
      );
      setBackups(prev => prev.filter(b => b.filePath !== filePath));
    } catch {
      /* ignore */
    } finally {
      setDeletingBackup(null);
      setConfirmingDelete(null);
    }
  };

  const startDeleteCountdown = (filePath: string) => {
    setConfirmingDelete(filePath);
    setDeleteCountdown(3);
    let n = 3;
    deleteTimerRef.current = setInterval(() => {
      n -= 1;
      setDeleteCountdown(n);
      if (n <= 0) {
        clearInterval(deleteTimerRef.current!);
        deleteTimerRef.current = null;
        deleteBackup(filePath);
      }
    }, 1000);
  };

  const cancelDelete = () => {
    if (deleteTimerRef.current) { clearInterval(deleteTimerRef.current); deleteTimerRef.current = null; }
    setConfirmingDelete(null);
    setDeleteCountdown(0);
  };

  return (
    <div>
      {/* Backup action + live feedback */}
      <div className={`${styles.dbOpSection}${dbStatus === 'running' ? ' alive' : ''}`}>
        <div className={styles.buildActions} style={{ alignItems: 'center' }}>
          <ZestButton
            zest={{ visualOptions: { variant: 'standard' } }}
            onClick={startBackup}
            disabled={dbRunning}>
            {dbRunning ? 'Running…' : 'Backup Now'}
          </ZestButton>
        </div>

        {dbStatus === 'running' && (
          <div className="elapsedBar">
            <span className="elapsedDot" />
            <span className="elapsedTime">{fmtElapsed(elapsed)}</span>
            <span>operation in progress…</span>
          </div>
        )}

        {(dbStatus === 'running' || dbLogs.length > 0) && (
          <LogViewer lines={toLogEntries(dbLogs)} isLive={dbStatus === 'running'} />
        )}
        {dbStatus === 'success' && <p style={{ color: '#3D9970', fontSize: 13, marginTop: 8 }}>{dbMessage}</p>}
        {dbStatus === 'error'   && <p style={{ color: '#B84040', fontSize: 13, marginTop: 8 }}>{dbMessage}</p>}
      </div>

      {/* Backup list */}
      {backups.length > 0 && (
        <div style={{ marginTop: 16 }}>
          <h3 className={styles.sectionTitle}>Backups</h3>
          {backups.map(b => (
            <div key={b.filePath} style={{ display: 'flex', alignItems: 'center', gap: 12,
              padding: '8px 0', borderBottom: '1px solid rgba(255,255,255,0.05)', flexWrap: 'wrap' }}>
              <span style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12, color: '#C9A84C', flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {b.fileName}
              </span>
              <span style={{ fontSize: 12, color: '#637389', flexShrink: 0 }}>
                {(b.sizeBytes / 1024).toFixed(1)} KB
              </span>
              <ZestButton
                zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                onClick={() => startRestore(b.filePath)}>
                Restore
              </ZestButton>
              {confirmingDelete === b.filePath ? (
                <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                  <span style={{ fontSize: 12, color: '#B84040', fontFamily: "'JetBrains Mono', monospace" }}>
                    {deletingBackup === b.filePath ? 'Deleting…' : `Deleting in ${deleteCountdown}s`}
                  </span>
                  {!deletingBackup && (
                    <ZestButton
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                      onClick={cancelDelete}>
                      Cancel
                    </ZestButton>
                  )}
                </div>
              ) : (
                <ZestButton
                  zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' }, semanticType: 'delete' }}
                  onClick={() => startDeleteCountdown(b.filePath)}
                  disabled={!!deletingBackup}>
                  Delete
                </ZestButton>
              )}
            </div>
          ))}
        </div>
      )}

      {/* SQL Query */}
      <div style={{ marginTop: 20 }}>
        <h3 className={styles.sectionTitle}>Run SQL Query</h3>
        <SqlQueryPanel apiBase={apiBase} />
      </div>
    </div>
  );
}