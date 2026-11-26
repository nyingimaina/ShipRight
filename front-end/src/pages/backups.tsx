import Head from 'next/head';
import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import Poller from 'jattac-web-poller';
import ZestButton from 'jattac.libs.web.zest-button';
import AppShell from '@/modules/AppShell/AppShell';
import RecoveryKeyPanel from '@/modules/Backups/RecoveryKeyPanel';
import DecryptBackupPanel from '@/modules/Backups/DecryptBackupPanel';
import { BackupStorageInfo, fmtBytes } from '@/modules/Backups/backupTypes';
import { api } from '@/shared/ApiService';
import { formatDateTime, formatRelative } from '@/shared/time';
import styles from './Styles/Backups.module.css';

interface CoverageProject {
  projectId: string;
  projectName: string;
  databaseName: string;
  backupCount: number;
  lastBackupAt: string | null;
  storedBytes: number;
  originalBytes: number;
  keepCount: number;
  keepDays: number;
  cron?: string | null;
  timeZone?: string | null;
  scheduleId?: string | null;
  nextFireAt: string | null;
  lastFiredAt: string | null;
}

interface CoverageResponse { projects: CoverageProject[]; capturedAt: string; }

interface HistoryRecord {
  id: string; projectId: string; projectName: string; databaseName: string;
  status: string; startedAt: string; completedAt: string | null;
  durationMs: number; errorMessage: string | null; backupSizeBytes: number;
  backupFileName: string | null;
  scheduleId: string; correlationId: string;
}

interface HistoryResponse { records: HistoryRecord[]; count: number; }

interface BackupSummary { totalRuns: number; successfulRuns: number; failedRuns: number; successRate: number; avgDurationMs: number; totalSizeBytes: number; }

interface InflightResponse {
  inflight: { projectId: string; projectName: string; databaseName: string; scheduledAt: string; startedAt: string; elapsedMs: number; } | null;
}

interface OverflowRecord { projectId: string; projectName: string; failedAt: string; errorMessage: string; }
interface OverflowResponse { items: OverflowRecord[]; count: number; }

function ago(dateStr: string): string {
  return formatRelative(dateStr);
}

function fmtMs(ms: number): string {
  if (ms <= 0) return '—';
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
}

function coverageState(project: CoverageProject): { label: string; tone: string } {
  if (project.scheduleId && project.backupCount > 0) return { label: 'Protected', tone: styles.statusHealthy };
  if (project.scheduleId) return { label: 'Waiting for first run', tone: styles.statusWaiting };
  if (project.backupCount > 0) return { label: 'Manual only', tone: styles.statusManual };
  return { label: 'Unprotected', tone: styles.statusUnprotected };
}

function projectBackupsHref(projectId: string): string {
  return `/projects?detail=${projectId}&tab=database`;
}

export default function BackupsPage() {
  const [coverage, setCoverage] = useState<CoverageProject[]>([]);
  const [history, setHistory] = useState<HistoryRecord[]>([]);
  const [summary, setSummary] = useState<BackupSummary | null>(null);
  const [inflight, setInflight] = useState<InflightResponse['inflight']>(null);
  const [overflow, setOverflow] = useState<OverflowRecord[]>([]);
  const [storage, setStorage] = useState<BackupStorageInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');

  const load = useCallback(async () => {
    try {
      const [c, h, sm, i, o, st] = await Promise.all([
        api.get<CoverageResponse>('/api/backups/coverage').catch(() => ({ projects: [], capturedAt: '' })),
        api.get<HistoryResponse>('/api/scheduler/history?limit=50').catch(() => ({ records: [], count: 0 })),
        api.get<BackupSummary>('/api/scheduler/history/summary').catch(() => null),
        api.get<InflightResponse>('/api/scheduler/inflight').catch(() => ({ inflight: null })),
        api.get<OverflowResponse>('/api/scheduler/overflow').catch(() => ({ items: [], count: 0 })),
        api.get<BackupStorageInfo>('/api/backups/storage').catch(() => null),
      ]);
      setCoverage(c.projects);
      setHistory(h.records);
      setSummary(sm);
      setInflight(i.inflight);
      setOverflow(o.items);
      setStorage(st);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  // The control room watches the same cheap coverage signal as a project, so a scheduled or manual backup that
  // finishes while this page is open updates the cards and history without a refresh.
  useEffect(() => {
    let previous: { coverage: CoverageProject[]; inflight: InflightResponse['inflight'] } | null = null;
    const poller = new Poller<{ coverage: CoverageProject[]; inflight: InflightResponse['inflight'] }>({
      pollIntervalMilliseconds: 10_000,
      maxIntervalMilliseconds: 30_000,
      func: async () => {
        const [coverage, running] = await Promise.all([
          api.get<CoverageResponse>('/api/backups/coverage').catch(() => ({ projects: [], capturedAt: '' })),
          api.get<InflightResponse>('/api/scheduler/inflight').catch(() => ({ inflight: null })),
        ]);
        return { coverage: coverage.projects, inflight: running.inflight };
      },
      onResult: (status, deltaDetected) => {
        setCoverage(status.coverage);
        setInflight(status.inflight);
        if (previous && deltaDetected) void load();
        previous = status;
      },
      onError: () => { /* the page keeps its last state */ },
    });
    poller.start();
    return () => { void poller.stop(); };
  }, [load]);

  const replayOverflow = async () => {
    await api.post('/api/scheduler/replay', {});
    toast.success('Overflow replay started.');
    load();
  };

  const filteredHistory = statusFilter
    ? history.filter(r => r.status === statusFilter)
    : history;

  const scheduled = coverage.filter(c => c.scheduleId).length;
  const unprotected = coverage.filter(c => !c.scheduleId && c.backupCount === 0).length;

  return (
    <>
      <Head><title>ShipRight - Backups</title></Head>
      <AppShell>
        <header className={styles.header}>
          <div>
            <h1 className={styles.heading}>Backups</h1>
            <p className={styles.sub}>
              Coverage and health across every database. Each project owns its policy, schedules and files — open the
              project to change them.
            </p>
          </div>
          <div className={styles.actions}>
            <ZestButton onClick={load} zest={{ buttonStyle: 'outline', semanticType: 'refresh' }}>
              {loading ? 'Loading.' : 'Refresh'}
            </ZestButton>
          </div>
        </header>

        {!loading && coverage.length === 0 && (
          <div className={styles.emptyState}>
            <div className={styles.emptyTitle}>No database projects yet</div>
            <p className={styles.emptyText}>
              Add a database to a project, then open that project&apos;s Database tab to choose its backup policy and schedule.
            </p>
            <Link href="/projects" className={styles.emptyLink}>Go to Projects</Link>
          </div>
        )}

        {storage?.insideDataDirectory && (
          <p className={styles.hint}>
            Backups are stored inside the app data directory ({storage.root}). A data reset or
            <code> docker compose down -v</code> would delete them. Set <code>SHIPRIGHT__BACKUP_DIR</code> to a mounted host folder.
          </p>
        )}

        {inflight && (
          <div className={styles.inflightBanner}>
            <span className={styles.statusDot + ' ' + styles.statusPending} />
            <div>
              <div className={styles.inflightLabel}>Backup in progress</div>
              <div className={styles.inflightInfo}>
                {inflight.projectName} · {inflight.databaseName} ({fmtMs(inflight.elapsedMs)})
              </div>
            </div>
          </div>
        )}

        <div className={styles.cards}>
          <div className={styles.card}>
            <div className={styles.cardLabel}>Database projects</div>
            <div className={styles.cardValue}>{coverage.length}</div>
            <div className={styles.cardSub}>{unprotected === 0 ? 'all have a plan' : `${unprotected} unprotected`}</div>
          </div>
          <div className={styles.card}>
            <div className={styles.cardLabel}>Scheduled</div>
            <div className={styles.cardValue}>{scheduled}</div>
            <div className={styles.cardSub}>of {coverage.length} projects</div>
          </div>
          <div className={styles.card}>
            <div className={styles.cardLabel}>Success rate</div>
            <div className={styles.cardValue}>{summary?.successRate ?? 100}%</div>
            <div className={styles.cardSub}>{summary?.successfulRuns ?? 0} ok / {summary?.totalRuns ?? 0} total</div>
          </div>
          <div className={styles.card}>
            <div className={styles.cardLabel}>Failed</div>
            <div className={styles.cardValue}>{summary?.failedRuns ?? 0}</div>
            <div className={styles.cardSub}>last 30 days</div>
          </div>
          <div className={styles.card}>
            <div className={styles.cardLabel}>Storage</div>
            <div className={styles.cardValue}>{storage ? fmtBytes(storage.usedBytes) : '—'}</div>
            <div className={styles.cardSub}>
              {storage ? `${storage.count} files · ${fmtBytes(storage.availableBytes)} free` : 'not available'}
            </div>
          </div>
        </div>

        {coverage.length > 0 && (
          <div className={styles.section}>
            <div className={styles.sectionHead}>
              <div>
                <div className={styles.sectionTitle}>Project coverage</div>
                <p className={styles.sectionSub}>Open a project to manage its backups where they belong.</p>
              </div>
            </div>
            <div className={styles.coverageGrid}>
              {coverage.map(project => {
                const state = coverageState(project);
                return (
                  <article key={project.projectId} className={styles.coverageCard}>
                    <div className={styles.coverageTop}>
                      <div className={styles.coverageNames}>
                        <span className={styles.coverageName}>{project.projectName}</span>
                        <span className={styles.coverageDb}>{project.databaseName}</span>
                      </div>
                      <span className={`${styles.coverageBadge} ${state.tone}`}>{state.label}</span>
                    </div>

                    <div className={styles.coverageFacts}>
                      <div className={styles.fact}>
                        <span className={styles.factLabel}>Last backup</span>
                        <span className={styles.factValue}>
                          {project.lastBackupAt ? formatRelative(project.lastBackupAt) : 'Never'}
                        </span>
                        {project.lastBackupAt && (
                          <span className={styles.factHint}>{formatDateTime(project.lastBackupAt)}</span>
                        )}
                      </div>
                      <div className={styles.fact}>
                        <span className={styles.factLabel}>Stored</span>
                        <span className={styles.factValue}>{project.backupCount} files</span>
                        <span className={styles.factHint}>{fmtBytes(project.storedBytes)} on disk</span>
                      </div>
                      <div className={styles.fact}>
                        <span className={styles.factLabel}>Schedule</span>
                        <span className={styles.factValue}>{project.cron || 'Not scheduled'}</span>
                        <span className={styles.factHint}>
                          {project.cron
                            ? `${(project.timeZone || 'Africa/Nairobi').replace(/_/g, ' ')}${project.nextFireAt ? ` · next ${formatDateTime(project.nextFireAt)}` : ''}`
                            : 'Set one in the project'}
                        </span>
                      </div>
                      <div className={styles.fact}>
                        <span className={styles.factLabel}>Retention</span>
                        <span className={styles.factValue}>Keep {project.keepCount}</span>
                        <span className={styles.factHint}>
                          {project.keepDays > 0 ? `and at most ${project.keepDays} days` : 'count only'}
                        </span>
                      </div>
                    </div>

                    <div className={styles.coverageActions}>
                      <Link href={projectBackupsHref(project.projectId)} className={styles.manageLink}>
                        Open backups
                        <span aria-hidden="true"> →</span>
                      </Link>
                    </div>
                  </article>
                );
              })}
            </div>
          </div>
        )}

        <div className={styles.section}>
          <RecoveryKeyPanel />
        </div>

        <div className={styles.section}>
          <div className={styles.sectionTitle}>Decrypt a backup file</div>
          <div className={styles.panel}>
            <DecryptBackupPanel />
          </div>
        </div>

        {overflow.length > 0 && (
          <div className={styles.section}>
            <div className={styles.sectionHeadRow}>
              <div className={styles.sectionTitle}>Overflow (failed backups)</div>
              <ZestButton onClick={replayOverflow} zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}>
                Replay all
              </ZestButton>
            </div>
            <div className={styles.tableWrap}>
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th>Project</th>
                    <th>Failed at</th>
                    <th>Error</th>
                  </tr>
                </thead>
                <tbody>
                  {overflow.map((r, i) => (
                    <tr key={i}>
                      <td>
                        <Link href={projectBackupsHref(r.projectId)} className={styles.rowLink}>{r.projectName}</Link>
                      </td>
                      <td style={{ color: '#637389', fontSize: 12 }}>{ago(r.failedAt)}</td>
                      <td style={{ color: '#F44336', fontSize: 12, maxWidth: 300, overflow: 'hidden', textOverflow: 'ellipsis' }}>{r.errorMessage}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        <div className={styles.section}>
          <div className={styles.sectionTitle}>Recent activity</div>
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
            <div className={styles.tableWrap}>
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th>Project</th>
                    <th>Database</th>
                    <th>Status</th>
                    <th>Started</th>
                    <th>Duration</th>
                    <th>Size</th>
                    <th>File</th>
                    <th>Error</th>
                  </tr>
                </thead>
                <tbody>
                  {filteredHistory.map(r => (
                    <tr key={r.id}>
                      <td>
                        <Link href={projectBackupsHref(r.projectId)} className={styles.rowLink}>{r.projectName}</Link>
                      </td>
                      <td style={{ color: '#A8B8CC', fontSize: 12 }}>{r.databaseName}</td>
                      <td>
                        <span className={styles.statusDot + ' ' + (r.status === 'completed' ? styles.statusOk : styles.statusFail)} />
                        {r.status}
                      </td>
                      <td style={{ color: '#637389', fontSize: 12 }}>{ago(r.startedAt)}</td>
                      <td style={{ fontSize: 12 }}>{fmtMs(r.durationMs)}</td>
                      <td style={{ color: '#A8B8CC', fontSize: 12 }}>{fmtBytes(r.backupSizeBytes)}</td>
                      <td style={{ color: '#A8B8CC', fontSize: 12, maxWidth: 220, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                        {r.backupFileName || '—'}
                      </td>
                      <td style={{ color: '#F44336', fontSize: 12, maxWidth: 200, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                        {r.errorMessage || '-'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </AppShell>
    </>
  );
}
