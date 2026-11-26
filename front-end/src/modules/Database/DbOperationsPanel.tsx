import { useEffect, useRef, useState } from 'react';
import dynamic from 'next/dynamic';
import ZestButton from 'jattac.libs.web.zest-button';
import AppTabs, { ZestTabItem } from '@/shared/components/AppTabs';
import BackupFiles from '@/modules/Backups/BackupFiles';
import LogViewer from '@/modules/BuildWizard/LogViewer';
import { api, sseUrl } from '@/shared/ApiService';
import { useElapsedTimer, fmtElapsed } from '@/shared/hooks/useElapsedTimer';
import { IDatabaseConfig } from '@/shared/types/IProject';
import styles from './DbOperationsPanel.module.css';

const SqlQueryPanel = dynamic(() => import('./SqlQueryPanel'), { ssr: false });

interface Props {
  apiBase: string;
  dbConfig: IDatabaseConfig;
  /** Only project panels have one; standalone server panels keep their own backup folder. */
  projectId?: string;
}

type OpStatus = 'idle' | 'running' | 'success' | 'error';

function toLogEntries(lines: string[]) {
  return lines.map((line, i) => ({ id: i, source: 'shipright' as const, line }));
}

export default function DbOperationsPanel({ apiBase, dbConfig, projectId }: Props) {
  const [activeTab, setActiveTab] = useState<string>('backup');
  const [dbRunning, setDbRunning] = useState(false);
  const [dbStatus, setDbStatus] = useState<OpStatus>('idle');
  const [dbLogs, setDbLogs] = useState<string[]>([]);
  const [dbMessage, setDbMessage] = useState('');
  const [revision, setRevision] = useState(0);
  const esRef = useRef<EventSource | null>(null);
  const elapsed = useElapsedTimer(dbStatus === 'running');

  const refresh = () => setRevision(r => r + 1);

  useEffect(() => {
    // Reconnect to an operation that was already in progress (e.g. after a page refresh).
    api.get<{ opId: string }>(
      `${apiBase}/ops/active?container=${encodeURIComponent(dbConfig.containerName)}&database=${encodeURIComponent(dbConfig.databaseName)}`
    )
      .then(res => {
        if (res?.opId) {
          setDbStatus('running');
          setDbRunning(true);
          setDbLogs(['Reconnecting to in-progress operation…']);
          subscribeToOp(res.opId, refresh);
        }
      })
      .catch(() => {});
    return () => { esRef.current?.close(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [apiBase]);

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

  const startBackup = async () => {
    setDbLogs([]);
    setDbStatus('running');
    setDbMessage('');
    setDbRunning(true);
    esRef.current?.close();
    try {
      const res = await api.post<{ opId: string }>(`${apiBase}/backup`, dbConfig);
      subscribeToOp(res.opId, refresh);
    } catch (e: unknown) {
      setDbStatus('error');
      setDbMessage((e as { message?: string })?.message ?? 'Failed to start the backup.');
      setDbRunning(false);
    }
  };

  const tabItems: ZestTabItem<string>[] = [
    { label: 'Backup & Restore', value: 'backup' },
    { label: 'SQL Script Runner', value: 'sql' },
  ];

  return (
    <div>
      <AppTabs
        id="db-ops"
        items={tabItems}
        activeValue={activeTab}
        onChange={setActiveTab}
      />

      {activeTab === 'backup' && (
        <>
          <div className={`${styles.dbOpSection}${dbStatus === 'running' ? ' alive' : ''}`}>
            <div className={styles.buildActions} style={{ alignItems: 'center' }}>
              <ZestButton
                zest={{ visualOptions: { variant: 'standard' }, semanticType: 'submit' }}
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

          <div style={{ marginTop: 16 }}>
            <h3 className={styles.sectionTitle}>Backups</h3>
            <BackupFiles
              key={revision}
              projectId={projectId ?? 'standalone'}
              apiBase={apiBase}
              retainCount={dbConfig.backupRetainCount}
              retainDays={dbConfig.backupRetainDays ?? 0}
              queryParams={projectId ? undefined : {
                container: dbConfig.containerName,
                database: dbConfig.databaseName,
                provider: dbConfig.provider,
                rootUser: dbConfig.rootUser,
              }}
              extraRestoreBody={projectId ? undefined : { dbConfig }}
              onChanged={refresh}
              onRestoreStarted={opId => subscribeToOp(opId, refresh)}
            />
          </div>
        </>
      )}

      {activeTab === 'sql' && (
        <SqlQueryPanel apiBase={apiBase} />
      )}
    </div>
  );
}
