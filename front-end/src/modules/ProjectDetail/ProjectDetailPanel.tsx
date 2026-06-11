import Head from 'next/head';
import { useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import dynamic from 'next/dynamic';
import AppShell from '@/modules/AppShell/AppShell';
import BuildWizard from '@/modules/BuildWizard/BuildWizard';
import { ProjectSummary } from '@/modules/Dashboard/ProjectCard';
import LogViewer, { LogEntry } from '@/modules/BuildWizard/LogViewer';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTabs, { ZestTabItem } from 'jattac.libs.web.zest-tabs';
import { api, sseUrl } from '@/shared/ApiService';
import { useElapsedTimer, fmtElapsed } from '@/shared/hooks/useElapsedTimer';

const SshTerminal    = dynamic(() => import('@/modules/Ssh/SshTerminal'),          { ssr: false });
import DbOperationsPanel from '@/modules/Database/DbOperationsPanel';
import { IDatabaseConfig, IProject } from '@/shared/types/IProject';
import { IDeployment, IServiceVersion } from '@/shared/types/IBuildRecord';
import styles from './ProjectDetailPanel.module.css';

type Tab = 'overview' | 'build' | 'database' | 'logs' | 'terminal';

function projectTabs(project: IProject): ZestTabItem<Tab>[] {
  const hasServer   = !!project.server.host;
  const hasServices = project.services.length > 0;
  const tabs: ZestTabItem<Tab>[] = [
    { label: 'Overview', value: 'overview' },
  ];
  if (hasServices)      tabs.push({ label: 'Build & Deploy', value: 'build' });
  if (project.database) tabs.push({ label: 'Database',        value: 'database' });
  if (hasServer)        tabs.push({ label: 'Logs',            value: 'logs' });
  if (hasServer)        tabs.push({ label: 'Terminal',        value: 'terminal' });
  return tabs;
}

function toLogEntries(lines: string[]): LogEntry[] {
  return lines.map((line, i) => ({
    id: i,
    source: line.startsWith('[docker]') ? 'docker'
          : line.startsWith('[git]') ? 'git'
          : line.startsWith('[ssh]') ? 'ssh'
          : 'shipright',
    line,
  }));
}

interface Props {
  projectId: string;
  onBack: () => void;
}

export default function ProjectDetailPanel({ projectId, onBack }: Props) {
  const [project, setProject] = useState<IProject | null>(null);
  const [versions, setVersions] = useState<IServiceVersion[]>([]);
  const [summary, setSummary] = useState<ProjectSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [wizardBuildId, setWizardBuildId] = useState<string | undefined>(undefined);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [activeTab, setActiveTab] = useState<Tab>('overview');
  const [inferring, setInferring] = useState(false);
  const [inferResult, setInferResult] = useState<{ config: IDatabaseConfig; detected: string[] } | null>(null);
  const [savingInfer, setSavingInfer] = useState(false);
  const [fixingContainer, setFixingContainer] = useState(false);
  const [containerOptions, setContainerOptions] = useState<{ name: string; image: string }[]>([]);
  const [selectedContainer, setSelectedContainer] = useState('');
  const [savingContainer, setSavingContainer] = useState(false);
  const [createVersionInputs, setCreateVersionInputs] = useState<Record<string, string>>({});
  const [creatingVersion, setCreatingVersion] = useState<string | null>(null);

  // Deployment history + rollback
  const [deployments, setDeployments] = useState<IDeployment[]>([]);
  const [rollbackStatus, setRollbackStatus] = useState<'idle' | 'running' | 'success' | 'error'>('idle');
  const [rollbackLogs, setRollbackLogs] = useState<string[]>([]);
  const [rollbackMessage, setRollbackMessage] = useState('');
  const rollbackEsRef = useRef<EventSource | null>(null);

  // Build log panel
  const [showBuildLog, setShowBuildLog] = useState(false);
  const [buildLogLines, setBuildLogLines] = useState<LogEntry[]>([]);
  const [buildLogLoading, setBuildLogLoading] = useState(false);

  // Container log stream
  const [logContainer, setLogContainer] = useState('');
  const [logContainers, setLogContainers] = useState<{ name: string; image: string }[]>([]);
  const [logLoadingContainers, setLogLoadingContainers] = useState(false);
  const [logLines, setLogLines] = useState<string[]>([]);
  const [logActive, setLogActive] = useState(false);
  const logEsRef = useRef<EventSource | null>(null);

  // Elapsed timers for active operations
  const rollbackElapsed = useElapsedTimer(rollbackStatus === 'running');
  const logElapsed = useElapsedTimer(logActive);

  useEffect(() => {
    Promise.all([
      api.get<IProject>(`/api/projects/${projectId}`),
      api.get<IServiceVersion[]>(`/api/projects/${projectId}/current-versions`).catch(() => [] as IServiceVersion[]),
      api.get<ProjectSummary>(`/api/projects/${projectId}/summary`).catch(() => null),
      api.get<IDeployment[]>(`/api/projects/${projectId}/deployments?page=1&pageSize=10`).catch(() => [] as IDeployment[]),
    ])
      .then(([p, v, s, d]) => { setProject(p); setVersions(v); setSummary(s); setDeployments(d); })
      .catch(() => toast.error('Project not found.'))
      .finally(() => setLoading(false));
  }, [projectId]);

  // Keep activeTab valid when tabs change (e.g. project has no database)
  useEffect(() => {
    if (!project) return;
    const valid = projectTabs(project).some(t => t.value === activeTab);
    if (!valid) setActiveTab('overview');
  }, [project?.database]);

  // Cleanup EventSource on unmount
  useEffect(() => {
    return () => {
      logEsRef.current?.close();
      rollbackEsRef.current?.close();
    };
  }, []);

  if (loading) return <AppShell><p className={styles.loading}>Loading…</p></AppShell>;
  if (!project) return <AppShell><p className={styles.notFound}>Project not found.</p></AppShell>;

  const versionMap = Object.fromEntries(versions.map(v => [v.serviceName, v]));

  const startRollback = async (buildId: string) => {
    rollbackEsRef.current?.close();
    setRollbackLogs([]);
    setRollbackStatus('running');
    setRollbackMessage('');
    try {
      const res = await api.post<{ opId: string }>(`/api/builds/${buildId}/rollback`, {});
      const es = new EventSource(sseUrl(`/api/builds/${res.opId}/stream`));
      rollbackEsRef.current = es;
      es.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data);
          if (data.type === 'log') setRollbackLogs(prev => [...prev, data.data.message]);
          else if (data.type === 'deployCompleted') {
            const ok = data.data.status === 'Deployed';
            setRollbackStatus(ok ? 'success' : 'error');
            setRollbackMessage(ok ? 'Rollback complete.' : (data.data.error ?? 'Rollback failed.'));
            es.close();
            api.get<IDeployment[]>(`/api/projects/${projectId}/deployments?page=1&pageSize=10`)
              .then(d => setDeployments(d)).catch(() => {});
          }
        } catch { /* ignore */ }
      };
      es.onerror = () => {
        if (es.readyState === EventSource.CLOSED) {
          setRollbackStatus('error');
          setRollbackMessage('Connection lost.');
        }
      };
    } catch (e: unknown) {
      setRollbackStatus('error');
      setRollbackMessage((e as { message?: string })?.message ?? 'Failed to start rollback.');
    }
  };

  const detectFromCompose = async () => {
    setInferring(true);
    setInferResult(null);
    try {
      const res = await api.get<{ found: boolean; config: IDatabaseConfig | null; detected: string[] }>(
        `/api/projects/${projectId}/db/infer`
      );
      if (res.found && res.config) {
        setInferResult({ config: res.config, detected: res.detected });
      } else {
        toast.error('No database service found in docker-compose.yml.');
      }
    } catch (e: unknown) {
      toast.error((e as { message?: string })?.message ?? 'Detection failed.');
    } finally {
      setInferring(false);
    }
  };

  const saveInferredConfig = async () => {
    if (!inferResult) return;
    setSavingInfer(true);
    try {
      const updated = await api.put<IProject>(`/api/projects/${projectId}`, {
        ...project,
        database: inferResult.config,
      });
      setProject(updated);
      setInferResult(null);
      toast.success('Database configuration saved.');
    } catch (e: unknown) {
      toast.error((e as { message?: string })?.message ?? 'Failed to save configuration.');
    } finally {
      setSavingInfer(false);
    }
  };

  const detectContainers = async () => {
    setFixingContainer(true);
    setContainerOptions([]);
    setSelectedContainer(project?.database?.containerName ?? '');
    try {
      const list = await api.get<{ name: string; image: string }[]>(`/api/projects/${projectId}/db/containers`);
      setContainerOptions(list);
    } catch (e: unknown) {
      toast.error((e as { message?: string })?.message ?? 'Could not reach server.');
      setFixingContainer(false);
    }
  };

  const saveContainer = async () => {
    if (!project?.database || !selectedContainer) return;
    setSavingContainer(true);
    try {
      const updated = await api.put<IProject>(`/api/projects/${projectId}`, {
        ...project,
        database: { ...project.database, containerName: selectedContainer },
      });
      setProject(updated);
      setFixingContainer(false);
      setContainerOptions([]);
      toast.success('Container name updated.');
    } catch (e: unknown) {
      toast.error((e as { message?: string })?.message ?? 'Failed to save.');
    } finally {
      setSavingContainer(false);
    }
  };

  const loadLastBuildLog = async () => {
    if (!summary?.lastBuild?.id) return;
    setBuildLogLoading(true);
    setBuildLogLines([]);
    try {
      const content = await api.getRaw(`/api/builds/${summary.lastBuild.id}/log`);
      setBuildLogLines(toLogEntries(content.split('\n').filter(Boolean)));
    } catch {
      setBuildLogLines([{ id: 0, source: 'shipright', line: '(Failed to load log)' }]);
    } finally {
      setBuildLogLoading(false);
    }
  };

  const loadLogContainers = async () => {
    setLogLoadingContainers(true);
    try {
      const list = await api.get<{ name: string; image: string }[]>(`/api/projects/${projectId}/db/containers`);
      setLogContainers(list);
      if (!logContainer && project?.database?.containerName) {
        const match = list.find(c => c.name === project.database!.containerName);
        if (match) setLogContainer(match.name);
      }
    } catch (e: unknown) {
      toast.error((e as { message?: string })?.message ?? 'Could not reach server.');
    } finally {
      setLogLoadingContainers(false);
    }
  };

  const startContainerLog = () => {
    if (!logContainer) return;
    logEsRef.current?.close();
    setLogLines([]);
    setLogActive(true);
    const es = new EventSource(
      sseUrl(`/api/projects/${projectId}/container-logs/stream?container=${encodeURIComponent(logContainer)}&tail=200`)
    );
    logEsRef.current = es;
    es.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data);
        if (data.line !== undefined) {
          setLogLines(prev => [...prev, data.line as string]);
        }
      } catch { /* ignore */ }
    };
    es.onerror = () => {
      setLogActive(false);
      logEsRef.current = null;
    };
  };

  const stopContainerLog = () => {
    logEsRef.current?.close();
    logEsRef.current = null;
    setLogActive(false);
  };

  const refreshVersions = () => {
    api.get<IServiceVersion[]>(`/api/projects/${projectId}/current-versions`)
      .then(setVersions)
      .catch(() => {});
  };

  const handleCreateVersionFile = async (serviceName: string) => {
    const version = (createVersionInputs[serviceName] ?? '').trim() || '1.0.0';
    setCreatingVersion(serviceName);
    try {
      await api.post(`/api/projects/${projectId}/create-version-file`, { serviceName, version });
      toast.success(`${serviceName}: version.txt created (v${version})`);
      refreshVersions();
      setCreateVersionInputs(p => { const n = { ...p }; delete n[serviceName]; return n; });
    } catch (e: unknown) {
      toast.error((e as { message?: string })?.message ?? 'Failed to create version.txt');
    } finally {
      setCreatingVersion(null);
    }
  };

  return (
    <>
      <Head><title>ShipRight — {project.name}</title></Head>
      <AppShell>
        <div className={styles.titleRow}>
          <h1 className={styles.heading}>{project.name}</h1>
          <ZestButton onClick={onBack}
            zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline', semanticType: 'edit' }}>
            Edit
          </ZestButton>
        </div>
        <p className={styles.breadcrumb}>
          <button onClick={onBack} className={styles.breadcrumbBtn}>Projects</button>
          {' / '}{project.name}
        </p>

        <ZestTabs
          id="project-detail"
          items={projectTabs(project)}
          activeValue={activeTab}
          onChange={setActiveTab}
        />

        {activeTab === 'overview' && (
          <section className={styles.section}>
            <h2 className={styles.sectionTitle}>Services</h2>
            <div className={styles.serviceList}>
              {project.services.map(s => {
                const v = versionMap[s.name];
                const isError = !!v?.error && !v?.version;
                return (
                  <div key={s.name} className={styles.serviceRow}>
                    <span className={styles.serviceName}>{s.name}</span>
                    {v?.version && <span className={styles.versionChip}>v{v.version}</span>}
                    {isError && <span className={styles.versionError} title={v.error ?? undefined}>version unreadable</span>}
                    {isError && (
                      <div className={styles.createVersionRow}>
                        <input
                          type="text"
                          className={styles.createVersionInput}
                          value={createVersionInputs[s.name] ?? '1.0.0'}
                          onChange={e => setCreateVersionInputs(p => ({ ...p, [s.name]: e.target.value }))}
                          disabled={creatingVersion === s.name}
                        />
                        <ZestButton
                          zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                          onClick={() => handleCreateVersionFile(s.name)}
                          disabled={creatingVersion === s.name}>
                          {creatingVersion === s.name ? 'Creating…' : 'Create'}
                        </ZestButton>
                      </div>
                    )}
                    <span className={styles.imageLabel}>{s.dockerImageName}</span>
                  </div>
                );
              })}
            </div>
          </section>
        )}

        {activeTab === 'build' && (
          <>
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

            {summary?.lastBuild && (
              <section className={styles.section}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: showBuildLog ? 12 : 0 }}>
                  <h2 className={styles.sectionTitle} style={{ margin: 0 }}>Last Build Log</h2>
                  <span style={{ fontSize: 12, color: '#637389' }}>{summary.lastBuild.status}</span>
                  <ZestButton
                    zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                    onClick={() => {
                      const next = !showBuildLog;
                      setShowBuildLog(next);
                      if (next && buildLogLines.length === 0) loadLastBuildLog();
                    }}>
                    {showBuildLog ? 'Hide' : 'Show log'}
                  </ZestButton>
                </div>
                {showBuildLog && (
                  buildLogLoading
                    ? <p style={{ fontSize: 12, color: '#637389', fontFamily: "'JetBrains Mono', monospace" }}>Loading…</p>
                    : <LogViewer lines={buildLogLines} />
                )}
              </section>
            )}

            {/* Deployment History */}
            <section className={styles.section}>
              <h2 className={styles.sectionTitle}>Deployment History</h2>
              {deployments.length === 0 ? (
                <p style={{ fontSize: 13, color: '#637389' }}>No deployments recorded yet.</p>
              ) : (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
                  {deployments.map((dep, idx) => (
                    <div key={dep.id} style={{ display: 'flex', alignItems: 'center', gap: 12,
                      padding: '10px 0', borderBottom: '1px solid rgba(255,255,255,0.05)', flexWrap: 'wrap' }}>
                      <span style={{ fontSize: 11, color: '#637389', flexShrink: 0, fontFamily: "'JetBrains Mono', monospace" }}>
                        {new Date(dep.deployedAt).toLocaleString()}
                      </span>
                      <span style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12, color: '#C9A84C', flexShrink: 0 }}>
                        {dep.gitTag || '—'}
                      </span>
                      {dep.versions.map(v => (
                        <span key={v.serviceName} style={{ fontSize: 11, background: 'rgba(74,127,168,0.15)',
                          border: '1px solid rgba(74,127,168,0.3)', borderRadius: 4,
                          padding: '2px 6px', color: '#7AACE0', fontFamily: "'JetBrains Mono', monospace" }}>
                          {v.serviceName}:{v.newVersion}
                        </span>
                      ))}
                      {dep.isRollback && (
                        <span style={{ fontSize: 10, color: '#C9943A', background: 'rgba(201,148,58,0.12)',
                          border: '1px solid rgba(201,148,58,0.3)', borderRadius: 4, padding: '2px 6px' }}>
                          ↩ rollback
                        </span>
                      )}
                      <ZestButton
                        zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                        onClick={() => startRollback(dep.id)}
                        disabled={
                          idx === 0 ||
                          rollbackStatus === 'running' ||
                          project.server.deployMode === 'GitScript'
                        }
                        title={
                          idx === 0 ? 'Currently deployed version'
                          : project.server.deployMode === 'GitScript'
                            ? 'Switch to GitCompose or EnvCompose deploy mode to enable rollback'
                            : undefined
                        }>
                        Roll back
                      </ZestButton>
                    </div>
                  ))}
                </div>
              )}

              {/* Rollback log */}
              {rollbackStatus !== 'idle' && (
                <div className={`${styles.liveOpCard}${rollbackStatus === 'running' ? ' alive' : ''}`}
                  style={{ marginTop: 12 }}>
                  {rollbackStatus === 'running' && (
                    <div className="elapsedBar" style={{ marginBottom: 8 }}>
                      <span className="elapsedDot" />
                      <span className="elapsedTime">{fmtElapsed(rollbackElapsed)}</span>
                      <span>rolling back…</span>
                    </div>
                  )}
                  {rollbackLogs.length > 0 && (
                    <LogViewer lines={toLogEntries(rollbackLogs)} isLive={rollbackStatus === 'running'} />
                  )}
                  {rollbackStatus === 'success' && <p style={{ color: '#3D9970', fontSize: 13, marginTop: 8 }}>{rollbackMessage}</p>}
                  {rollbackStatus === 'error'   && <p style={{ color: '#B84040', fontSize: 13, marginTop: 8 }}>{rollbackMessage}</p>}
                </div>
              )}
            </section>
          </>
        )}

        {activeTab === 'database' && (
          <section className={styles.section}>
            {!project.database ? (
              <>
                <h2 className={styles.sectionTitle}>Database</h2>
                <p style={{ fontSize: 13, color: '#637389', marginBottom: 12 }}>
                  No database configured for this project.
                </p>

                {!inferResult && (
                  <ZestButton
                    zest={{ buttonStyle: 'outline' }}
                    onClick={detectFromCompose}
                    disabled={inferring}>
                    {inferring ? 'Detecting…' : 'Detect from docker-compose.yml'}
                  </ZestButton>
                )}

                {inferResult && (
                  <div style={{ background: '#131D30', border: '1px solid rgba(74,127,168,0.35)',
                    borderRadius: 10, padding: '16px 20px', marginTop: 4, display: 'flex',
                    flexDirection: 'column', gap: 10 }}>
                    <p style={{ fontSize: 13, color: '#4A7FA8', fontWeight: 600, margin: 0 }}>
                      Found in docker-compose.yml
                    </p>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                      {inferResult.detected.map((d, i) => (
                        <span key={i} style={{ fontSize: 12, color: '#A8B8CC',
                          fontFamily: "'JetBrains Mono', monospace" }}>
                          {d}
                        </span>
                      ))}
                    </div>
                    {!inferResult.config.containerName && (
                      <p style={{ fontSize: 12, color: '#C9943A', margin: 0 }}>
                        Container name could not be matched — use the Edit page to set it after saving,
                        or click Detect containers in the wizard.
                      </p>
                    )}
                    <div style={{ display: 'flex', gap: 10, marginTop: 4 }}>
                      <ZestButton
                        zest={{ visualOptions: { variant: 'standard' } }}
                        onClick={saveInferredConfig}
                        disabled={savingInfer}>
                        {savingInfer ? 'Saving…' : 'Save this configuration'}
                      </ZestButton>
                      <ZestButton
                        zest={{ buttonStyle: 'outline' }}
                        onClick={() => setInferResult(null)}>
                        Dismiss
                      </ZestButton>
                    </div>
                  </div>
                )}
              </>
            ) : (
              <>
                <h2 className={styles.sectionTitle}>Database — {project.database.databaseName}</h2>

                {/* Container name row */}
                {!fixingContainer ? (
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
                    <span style={{ fontSize: 12, color: '#637389' }}>Container:</span>
                    <span style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12,
                      color: project.database.containerName ? '#C9A84C' : '#B84040' }}>
                      {project.database.containerName || '(not set)'}
                    </span>
                    <ZestButton
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                      onClick={detectContainers}>
                      Change
                    </ZestButton>
                  </div>
                ) : (
                  <div style={{ marginBottom: 14, background: '#0D1625', border: '1px solid rgba(255,255,255,0.08)',
                    borderRadius: 8, padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 10 }}>
                    <p style={{ margin: 0, fontSize: 12, color: '#637389' }}>
                      {containerOptions.length === 0 ? 'Detecting running containers…' : 'Select the database container:'}
                    </p>
                    {containerOptions.length > 0 && (
                      <select
                        value={selectedContainer}
                        onChange={e => setSelectedContainer(e.target.value)}
                        style={{ background: '#131D30', border: '1px solid rgba(255,255,255,0.12)',
                          borderRadius: 6, padding: '6px 10px', color: '#C9D6E3',
                          fontFamily: "'JetBrains Mono', monospace", fontSize: 12 }}>
                        <option value="">— pick container —</option>
                        {containerOptions.map(c => (
                          <option key={c.name} value={c.name}>
                            {c.name}  ({c.image})
                          </option>
                        ))}
                      </select>
                    )}
                    <div style={{ display: 'flex', gap: 8 }}>
                      <ZestButton
                        zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}
                        onClick={saveContainer}
                        disabled={!selectedContainer || savingContainer}>
                        {savingContainer ? 'Saving…' : 'Save'}
                      </ZestButton>
                      <ZestButton
                        zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                        onClick={() => { setFixingContainer(false); setContainerOptions([]); }}>
                        Cancel
                      </ZestButton>
                    </div>
                  </div>
                )}

                <DbOperationsPanel
                  apiBase={`/api/projects/${projectId}/db`}
                  dbConfig={project.database}
                />
              </>
            )}
          </section>
        )}

        {activeTab === 'logs' && (
          <section className={styles.section}>
            <h2 className={styles.sectionTitle}>Container Logs</h2>

            {/* Container picker */}
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 12, flexWrap: 'wrap' }}>
              {logContainers.length > 0 ? (
                <select
                  value={logContainer}
                  onChange={e => setLogContainer(e.target.value)}
                  style={{ background: '#131D30', border: '1px solid rgba(255,255,255,0.12)',
                    borderRadius: 6, padding: '6px 10px', color: '#C9D6E3',
                    fontFamily: "'JetBrains Mono', monospace", fontSize: 12, minWidth: 220 }}>
                  <option value="">— pick container —</option>
                  {logContainers.map(c => (
                    <option key={c.name} value={c.name}>{c.name}  ({c.image})</option>
                  ))}
                </select>
              ) : (
                <span style={{ fontSize: 12, color: '#637389', fontFamily: "'JetBrains Mono', monospace" }}>
                  {logLoadingContainers ? 'Detecting…' : 'No containers detected yet'}
                </span>
              )}
              <ZestButton
                zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                onClick={loadLogContainers}
                disabled={logLoadingContainers}>
                {logLoadingContainers ? 'Detecting…' : logContainers.length === 0 ? 'Detect containers' : 'Refresh'}
              </ZestButton>
            </div>

            {/* Stream controls */}
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 12, flexWrap: 'wrap' }}>
              {!logActive ? (
                <ZestButton
                  zest={{ visualOptions: { variant: 'standard' } }}
                  onClick={startContainerLog}
                  disabled={!logContainer}>
                  Start streaming
                </ZestButton>
              ) : (
                <ZestButton
                  zest={{ buttonStyle: 'outline' }}
                  onClick={stopContainerLog}>
                  Stop
                </ZestButton>
              )}
              {logLines.length > 0 && (
                <ZestButton
                  zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                  onClick={() => setLogLines([])}>
                  Clear
                </ZestButton>
              )}
              {logActive && (
                <div className="elapsedBar" style={{ marginLeft: 4 }}>
                  <span className="elapsedDot" />
                  <span className="elapsedTime">{fmtElapsed(logElapsed)}</span>
                  <span>streaming</span>
                </div>
              )}
            </div>

            {/* Live log via LogViewer */}
            {(logLines.length > 0 || logActive) && (
              <LogViewer
                lines={toLogEntries(logLines)}
                isLive={logActive}
              />
            )}
          </section>
        )}

        {activeTab === 'terminal' && (
          <section className={styles.section}>
            <h2 className={styles.sectionTitle}>SSH Terminal</h2>
            <p style={{ marginBottom: 16, fontSize: 13, color: '#637389' }}>
              Run arbitrary commands on{' '}
              <span style={{ fontFamily: "'JetBrains Mono', monospace", color: '#C9D6E3' }}>
                {project.server.username}@{project.server.host}
              </span>
            </p>
            {project.server.host ? (
              <SshTerminal
                projectId={project.id}
                serverLabel={`${project.server.username}@${project.server.host}`}
              />
            ) : (
              <p style={{ color: '#C9943A', fontSize: 13 }}>
                No server configured for this project. Edit the project to add server details.
              </p>
            )}
          </section>
        )}

      </AppShell>

      <BuildWizard
        projectId={project.id}
        projectName={project.name}
        currentVersions={versions}
        defaultDeployMode={project.server.deployMode}
        initialBuildId={wizardBuildId}
        isOpen={wizardOpen}
        onClose={() => { setWizardOpen(false); setWizardBuildId(undefined); }}
        onVersionCreated={refreshVersions}
      />
    </>
  );
}