import Head from 'next/head';
import { useEffect, useState, useCallback } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import AppShell from '@/modules/AppShell/AppShell';
import { api } from '@/shared/ApiService';
import styles from './Styles/Backups.module.css';

interface ScheduleJob {
  scheduleId: string; jobName: string; scheduleType: string;
  scheduleDescription: string; lastFiredAt: string | null;
  nextFireAt: string | null; inFlightCount: number;
  totalFired: number; totalMissed: number; lastDrift: string | null;
}

interface SchedulesResponse { jobs: ScheduleJob[]; count: number; capturedAt: string; }

interface HistoryRecord {
  id: string; projectId: string; projectName: string; databaseName: string;
  status: string; startedAt: string; completedAt: string | null;
  durationMs: number; errorMessage: string | null; backupSizeBytes: number;
  scheduleId: string; correlationId: string;
}

interface HistoryResponse { records: HistoryRecord[]; count: number; }

interface BackupSummary { totalRuns: number; successfulRuns: number; failedRuns: number; successRate: number; avgDurationMs: number; totalSizeBytes: number; }

interface SnapshotResponse {
  totalPending: number; throughputPerSecond: number; totalProcessed: number;
  totalFailures: number; healthStatus: string; isDispatcherRunning: boolean;
  pendingByTenant: Record<string, number>; capturedAt: string;
}

interface InflightResponse {
  inflight: { projectId: string; projectName: string; databaseName: string; scheduledAt: string; startedAt: string; elapsedMs: number; } | null;
}

interface OverflowRecord { projectId: string; projectName: string; failedAt: string; errorMessage: string; }
interface OverflowResponse { items: OverflowRecord[]; count: number; }

function ago(dateStr: string): string {
  const ms = Date.now() - new Date(dateStr).getTime();
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  return `${Math.floor(h / 24)}d ago`;
}

function fmtBytes(bytes: number): string {
  if (bytes === 0) return '-';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1048576).toFixed(1)} MB`;
}

function fmtMs(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
}

export default function BackupsPage() {
  const [schedules, setSchedules] = useState<ScheduleJob[]>([]);
  const [history, setHistory] = useState<HistoryRecord[]>([]);
  const [summary, setSummary] = useState<BackupSummary | null>(null);
  const [inflight, setInflight] = useState<InflightResponse['inflight']>(null);
  const [overflow, setOverflow] = useState<OverflowRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');

  const load = useCallback(async () => {
    try {
      const [s, h, sm, i, o] = await Promise.all([
        api.get<SchedulesResponse>('/api/scheduler/schedules').catch(() => ({ jobs: [], count: 0, capturedAt: '' })),
        api.get<HistoryResponse>('/api/scheduler/history?limit=50').catch(() => ({ records: [], count: 0 })),
        api.get<BackupSummary>('/api/scheduler/history/summary').catch(() => null),
        api.get<InflightResponse>('/api/scheduler/inflight').catch(() => ({ inflight: null })),
        api.get<OverflowResponse>('/api/scheduler/overflow').catch(() => ({ items: [], count: 0 })),
      ]);
      setSchedules(s.jobs);
      setHistory(h.records);
      setSummary(sm);
      setInflight(i.inflight);
      setOverflow(o.items);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  const deleteSchedule = async (scheduleId: string) => {
    await api.delete(`/api/scheduler/schedules/${scheduleId}`);
    load();
  };

  const replayOverflow = async () => {
    await api.post('/api/scheduler/replay', {});
    load();
  };

  const filteredHistory = statusFilter
    ? history.filter(r => r.status === statusFilter)
    : history;

  return (
    <>
      <Head><title>ShipRight — Backups</title></Head>
      <AppShell>
        <h1 className={styles.heading}>Automated Backups</h1>
        <p className={styles.sub}>Scheduled database backups, history, and overflow recovery.</p>

        <div className={styles.refreshRow}>
          <ZestButton onClick={load} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
            {loading ? 'Loading…' : 'Refresh'}
          </ZestButton>
        </div>

        {inflight && (
          <div className={styles.inflightBanner}>
            <span className={styles.statusDot + ' ' + styles.statusPending} />
            <div>
              <div className={styles.inflightLabel}>Backup In Progress</div>
              <div className={styles.inflightInfo}>
                {inflight.projectName} — {inflight.databaseName} ({fmtMs(inflight.elapsedMs)})
              </div>
            </div>
          </div>
        )}

        <div className={styles.cards}>
          <div className={styles.card}>
            <div className={styles.cardLabel}>Schedules</div>
            <div className={styles.cardValue}>{schedules.length}</div>
            <div className={styles.cardSub}>active cron jobs</div>
          </div>
          <div className={styles.card}>
            <div className={styles.cardLabel}>Success Rate</div>
            <div className={styles.cardValue}>{summary?.successRate ?? 100}%</div>
            <div className={styles.cardSub}>{summary?.successfulRuns ?? 0} ok / {summary?.totalRuns ?? 0} total</div>
          </div>
          <div className={styles.card}>
            <div className={styles.cardLabel}>Failed</div>
            <div className={styles.cardValue}>{summary?.failedRuns ?? 0}</div>
            <div className={styles.cardSub}>last 30 days</div>
          </div>
          <div className={styles.card}>
            <div className={styles.cardLabel}>Overflow</div>
            <div className={styles.cardValue}>{overflow.length}</div>
            <div className={styles.cardSub}>items awaiting replay</div>
          </div>
        </div>

        <div className={styles.section}>
          <div className={styles.sectionTitle}>Schedules</div>
          {schedules.length === 0 ? (
            <div className={styles.empty}>No backup schedules configured.</div>
          ) : (
            <table className={styles.table}>
              <thead>
                <tr>
                  <th>Project</th>
                  <th>Schedule</th>
                  <th>Last Fired</th>
                  <th>Next Fire</th>
                  <th>Fired</th>
                  <th>Missed</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {schedules.map(s => (
                  <tr key={s.scheduleId}>
                    <td>{s.jobName.replace('Backup-', '')}</td>
                    <td style={{ color: '#A8B8CC', fontSize: 12 }}>{s.scheduleDescription}</td>
                    <td style={{ color: '#637389', fontSize: 12 }}>{s.lastFiredAt ? ago(s.lastFiredAt) : '—'}</td>
                    <td style={{ fontSize: 12 }}>{s.nextFireAt ? new Date(s.nextFireAt).toLocaleString() : '—'}</td>
                    <td style={{ color: '#A8B8CC' }}>{s.totalFired}</td>
                    <td style={{ color: s.totalMissed > 0 ? '#F44336' : '#637389' }}>{s.totalMissed}</td>
                    <td>
                      <div className={styles.scheduleActions}>
                        <ZestButton onClick={() => deleteSchedule(s.scheduleId)}
                          zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
                          Remove
                        </ZestButton>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        {overflow.length > 0 && (
          <div className={styles.section}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
              <div className={styles.sectionTitle}>Overflow (Failed Backups)</div>
              <ZestButton onClick={replayOverflow} zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}>
                Replay All
              </ZestButton>
            </div>
            <table className={styles.table}>
              <thead>
                <tr>
                  <th>Project</th>
                  <th>Failed At</th>
                  <th>Error</th>
                </tr>
              </thead>
              <tbody>
                {overflow.map((r, i) => (
                  <tr key={i}>
                    <td>{r.projectName}</td>
                    <td style={{ color: '#637389', fontSize: 12 }}>{ago(r.failedAt)}</td>
                    <td style={{ color: '#F44336', fontSize: 12, maxWidth: 300, overflow: 'hidden', textOverflow: 'ellipsis' }}>{r.errorMessage}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <div className={styles.section}>
          <div className={styles.sectionTitle}>History</div>
          <div className={styles.filters}>
            <div className={styles.filterGroup}>
              <span className={styles.filterLabel}>Status</span>
              <select className={styles.filterInput} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
                <option value="">All</option>
                <option value="completed">Completed</option>
                <option value="failed">Failed</option>
              </select>
            </div>
          </div>
          {filteredHistory.length === 0 ? (
            <div className={styles.empty}>No backup history yet.</div>
          ) : (
            <table className={styles.table}>
              <thead>
                <tr>
                  <th>Project</th>
                  <th>Database</th>
                  <th>Status</th>
                  <th>Started</th>
                  <th>Duration</th>
                  <th>Size</th>
                  <th>Error</th>
                </tr>
              </thead>
              <tbody>
                {filteredHistory.map(r => (
                  <tr key={r.id}>
                    <td>{r.projectName}</td>
                    <td style={{ color: '#A8B8CC', fontSize: 12 }}>{r.databaseName}</td>
                    <td>
                      <span className={styles.statusDot + ' ' + (r.status === 'completed' ? styles.statusOk : styles.statusFail)} />
                      {r.status}
                    </td>
                    <td style={{ color: '#637389', fontSize: 12 }}>{ago(r.startedAt)}</td>
                    <td style={{ fontSize: 12 }}>{r.durationMs > 0 ? fmtMs(r.durationMs) : '—'}</td>
                    <td style={{ color: '#A8B8CC', fontSize: 12 }}>{fmtBytes(r.backupSizeBytes)}</td>
                    <td style={{ color: '#F44336', fontSize: 12, maxWidth: 200, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {r.errorMessage || '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </AppShell>
    </>
  );
}
