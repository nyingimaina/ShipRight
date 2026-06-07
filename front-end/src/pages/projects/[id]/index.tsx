import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import AppShell from '@/modules/AppShell/AppShell';
import BuildWizard from '@/modules/BuildWizard/BuildWizard';
import { ProjectSummary } from '@/modules/Dashboard/ProjectCard';
import SqlQueryPanel from '@/modules/Database/SqlQueryPanel';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { IProject } from '@/shared/types/IProject';
import { IServiceVersion } from '@/shared/types/IBuildRecord';
import styles from './Styles/ProjectDetail.module.css';

export default function ProjectDetail() {
  const router = useRouter();
  const { id } = router.query as { id: string };
  const [project, setProject] = useState<IProject | null>(null);
  const [versions, setVersions] = useState<IServiceVersion[]>([]);
  const [summary, setSummary] = useState<ProjectSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [wizardBuildId, setWizardBuildId] = useState<string | undefined>(undefined);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [backups, setBackups] = useState<{ fileName: string; filePath: string; sizeBytes: number; createdAt: string }[]>([]);
  const [backupRunning, setBackupRunning] = useState(false);
  const [dbOpId, setDbOpId] = useState<string | null>(null);
  const [dbLogs, setDbLogs] = useState<string[]>([]);
  const [dbStatus, setDbStatus] = useState<'idle' | 'running' | 'success' | 'error'>('idle');
  const [dbMessage, setDbMessage] = useState('');

  useEffect(() => {
    if (!id) return;
    Promise.all([
      api.get<IProject>(`/api/projects/${id}`),
      api.get<IServiceVersion[]>(`/api/projects/${id}/current-versions`).catch(() => [] as IServiceVersion[]),
      api.get<ProjectSummary>(`/api/projects/${id}/summary`).catch(() => null),
      api.get<{ fileName: string; filePath: string; sizeBytes: number; createdAt: string }[]>(
        `/api/projects/${id}/db/backups`).catch(() => []),
    ])
      .then(([p, v, s, b]) => { setProject(p); setVersions(v); setSummary(s); setBackups(b); })
      .catch(() => toast.error('Project not found.'))
      .finally(() => setLoading(false));
  }, [id]);

  if (loading) return <AppShell><p className={styles.loading}>Loading…</p></AppShell>;
  if (!project) return <AppShell><p className={styles.notFound}>Project not found.</p></AppShell>;

  const versionMap = Object.fromEntries(versions.map(v => [v.serviceName, v]));

  const startBackup = async () => {
    setBackupRunning(true);
    setDbLogs([]);
    setDbStatus('running');
    setDbMessage('');
    try {
      const res = await api.post<{ opId: string }>(`/api/projects/${id}/db/backup`, {});
      const opid = res.opId;
      setDbOpId(opid);
      const es = new EventSource(`/api/projects/${id}/db/ops/${opid}/stream`);
      es.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data);
          if (data.type === 'log') setDbLogs(prev => [...prev, data.data.message]);
          else if (data.type === 'complete') {
            setDbStatus('success');
            setDbMessage(`Backup complete: ${data.data.fileName ?? ''}`);
            setBackupRunning(false);
            es.close();
            api.get<typeof backups>(`/api/projects/${id}/db/backups`).catch(() => []).then(b => setBackups(b as typeof backups));
          } else if (data.type === 'error') {
            setDbStatus('error');
            setDbMessage(data.data.message ?? 'Backup failed.');
            setBackupRunning(false);
            es.close();
          }
        } catch { /* ignore */ }
      };
      es.onerror = () => { setDbStatus('error'); setDbMessage('Connection lost.'); setBackupRunning(false); es.close(); };
    } catch (e: unknown) {
      setDbStatus('error');
      setDbMessage((e as { message?: string })?.message ?? 'Failed to start backup.');
      setBackupRunning(false);
    }
  };

  const startRestore = async (filePath: string) => {
    setDbLogs([]);
    setDbStatus('running');
    setDbMessage('');
    try {
      const res = await api.post<{ opId: string }>(`/api/projects/${id}/db/restore`, { backupFile: filePath });
      const opid = res.opId;
      const es = new EventSource(`/api/projects/${id}/db/ops/${opid}/stream`);
      es.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data);
          if (data.type === 'log') setDbLogs(prev => [...prev, data.data.message]);
          else if (data.type === 'complete') { setDbStatus('success'); setDbMessage('Restore complete.'); es.close(); }
          else if (data.type === 'error') { setDbStatus('error'); setDbMessage(data.data.message ?? 'Restore failed.'); es.close(); }
        } catch { /* ignore */ }
      };
      es.onerror = () => { setDbStatus('error'); setDbMessage('Connection lost.'); es.close(); };
    } catch (e: unknown) {
      setDbStatus('error');
      setDbMessage((e as { message?: string })?.message ?? 'Failed to start restore.');
    }
  };

  return (
    <>
      <Head><title>ShipRight — {project.name}</title></Head>
      <AppShell>
        <div className={styles.titleRow}>
          <h1 className={styles.heading}>{project.name}</h1>
          <ZestButton onClick={() => router.push(`/projects/${id}/edit`)}
            zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline', semanticType: 'edit' }}>
            Edit
          </ZestButton>
        </div>
        <p className={styles.breadcrumb}>
          <Link href="/projects/" className={styles.breadcrumbLink}>Projects</Link>
          {' / '}{project.name}
        </p>

        <section className={styles.section}>
          <h2 className={styles.sectionTitle}>Services</h2>
          <div className={styles.serviceList}>
            {project.services.map(s => {
              const v = versionMap[s.name];
              return (
                <div key={s.name} className={styles.serviceRow}>
                  <span className={styles.serviceName}>{s.name}</span>
                  {v?.version && <span className={styles.versionChip}>v{v.version}</span>}
                  {v?.error && <span className={styles.versionError} title={v.error}>version unreadable</span>}
                  <span className={styles.imageLabel}>{s.dockerImageName}</span>
                </div>
              );
            })}
          </div>
        </section>

        <section className={styles.section}>
          <h2 className={styles.sectionTitle}>Build & Deploy</h2>
          <div className={styles.buildActions}>
            <ZestButton zest={{ visualOptions: { variant: 'standard' } }}
              onClick={() => { setWizardBuildId(undefined); setWizardOpen(true); }}>
              Build
            </ZestButton>
            {summary?.lastBuild?.status === 'ImageBuilt' && (
              <ZestButton zest={{ visualOptions: { variant: 'standard' } }}
                onClick={() => { setWizardBuildId(summary.lastBuild!.id); setWizardOpen(true); }}>
                Push to Registry
              </ZestButton>
            )}
            {summary?.lastBuild?.status === 'PushFailed' && (
              <ZestButton zest={{ visualOptions: { variant: 'standard' } }}
                onClick={() => { setWizardBuildId(summary.lastBuild!.id); setWizardOpen(true); }}>
                Retry Push
              </ZestButton>
            )}
            {(summary?.lastBuild?.status === 'PushSucceeded' || summary?.lastBuild?.status === 'BuildSucceeded') && (
              <ZestButton zest={{ visualOptions: { variant: 'standard' } }}
                onClick={() => { setWizardBuildId(summary.lastBuild!.id); setWizardOpen(true); }}>
                Deploy to Production
              </ZestButton>
            )}
          </div>
        </section>

        {project.database && (
          <section id="database" className={styles.section}>
            <h2 className={styles.sectionTitle}>Database — {project.database.databaseName}</h2>
            <div className={styles.buildActions}>
              <ZestButton
                zest={{ visualOptions: { variant: 'standard' } }}
                onClick={startBackup}
                disabled={backupRunning}>
                {backupRunning ? 'Backing up…' : 'Backup Now'}
              </ZestButton>
            </div>

            {/* Live log for backup / restore */}
            {(dbStatus === 'running' || dbLogs.length > 0) && (
              <div style={{ marginTop: 12, background: '#0D1625', borderRadius: 8, padding: '10px 14px',
                fontFamily: "'JetBrains Mono', monospace", fontSize: 12, color: '#A8B8CC',
                maxHeight: 200, overflowY: 'auto', border: '1px solid rgba(255,255,255,0.08)' }}>
                {dbLogs.map((l, i) => <div key={i}>{l}</div>)}
              </div>
            )}
            {dbStatus === 'success' && <p style={{ color: '#3D9970', fontSize: 13, marginTop: 8 }}>{dbMessage}</p>}
            {dbStatus === 'error'   && <p style={{ color: '#B84040', fontSize: 13, marginTop: 8 }}>{dbMessage}</p>}

            {/* Backup list */}
            {backups.length > 0 && (
              <div style={{ marginTop: 16 }}>
                <h3 className={styles.sectionTitle}>Backups</h3>
                {backups.map(b => (
                  <div key={b.filePath} style={{ display: 'flex', alignItems: 'center', gap: 12,
                    padding: '8px 0', borderBottom: '1px solid rgba(255,255,255,0.05)' }}>
                    <span style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12, color: '#C9A84C', flex: 1 }}>
                      {b.fileName}
                    </span>
                    <span style={{ fontSize: 12, color: '#637389' }}>
                      {(b.sizeBytes / 1024).toFixed(1)} KB
                    </span>
                    <ZestButton
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                      onClick={() => startRestore(b.filePath)}>
                      Restore
                    </ZestButton>
                  </div>
                ))}
              </div>
            )}

            {/* SQL Query */}
            <div style={{ marginTop: 20 }}>
              <h3 className={styles.sectionTitle}>Run SQL Query</h3>
              <SqlQueryPanel projectId={id} />
            </div>
          </section>
        )}
      </AppShell>

      <BuildWizard
        projectId={project.id}
        projectName={project.name}
        currentVersions={versions}
        initialBuildId={wizardBuildId}
        isOpen={wizardOpen}
        onClose={() => { setWizardOpen(false); setWizardBuildId(undefined); }}
      />
    </>
  );
}

export async function getStaticPaths() { return { paths: [], fallback: false }; }
export async function getStaticProps() { return { props: {} }; }
