import Head from 'next/head';
import { useCallback, useEffect, useMemo, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import AppShell from '@/modules/AppShell/AppShell';
import BackupScheduleDialog from '@/modules/Backups/BackupScheduleDialog';
import BackupFiles from '@/modules/Backups/BackupFiles';
import BackupPolicyEditor from '@/modules/Backups/BackupPolicyEditor';
import RecoveryKeyPanel from '@/modules/Backups/RecoveryKeyPanel';
import DecryptBackupPanel from '@/modules/Backups/DecryptBackupPanel';
import { BackupStorageInfo, fmtBytes } from '@/modules/Backups/backupTypes';
import { api } from '@/shared/ApiService';
import { IProject } from '@/shared/types/IProject';
import { formatDateTime } from '@/shared/time';
import styles from './Styles/Backups.module.css';

interface ScheduleJob {
  scheduleId: string; definitionId?: string; projectId?: string; projectName?: string;
  cron?: string; timeZone?: string;
  jobName: string; scheduleType: string;
  scheduleDescription: string; lastFiredAt: string | null;
  nextFireAt: string | null; inFlightCount: number;
  totalFired: number; totalMissed: number; lastDrift: string | null;
}

interface SchedulesResponse { jobs: ScheduleJob[]; count: number; capturedAt: string; }

interface HistoryRecord {
  id: string; projectId: string; projectName: string; databaseName: string;
  status: string; startedAt: string; completedAt: string | null;
  durationMs: number; errorMessage: string | null; backupSizeBytes: number;
  backupFileName: string | null;
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

function fmtMs(ms: number): string {
  if (ms <= 0) return '—';
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
}

function projectSchedule(schedules: ScheduleJob[], project: IProject): ScheduleJob | undefined {
  const suffix = project.name.replace(/\s+/g, '');
  return schedules.find(s => s.jobName.replace(/^Backup-/, '').replace(/\s+/g, '') === suffix);
}

export default function BackupsPage() {
  const [schedules, setSchedules] = useState<ScheduleJob[]>([]);
  const [history, setHistory] = useState<HistoryRecord[]>([]);
  const [summary, setSummary] = useState<BackupSummary | null>(null);
  const [inflight, setInflight] = useState<InflightResponse['inflight']>(null);
  const [overflow, setOverflow] = useState<OverflowRecord[]>([]);
  const [projects, setProjects] = useState<IProject[]>([]);
  const [storage, setStorage] = useState<BackupStorageInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [scheduleOpen, setScheduleOpen] = useState(false);
  const [editingSchedule, setEditingSchedule] = useState<ScheduleJob | null>(null);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [revision, setRevision] = useState(0);

  const load = useCallback(async () => {
    try {
      const [s, h, sm, i, o, p, st] = await Promise.all([
        api.get<SchedulesResponse>('/api/scheduler/schedules').catch(() => ({ jobs: [], count: 0, capturedAt: '' })),
        api.get<HistoryResponse>('/api/scheduler/history?limit=50').catch(() => ({ records: [], count: 0 })),
        api.get<BackupSummary>('/api/scheduler/history/summary').catch(() => null),
        api.get<InflightResponse>('/api/scheduler/inflight').catch(() => ({ inflight: null })),
        api.get<OverflowResponse>('/api/scheduler/overflow').catch(() => ({ items: [], count: 0 })),
        api.get<IProject[]>('/api/projects').catch(() => []),
        api.get<BackupStorageInfo>('/api/backups/storage').catch(() => null),
      ]);
      setSchedules(s.jobs);
      setHistory(h.records);
      setSummary(sm);
      setInflight(i.inflight);
      setOverflow(o.items);
      setProjects(p);
      setStorage(st);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  const databaseProjects = useMemo(
    () => projects.filter((p) => p.database?.databaseName),
    [projects],
  );

  const deleteSchedule = async (scheduleId: string) => {
    await api.delete(`/api/scheduler/schedules/${scheduleId}`);
    load();
  };

  const replayOverflow = async () => {
    await api.post('/api/scheduler/replay', {});
    load();
  };

  const backupNow = async (project: IProject) => {
    try {
      await api.post(`/api/projects/${project.id}/db/backup`, {});
      toast.success(`Backup started for ${project.name}.`);
      setRevision(r => r + 1);
      setExpanded(prev => ({ ...prev, [project.id]: true }));
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not start the backup.');
    }
  };

  const filteredHistory = statusFilter
    ? history.filter(r => r.status === statusFilter)
    : history;

  return (
    <>
      <Head><title>ShipRight - Backups</title></Head>
      <AppShell>
        <header className={styles.header}>
          <div>
            <h1 className={styles.heading}>Automated Backups</h1>
            <p className={styles.sub}>Scheduled database backups, history, and overflow recovery.</p>
          </div>
          <div className={styles.actions}>
            <ZestButton onClick={load} zest={{ buttonStyle: 'outline', semanticType: 'refresh' }}>
              {loading ? 'Loading.' : 'Refresh'}
            </ZestButton>
            <ZestButton
              onClick={() => setScheduleOpen(true)}
              disabled={databaseProjects.length === 0}
              zest={{ semanticType: 'add', visualOptions: { variant: 'standard' } }}
            >
              Schedule backup
            </ZestButton>
          </div>
        </header>

        {!loading && databaseProjects.length === 0 && (
          <p className={styles.hint}>Add a database to a project before scheduling backups.</p>
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
              <div className={styles.inflightLabel}>Backup In Progress</div>
              <div className={styles.inflightInfo}>
                {inflight.projectName} - {inflight.databaseName} ({fmtMs(inflight.elapsedMs)})
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
          <div className={styles.card}>
            <div className={styles.cardLabel}>Storage</div>
            <div className={styles.cardValue}>{storage ? fmtBytes(storage.usedBytes) : '—'}</div>
            <div className={styles.cardSub}>
              {storage
                ? `${storage.count} files · ${fmtBytes(storage.availableBytes)} free`
                : 'not available'}
            </div>
          </div>
        </div>

        <div className={styles.section}>
          <div className={styles.sectionTitle}>Projects</div>
          {databaseProjects.length === 0 ? (
            <div className={styles.empty}>No projects with a database yet.</div>
          ) : (
            <div className={styles.coverageList}>
              {databaseProjects.map(project => {
                const schedule = projectSchedule(schedules, project);
                const open = expanded[project.id] ?? false;
                return (
                  <div key={project.id} className={styles.coverageRow}>
                    <div className={styles.coverageMain}>
                      <button
                        type="button"
                        className={styles.chevron}
                        aria-expanded={open}
                        aria-controls={`backups-${project.id}`}
                        aria-label={`${open ? 'Collapse' : 'Expand'} backups for ${project.name}`}
                        onClick={() => setExpanded(prev => ({ ...prev, [project.id]: !open }))}
                      >
                        {open ? '▾' : '▸'}
                      </button>
                      <div className={styles.coverageNames}>
                        <span className={styles.coverageName}>{project.name}</span>
                        <span className={styles.coverageDb}>{project.database?.databaseName}</span>
                      </div>
                      <span className={styles.coverageMeta}>
                        {schedule ? schedule.scheduleDescription : 'No schedule'}
                      </span>
                      <span className={styles.coverageMeta}>
                        Keep {project.database?.backupRetainCount ?? 10}
                        {(project.database?.backupRetainDays ?? 0) > 0
                          ? ` · ${project.database?.backupRetainDays}d max`
                          : ''}
                      </span>
                      <ZestButton
                        onClick={() => backupNow(project)}
                        zest={{ buttonStyle: 'outline', semanticType: 'submit', visualOptions: { size: 'sm' } }}
                      >
                        Backup now
                      </ZestButton>
                    </div>
                    {open && (
                      <div className={styles.expanded} id={`backups-${project.id}`}>
                        <BackupPolicyEditor project={project} onSaved={load} />
                        <BackupFiles
                          key={`${project.id}-${revision}-${project.database?.backupRetainCount}-${project.database?.backupRetainDays}`}
                          projectId={project.id}
                          onChanged={() => { setRevision(r => r + 1); load(); }}
                        />
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </div>

        <div className={styles.section}>
          <RecoveryKeyPanel />
        </div>

        <div className={styles.section}>
          <div className={styles.sectionTitle}>Decrypt a backup file</div>
          <div className={styles.panel}>
            <DecryptBackupPanel />
          </div>
        </div>

        <div className={styles.section}>
          <div className={styles.sectionTitle}>Schedules</div>          {schedules.length === 0 ? (
            <div className={styles.emptyState}>
              <div className={styles.emptyTitle}>No backup schedules configured.</div>
              <p className={styles.emptyText}>Pick a project with a database and choose how often ShipRight should back it up.</p>
              {databaseProjects.length > 0 && (
                <ZestButton
                  onClick={() => setScheduleOpen(true)}
                  zest={{ semanticType: 'add', visualOptions: { variant: 'standard' } }}
                >
                  Schedule your first backup
                </ZestButton>
              )}
            </div>
          ) : (
            <div className={styles.tableWrap}>
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
                      <td>{s.projectName ?? s.jobName.replace('Backup-', '')}</td>
                      <td style={{ color: '#A8B8CC', fontSize: 12 }}>
                        {s.cron ? `${s.cron} · ${(s.timeZone ?? '').replace(/_/g, ' ')}` : s.scheduleDescription}
                      </td>
                      <td style={{ color: '#637389', fontSize: 12 }}>{s.lastFiredAt ? ago(s.lastFiredAt) : '-'}</td>
                      <td style={{ fontSize: 12 }}>{s.nextFireAt ? formatDateTime(s.nextFireAt) : '-'}</td>
                      <td style={{ color: '#A8B8CC' }}>{s.totalFired}</td>
                      <td style={{ color: s.totalMissed > 0 ? '#F44336' : '#637389' }}>{s.totalMissed}</td>
                      <td>
                        <div className={styles.scheduleActions}>
                          <ZestButton onClick={() => setEditingSchedule(s)}
                            zest={{ buttonStyle: 'outline', semanticType: 'edit', visualOptions: { size: 'sm' } }}>
                            Edit
                          </ZestButton>
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
            </div>
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
            <div className={styles.tableWrap}>
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
                      <td>{r.projectName}</td>
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

        {scheduleOpen && (
          <BackupScheduleDialog
            projects={projects}
            onClose={() => setScheduleOpen(false)}
            onScheduled={load}
          />
        )}

        {editingSchedule && (
          <BackupScheduleDialog
            projects={projects}
            initial={{
              scheduleId: editingSchedule.scheduleId,
              projectId: editingSchedule.projectId ?? projects.find((p) => p.name === editingSchedule.projectName)?.id,
              cron: editingSchedule.cron,
              timeZone: editingSchedule.timeZone,
            }}
            onClose={() => setEditingSchedule(null)}
            onScheduled={load}
          />
        )}
      </AppShell>
    </>
  );
}
