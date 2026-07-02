import Head from 'next/head';
import { useEffect, useState, useCallback } from 'react';
import AppShell from '@/modules/AppShell/AppShell';
import { api } from '@/shared/ApiService';
import styles from './Styles/Backups.module.css';

interface WatchJob {
  scheduleId: string;
  jobName: string;
  projectId: string;
  branchName: string;
  interval: number;
  lastFiredAt: string | null;
  nextFireAt: string | null;
  totalFired: number;
}

interface SchedulesResponse { jobs: WatchJob[]; count: number; capturedAt: string; }

interface HistoryRecord {
  id: string;
  projectId: string;
  projectName: string;
  branchName: string;
  triggeredBuildId: string | null;
  status: string;
  triggeredAt: string;
  errorMessage: string | null;
}

interface HistoryResponse { records: HistoryRecord[]; count: number; }

interface SnapshotResponse {
  totalPending: number;
  totalProcessed: number;
  totalFailures: number;
  healthStatus: string;
  isDispatcherRunning: boolean;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  capturedAt: string;
}

interface InflightResponse {
  inflight: { projectId: string; projectName: string; branchName: string; startedAt: string; elapsedMs: number; } | null;
}

function ago(dateStr: string): string {
  const ms = Date.now() - new Date(dateStr).getTime();
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  return h < 24 ? `${h}h ago` : `${Math.floor(h / 24)}d ago`;
}

function fmtInterval(s: number): string {
  if (s < 60) return `${s}s`;
  return s < 3600 ? `${Math.floor(s / 60)}m` : `${Math.floor(s / 3600)}h`;
}

const th: React.CSSProperties = {
  textAlign: 'left', padding: '8px 12px',
  borderBottom: '1px solid rgba(255,255,255,0.08)',
  color: 'var(--text-secondary)', fontWeight: 500, fontSize: 12,
};
const td: React.CSSProperties = {
  padding: '8px 12px', borderBottom: '1px solid rgba(255,255,255,0.06)', fontSize: 13,
};

function StatusBadge({ s }: { s: string }) {
  const color = s === 'triggered' ? '#4CAF79' : s === 'failed' ? '#E05252' : '#C9A84C';
  return (
    <span style={{
      background: `${color}22`, color, borderRadius: 4,
      padding: '2px 8px', fontSize: 12, fontWeight: 500,
    }}>
      {s}
    </span>
  );
}

export default function WatchBranchPage() {
  const [schedules, setSchedules] = useState<WatchJob[]>([]);
  const [history, setHistory] = useState<HistoryRecord[]>([]);
  const [snapshot, setSnapshot] = useState<SnapshotResponse | null>(null);
  const [inflight, setInflight] = useState<InflightResponse['inflight']>(null);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');

  const load = useCallback(async () => {
    try {
      const [sched, hist, snap, infl] = await Promise.all([
        api.get<SchedulesResponse>('/api/watch-branch/schedules').catch(() => ({ jobs: [], count: 0, capturedAt: '' })),
        api.get<HistoryResponse>('/api/watch-branch/history?limit=50').catch(() => ({ records: [], count: 0 })),
        api.get<SnapshotResponse>('/api/watch-branch/snapshot').catch(() => null),
        api.get<InflightResponse>('/api/watch-branch/inflight').catch(() => ({ inflight: null })),
      ]);
      setSchedules(sched.jobs);
      setHistory(hist.records);
      setSnapshot(snap);
      setInflight(infl.inflight);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); const t = setInterval(load, 30_000); return () => clearInterval(t); }, [load]);

  const filteredHistory = statusFilter ? history.filter(r => r.status === statusFilter) : history;

  const triggeredCount = history.filter(r => r.status === 'triggered').length;
  const failedCount    = history.filter(r => r.status === 'failed').length;

  return (
    <>
      <Head><title>Watch Branch — ShipRight</title></Head>
      <AppShell>
        <div className={styles.page}>
          <div className={styles.header}>
            <h1 className={styles.title}>Watch Branch</h1>
            <p className={styles.subtitle}>
              Automatic build triggers on remote branch SHA changes.
            </p>
          </div>

          {inflight && (
            <div className={styles.inflightBanner}>
              <span className={styles.inflightDot} />
              Polling <strong>{inflight.branchName}</strong> for <strong>{inflight.projectName}</strong>
              &nbsp;— {Math.round(inflight.elapsedMs / 1000)}s elapsed
            </div>
          )}

          {/* Stat cards */}
          <div className={styles.statCards}>
            <div className={styles.statCard}>
              <span className={styles.statValue}>{schedules.length}</span>
              <span className={styles.statLabel}>Active watches</span>
            </div>
            <div className={styles.statCard}>
              <span className={styles.statValue}>{triggeredCount}</span>
              <span className={styles.statLabel}>Builds triggered</span>
            </div>
            <div className={styles.statCard}>
              <span className={styles.statValue}>{failedCount}</span>
              <span className={styles.statLabel}>Failures</span>
            </div>
            {snapshot && (
              <div className={styles.statCard}>
                <span className={styles.statValue} style={{ color: snapshot.healthStatus === 'Healthy' ? '#4CAF79' : '#E05252' }}>
                  {snapshot.healthStatus}
                </span>
                <span className={styles.statLabel}>Dispatcher</span>
              </div>
            )}
          </div>

          {/* Schedules */}
          {schedules.length > 0 && (
            <section className={styles.section}>
              <h2 className={styles.sectionTitle}>Active Schedules</h2>
              <div className={styles.tableWrapper}>
                <table className={styles.table} style={{ width: '100%', borderCollapse: 'collapse' }}>
                  <thead>
                    <tr>
                      <th style={th}>Job</th>
                      <th style={th}>Interval</th>
                      <th style={th}>Last polled</th>
                      <th style={th}>Next poll</th>
                      <th style={th}>Total fired</th>
                    </tr>
                  </thead>
                  <tbody>
                    {schedules.map(j => (
                      <tr key={j.scheduleId}>
                        <td style={td}>{j.jobName.replace('WatchBranch-', '')}</td>
                        <td style={td}>{fmtInterval(j.interval)}</td>
                        <td style={td}>{j.lastFiredAt ? ago(j.lastFiredAt) : '—'}</td>
                        <td style={td}>{j.nextFireAt ? ago(j.nextFireAt) : '—'}</td>
                        <td style={td}>{j.totalFired}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}

          {/* History */}
          <section className={styles.section}>
            <div className={styles.sectionHeader}>
              <h2 className={styles.sectionTitle}>Run History</h2>
              <select
                value={statusFilter}
                onChange={e => setStatusFilter(e.target.value)}
                style={{
                  background: '#131D30', color: '#F0F2F5',
                  border: '1px solid rgba(255,255,255,0.12)',
                  borderRadius: 6, padding: '4px 8px', fontSize: 12,
                }}
              >
                <option value="">All</option>
                <option value="triggered">Triggered</option>
                <option value="skipped">Skipped</option>
                <option value="failed">Failed</option>
              </select>
            </div>
            {loading && <p className={styles.emptyText}>Loading...</p>}
            {!loading && filteredHistory.length === 0 && (
              <p className={styles.emptyText}>
                No watch runs recorded yet.
                {schedules.length === 0 && ' Enable branch watching in project config to get started.'}
              </p>
            )}
            {filteredHistory.length > 0 && (
              <div className={styles.tableWrapper}>
                <table className={styles.table} style={{ width: '100%', borderCollapse: 'collapse' }}>
                  <thead>
                    <tr>
                      <th style={th}>Project / Branch</th>
                      <th style={th}>Status</th>
                      <th style={th}>Build</th>
                      <th style={th}>When</th>
                    </tr>
                  </thead>
                  <tbody>
                    {filteredHistory.map(r => (
                      <tr key={r.id}>
                        <td style={td}>{r.projectName} / <code>{r.branchName}</code></td>
                        <td style={td}><StatusBadge s={r.status} /></td>
                        <td style={td}>{r.triggeredBuildId
                          ? <code style={{ fontSize: 11 }}>{r.triggeredBuildId.slice(0, 8)}</code>
                          : '—'}
                        </td>
                        <td style={td}>{ago(r.triggeredAt)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </div>
      </AppShell>
    </>
  );
}
