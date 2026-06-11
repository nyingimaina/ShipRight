import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import Link from 'next/link';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import CreatableSelect from 'react-select/creatable';
import { RiCheckLine, RiAlertLine, RiLoader2Line } from 'react-icons/ri';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { api } from '@/shared/ApiService';
import { IDetectedProjectConfig } from '@/shared/types/IDetectedProject';
import { IProject, IProjectInput, IApiError, IServerConfig, IDatabaseConfig, DbProviderType, emptyDatabaseConfig } from '@/shared/types/IProject';
import styles from './Styles/ProjectSetupWizard.module.css';

interface Props {
  existing?: IProject;           // pre-filled when editing
  onSaved: (project: IProject) => void;
  onCancel: () => void;
}

type Step = 'type' | 'pick' | 'review' | 'server';

function uncToLinuxPath(p: string): string {
  if (!p.startsWith('\\\\')) return p;
  const parts = p.split('\\').filter(Boolean);
  // \\wsl.localhost\Ubuntu\home\... → /home/...  (skip server + distro segments)
  return parts.length >= 3 ? '/' + parts.slice(2).join('/') : p;
}

export default function ProjectSetupWizard({ existing, onSaved, onCancel }: Props) {
  const [step, setStep] = useState<Step>(existing ? 'review' : 'type');
  const [projectType, setProjectType] = useState<'pipeline' | 'freeform'>('pipeline');
  const [freeformFeatures, setFreeformFeatures] = useState({ docker: true, git: true, deploy: true, database: false });
  const [rootPath, setRootPath] = useState(existing?.gitRepos[0]?.repoPath ?? '');
  const [detected, setDetected] = useState<IDetectedProjectConfig | null>(null);
  const [detecting, setDetecting] = useState(false);
  const [detectError, setDetectError] = useState<string | null>(null);

  // Editable fields from detection + manual
  type ServiceState = { name: string; versionFilePath: string; buildContextPath: string; dockerImageName: string; dockerRegistry: string; composeServiceName: string; dockerUsername: string; dockerPassword: string; version: string | null };
  const [name, setName]                 = useState(existing?.name ?? '');
  const [services, setServices]         = useState<ServiceState[]>(existing
    ? existing.services.map(s => ({ name: s.name, versionFilePath: s.versionFilePath, buildContextPath: s.buildContextPath, dockerImageName: s.dockerImageName, dockerRegistry: s.dockerRegistry ?? '', composeServiceName: s.composeServiceName ?? '', dockerUsername: s.dockerUsername ?? '', dockerPassword: '', version: null }))
    : []);
  const [gitRepos, setGitRepos]         = useState<{ repoPath: string; deployBranch: string }[]>(existing?.gitRepos ?? []);
  const [wslWorkingDir, setWslWorkingDir] = useState(existing?.wsl.workingDir ?? '');
  const [serverHost, setServerHost]     = useState(existing?.server.host ?? '');
  const [serverUser, setServerUser]     = useState(existing?.server.username ?? 'ubuntu');
  const [sshKeyPath, setSshKeyPath]     = useState(existing?.server.sshKeyPath ?? '');
  const [remoteDir, setRemoteDir]       = useState(existing?.server.remoteWorkingDir ?? '');
  const [rebuildScript, setRebuildScript] = useState(existing?.server.rebuildScript ?? '');
  const [legacyMode, setLegacyMode] = useState(false);
  const [errors, setErrors]             = useState<Record<string, string>>({});
  const [globalServers, setGlobalServers] = useState<IServerConfig[]>([]);
  const [serverId, setServerId] = useState(existing?.serverId ?? '');
  const [showSshPicker, setShowSshPicker] = useState(false);
  const [showWslPicker, setShowWslPicker] = useState(false);
  const [showRemotePicker, setShowRemotePicker] = useState(false);
  const [dbEnabled, setDbEnabled]       = useState(!!existing?.database);
  const [db, setDb]                     = useState<IDatabaseConfig>(existing?.database ?? emptyDatabaseConfig());
  const [dbContainers, setDbContainers] = useState<{ name: string; image: string }[]>([]);
  const [dbDatabases, setDbDatabases]   = useState<string[]>([]);
  const [loadingContainers, setLoadingContainers] = useState(false);
  const [loadingDatabases, setLoadingDatabases]   = useState(false);

  // Step 1: detect from root path
  const handleDetect = async () => {
    if (!rootPath.trim()) { setDetectError('Enter your project root directory first.'); return; }
    setDetecting(true);
    setDetectError(null);
    try {
      const result = await api.post<IDetectedProjectConfig>('/api/projects/detect', { rootPath: rootPath.trim() });
      setDetected(result);
      if (result.suggestedName && !name) setName(result.suggestedName);
      setGitRepos(result.gitRepos.map(r => ({ repoPath: r.repoPath, deployBranch: r.deployBranch })));
      if (result.wslWorkingDir) setWslWorkingDir(result.wslWorkingDir);
      setServices(result.services.map(s => ({
        name: s.suggestedName,
        versionFilePath: s.versionFilePath,
        buildContextPath: s.buildContextPath,
        dockerImageName: s.dockerImageName ?? '',
        dockerRegistry: s.dockerRegistry ?? '',
        composeServiceName: s.composeServiceName ?? '',
        dockerUsername: '',
        dockerPassword: '',
        version: s.version,
      })));
      setStep('review');
    } catch (e: unknown) {
      setDetectError((e as { message?: string })?.message ?? 'Detection failed.');
    } finally {
      setDetecting(false);
    }
  };

  const detectContainers = async () => {
    if (!existing?.id) return;
    setLoadingContainers(true);
    setDbContainers([]);
    setDbDatabases([]);
    try {
      const list = await api.get<{ name: string; image: string }[]>(
        `/api/projects/${existing.id}/db/containers`);
      setDbContainers(list);
    } catch { /* ignore */ }
    finally { setLoadingContainers(false); }
  };

  const detectContainersFromServer = async () => {
    if (!serverHost || !serverUser || !sshKeyPath) return;
    setLoadingContainers(true);
    setDbContainers([]);
    setDbDatabases([]);
    try {
      const list = await api.post<{ name: string; image: string }[]>(
        '/api/servers/db/containers-inline', { host: serverHost, username: serverUser, sshKeyPath });
      setDbContainers(list);
    } catch { /* ignore */ }
    finally { setLoadingContainers(false); }
  };

  const detectDatabases = async (containerName: string, provider: DbProviderType) => {
    if (!containerName) return;
    setLoadingDatabases(true);
    setDbDatabases([]);
    try {
      const list = existing?.id
        ? await api.get<string[]>(
            `/api/projects/${existing.id}/db/databases?container=${encodeURIComponent(containerName)}&provider=${provider}`)
        : await api.post<string[]>('/api/servers/db/databases-inline',
            { host: serverHost, username: serverUser, sshKeyPath, container: containerName, provider });
      setDbDatabases(list ?? []);
    } catch { /* ignore */ }
    finally { setLoadingDatabases(false); }
  };

  const setDbField = <K extends keyof IDatabaseConfig>(key: K, value: IDatabaseConfig[K]) =>
    setDb(prev => ({ ...prev, [key]: value }));

  // Save
  // Fetch global servers for selector
  useEffect(() => {
    api.get<IServerConfig[]>('/api/servers')
      .then(setGlobalServers)
      .catch(() => {});
  }, []);

  // When a global server is selected, auto-fill the inline fields
  const applyGlobalServer = (id: string) => {
    setServerId(id);
    const s = globalServers.find(g => g.id === id);
    if (s) {
      setServerHost(s.host);
      setServerUser(s.username);
      setSshKeyPath(s.sshKeyPath);
      setRemoteDir(s.remoteWorkingDir);
      setRebuildScript(s.rebuildScript);
      detectContainersFromServer();
    }
  };

  const handleSave = async () => {
    setErrors({});
    const input: IProjectInput = {
      name,
      serverId: serverId || undefined,
      services,
      gitRepos,
      wsl: { workingDir: wslWorkingDir },
      server: { host: serverHost, username: serverUser, sshKeyPath, remoteWorkingDir: remoteDir, rebuildScript, deployMode: existing?.server.deployMode ?? 'GitScript' },
      database: dbEnabled ? db : undefined,
    };
    try {
      const saved = existing
        ? await api.put<IProject>(`/api/projects/${existing.id}`, input)
        : await api.post<IProject>('/api/projects', input);
      onSaved(saved);
    } catch (errs: unknown) {
      const apiErrors: Record<string, string> = {};
      const list = Array.isArray(errs) ? errs : [errs];
      (list as IApiError[]).forEach(e => { if (e.field) apiErrors[e.field] = e.message; });
      setErrors(apiErrors);
      // If server errors, keep user on server step
      if (Object.keys(apiErrors).some(k => k.startsWith('server'))) setStep('server');
      // If no field errors were mapped (network error, 500, etc.), surface a toast
      if (Object.keys(apiErrors).length === 0) {
        const msg = (errs as IApiError)?.message ?? 'Failed to save project. Please try again.';
        toast.error(msg);
      }
    }
  };

  const stepIndex = step === 'server' ? 0 : step === 'pick' ? 1 : 2;

  return (
    <div className={styles.wizard}>
      {/* Step dots — hidden on intro screen */}
      {step !== 'type' && (
        <div className={styles.steps}>
          {['server', 'pick', 'review'].map((s, i) => (
            <div key={s} style={{ display: 'contents' }}>
              {i > 0 && <div className={styles.stepDotLine} />}
              <div className={`${styles.stepDot} ${i < stepIndex ? styles.stepDotDone : i === stepIndex ? styles.stepDotActive : ''}`} />
            </div>
          ))}
        </div>
      )}

      {/* ── INTRO: Pipeline vs Freeform ── */}
      {step === 'type' && (
        <>
          <div>
            <h2 className={styles.stepTitle}>How do you want to set up this project?</h2>
            <p className={styles.stepSub}>Choose a mode that fits your workflow.</p>
          </div>

          <div style={{ display: 'flex', gap: 14, margin: '16px 0' }}>
            <button
              type="button"
              onClick={() => setProjectType('pipeline')}
              style={{
                flex: 1, cursor: 'pointer', background: projectType === 'pipeline'
                  ? 'rgba(74,127,168,0.2)' : '#0D1625',
                border: projectType === 'pipeline'
                  ? '2px solid #4A7FA8' : '1px solid rgba(255,255,255,0.1)',
                borderRadius: 10, padding: '18px 16px', textAlign: 'left',
                transition: 'border-color .15s, background .15s',
              }}>
              <div style={{ fontSize: 15, fontWeight: 600, color: '#F0F2F5', marginBottom: 6 }}>Pipeline</div>
              <div style={{ fontSize: 12, color: '#637389', lineHeight: 1.5 }}>
                Full guided wizard — detect source code, configure services, set up your server and database.
              </div>
            </button>
            <button
              type="button"
              onClick={() => setProjectType('freeform')}
              style={{
                flex: 1, cursor: 'pointer', background: projectType === 'freeform'
                  ? 'rgba(74,127,168,0.2)' : '#0D1625',
                border: projectType === 'freeform'
                  ? '2px solid #4A7FA8' : '1px solid rgba(255,255,255,0.1)',
                borderRadius: 10, padding: '18px 16px', textAlign: 'left',
                transition: 'border-color .15s, background .15s',
              }}>
              <div style={{ fontSize: 15, fontWeight: 600, color: '#F0F2F5', marginBottom: 6 }}>Freeform</div>
              <div style={{ fontSize: 12, color: '#637389', lineHeight: 1.5 }}>
                Pick only what you need — Docker build, Git repos, deployment server, database.
              </div>
            </button>
          </div>

          {projectType === 'freeform' && (
            <div style={{ background: '#0D1625', borderRadius: 8, padding: '14px 16px', marginBottom: 12 }}>
              <div style={{ fontSize: 13, fontWeight: 600, color: '#A8B8CC', marginBottom: 10 }}>Features</div>
              <label style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8, cursor: 'pointer', fontSize: 13, color: '#C9D6E3' }}>
                <input type="checkbox" checked={freeformFeatures.docker} onChange={e => setFreeformFeatures(f => ({ ...f, docker: e.target.checked }))}
                  style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
                Docker build &amp; push — services, version files, build context
              </label>
              <label style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8, cursor: 'pointer', fontSize: 13, color: '#C9D6E3' }}>
                <input type="checkbox" checked={freeformFeatures.git} onChange={e => setFreeformFeatures(f => ({ ...f, git: e.target.checked }))}
                  style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
                Git repositories — version control integration
              </label>
              <label style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8, cursor: 'pointer', fontSize: 13, color: '#C9D6E3' }}>
                <input type="checkbox" checked={freeformFeatures.deploy} onChange={e => setFreeformFeatures(f => ({ ...f, deploy: e.target.checked }))}
                  style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
                Deploy server — SSH host, credentials, remote directory
              </label>
              <label style={{ display: 'flex', alignItems: 'center', gap: 10, cursor: 'pointer', fontSize: 13, color: '#C9D6E3' }}>
                <input type="checkbox" checked={freeformFeatures.database} onChange={e => setFreeformFeatures(f => ({ ...f, database: e.target.checked }))}
                  style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
                Database — backup, restore, SQL queries
              </label>
            </div>
          )}

          <div className={styles.footer}>
            <ZestButton
              zest={{ visualOptions: { variant: 'standard' } }}
              onClick={async () => {
                if (projectType === 'pipeline') {
                  setStep('server');
                } else if (freeformFeatures.deploy) {
                  setStep('server');
                } else if (freeformFeatures.docker) {
                  setStep('pick');
                } else if (freeformFeatures.database) {
                  setDbEnabled(true);
                  setStep('server');
                } else {
                  // Minimal save: name only, no features
                  setErrors({});
                  if (!name.trim()) {
                    setErrors({ name: 'Project name is required.' });
                    return;
                  }
                  const input: IProjectInput = {
                    name,
                    serverId: '',
                    services: [],
                    gitRepos: [],
                    wsl: { workingDir: '' },
                    server: { host: '', username: 'ubuntu', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: '', deployMode: 'GitScript' },
                    database: undefined,
                  };
                  try {
                    const saved = await api.post<IProject>('/api/projects', input);
                    onSaved(saved);
                  } catch (errs: unknown) {
                    const apiErrors: Record<string, string> = {};
                    const list = Array.isArray(errs) ? errs : [errs];
                    (list as IApiError[]).forEach(e => { if (e.field) apiErrors[e.field] = e.message; });
                    setErrors(apiErrors);
                    if (Object.keys(apiErrors).length === 0) {
                      toast.error('Failed to save project.');
                    }
                  }
                }
              }}>
              Continue
            </ZestButton>
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          </div>
        </>
      )}

      {step === 'pick' && (
        <>
          <div>
            <h2 className={styles.stepTitle}>Where is your project?</h2>
            <p className={styles.stepSub}>Navigate to your source code root — ShipRight will auto-detect the rest.</p>
          </div>

          <FilePicker
            initialPath={rootPath || undefined}
            dirsOnly
            label="Source code root directory"
            onSelect={path => setRootPath(path)}
          />

          {detectError && <p className={styles.errorText}>{detectError}</p>}

          <div className={styles.footer}>
            <ZestButton onClick={() => setStep('server')} zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>
            <ZestButton onClick={handleDetect}
              zest={{ visualOptions: { variant: 'standard' }, busyOptions: { handleInternally: true } }}>
              {detecting ? 'Detecting…' : 'Detect & Continue'}
            </ZestButton>
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          </div>
        </>
      )}

      {/* ── STEP 2: Review detected + fill service gaps ── */}
      {step === 'review' && (
        <>
          <div>
            <h2 className={styles.stepTitle}>Review detected config</h2>
            <p className={styles.stepSub}>Fields marked in gold need your input.</p>
          </div>

          {/* Project name */}
          <div className={styles.section}>
            <span className={styles.sectionTitle}>Project</span>
            <div className={styles.fieldRow}>
              <label className={styles.fieldLabel}>Name</label>
              <ZestTextbox value={name} onChange={e => setName(e.target.value)}
                placeholder="e.g. SMS Gateway" zest={{ stretch: true }} />
              {errors['name'] && <p className={styles.errorText}>{errors['name']}</p>}
            </div>
          </div>

          {/* Detected git/wsl */}
          {detected && detected.detected.length > 0 && (
            <div className={styles.section}>
              <span className={styles.sectionTitle}>Auto-detected</span>
              <div className={styles.detectedList}>
                {detected.detected.map((d, i) => (
                  <div key={i} className={styles.detectedChip}>
                    <RiCheckLine className={styles.detectedIcon} />
                    <span className={styles.detectedText}>{d}</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Git repositories */}
          <div className={styles.section}>
            <span className={styles.sectionTitle}>Git Repositories</span>
            {errors['gitRepos'] && <p className={styles.errorText}>{errors['gitRepos']}</p>}
            {gitRepos.length === 0 && !errors['gitRepos'] && (
              <div className={styles.warningBox}>No git repositories detected.</div>
            )}
            {gitRepos.map((repo, i) => (
              <div key={i} className={styles.serviceCard}>
                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Repository path</span>
                  <div className={styles.fieldValue}>{repo.repoPath || <em style={{ color: '#888' }}>not detected</em>}</div>
                  {errors[`gitRepos[${i}].repoPath`] && <p className={styles.errorText}>{errors[`gitRepos[${i}].repoPath`]}</p>}
                </div>
                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Deploy branch</span>
                  <ZestTextbox
                    value={repo.deployBranch}
                    onChange={e => setGitRepos(prev => prev.map((r, j) => j === i ? { ...r, deployBranch: e.target.value } : r))}
                    placeholder="e.g. master"
                    zest={{ stretch: true, zSize: 'sm' }}
                  />
                  {errors[`gitRepos[${i}].deployBranch`] && <p className={styles.errorText}>{errors[`gitRepos[${i}].deployBranch`]}</p>}
                </div>
              </div>
            ))}
          </div>

          {/* Services */}
          <div className={styles.section}>
            <span className={styles.sectionTitle}>Services</span>
            {services.length === 0 && (
              <div className={styles.warningBox}>
                No services detected. Ensure each service has a version.txt alongside a Dockerfile.
              </div>
            )}
            {errors['services'] && <p className={styles.errorText}>{errors['services']}</p>}
            {(() => {
              const composeNames = detected ? Array.from(new Set(detected.services.map(s => s.composeServiceName).filter(Boolean) as string[])) : [];
              return services.map((svc, i) => (
              <div key={i} className={styles.serviceCard}>
                <div className={styles.serviceHeader}>
                  <span className={styles.serviceName}>{svc.name || `Service ${i + 1}`}</span>
                  {svc.version && <span className={styles.versionChip}>v{svc.version}</span>}
                  {svc.dockerImageName
                    ? <span className={styles.detectedBadge}>image detected</span>
                    : <span className={styles.needsBadge}>needs image name</span>}
                </div>

                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Service name</span>
                  <ZestTextbox value={svc.name}
                    onChange={e => setServices(prev => prev.map((s, j) => j === i ? { ...s, name: e.target.value } : s))}
                    zest={{ stretch: true, zSize: 'sm' }}
                    list="svc-name-suggestions" />
                  <datalist id="svc-name-suggestions">
                    {composeNames.map(n => <option key={n} value={n} />)}
                  </datalist>
                  {errors[`services[${i}].name`] && <p className={styles.errorText}>{errors[`services[${i}].name`]}</p>}
                </div>

                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Docker image name <span style={{ color: '#C9A84C' }}>*</span></span>
                  <ZestTextbox value={svc.dockerImageName ?? ''}
                    onChange={e => setServices(prev => prev.map((s, j) => j === i ? { ...s, dockerImageName: e.target.value } : s))}
                    placeholder="e.g. nyingi/jattac-sms"
                    zest={{ stretch: true, zSize: 'sm' }} />
                  {errors[`services[${i}].dockerImageName`] && (
                    <p className={styles.errorText}>{errors[`services[${i}].dockerImageName`]}</p>
                  )}
                </div>

                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Docker registry</span>
                  <ZestTextbox value={svc.dockerRegistry ?? ''}
                    onChange={e => setServices(prev => prev.map((s, j) => j === i ? { ...s, dockerRegistry: e.target.value } : s))}
                    placeholder="e.g. ghcr.io (leave empty for Docker Hub)"
                    zest={{ stretch: true, zSize: 'sm' }} />
                  <p style={{ margin: '3px 0 0', fontSize: 11, color: '#637389' }}>
                    Optional — only needed for non-Docker Hub registries.
                  </p>
                </div>

                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>
                    Compose service name
                    {svc.composeServiceName && <span className={styles.detectedBadge} style={{ marginLeft: 6 }}>auto-detected</span>}
                  </span>
                  <ZestTextbox value={svc.composeServiceName ?? ''}
                    onChange={e => setServices(prev => prev.map((s, j) => j === i ? { ...s, composeServiceName: e.target.value } : s))}
                    placeholder="e.g. api (key in docker-compose.yml)"
                    zest={{ stretch: true, zSize: 'sm' }}
                    list="compose-name-suggestions" />
                  <datalist id="compose-name-suggestions">
                    {composeNames.map(n => <option key={n} value={n} />)}
                  </datalist>
                  <p style={{ margin: '3px 0 0', fontSize: 11, color: '#637389' }}>
                    Optional — when set on all services, only those containers restart (nginx/minio stay up).
                  </p>
                </div>

                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Docker username</span>
                  <ZestTextbox value={svc.dockerUsername ?? ''}
                    onChange={e => setServices(prev => prev.map((s, j) => j === i ? { ...s, dockerUsername: e.target.value } : s))}
                    placeholder="registry username" zest={{ stretch: true, zSize: 'sm' }} />
                  <p style={{ margin: '3px 0 0', fontSize: 11, color: '#637389' }}>
                    Optional — saved credentials skip the login prompt during push.
                  </p>
                </div>
                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Docker password / token</span>
                  <input type="password" value={svc.dockerPassword ?? ''}
                    onChange={e => setServices(prev => prev.map((s, j) => j === i ? { ...s, dockerPassword: e.target.value } : s))}
                    placeholder="Enter to set or update"
                    autoComplete="new-password"
                    style={{ width: '100%', background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)',
                      borderRadius: 6, padding: '6px 10px', fontSize: 14, boxSizing: 'border-box' }} />
                  <p style={{ margin: '3px 0 0', fontSize: 11, color: '#637389' }}>
                    Encrypted at rest with AES-256-GCM. Leave blank to keep existing or be prompted at build time.
                  </p>
                </div>

                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Version file</span>
                  <div className={styles.fieldValue}>{svc.versionFilePath}</div>
                </div>
                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Build context</span>
                  <div className={styles.fieldValue}>{svc.buildContextPath}</div>
                </div>
              </div>
            )); })()}
          </div>

          {/* WSL working dir */}
          <div className={styles.section}>
            <span className={styles.sectionTitle}>Docker Compose directory <span style={{ color: '#C9A84C' }}>*</span></span>
            <p className={styles.stepSub}>Where is the docker-compose.yml for this project? (WSL path)</p>
            {showWslPicker
              ? <FilePicker dirsOnly onSelect={p => { setWslWorkingDir(uncToLinuxPath(p)); setShowWslPicker(false); }} />
              : <div style={{ display: 'flex', gap: 8 }}>
                  <ZestTextbox value={wslWorkingDir} onChange={e => setWslWorkingDir(e.target.value)}
                    placeholder="/home/nyingi/work/jattac/docker/..." zest={{ stretch: true }} />
                  <ZestButton onClick={() => setShowWslPicker(true)} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Browse</ZestButton>
                </div>
            }
            {errors['wsl.workingDir'] && <p className={styles.errorText}>{errors['wsl.workingDir']}</p>}
          </div>

          <div className={styles.footer}>
            {!existing && (
              <ZestButton onClick={() => setStep(projectType === 'freeform' && !freeformFeatures.deploy ? 'type' : 'server')}
                zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>
            )}
            {existing
              ? <ZestButton onClick={() => setStep('server')} zest={{ visualOptions: { variant: 'standard' } }}>Server config →</ZestButton>
              : <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>Create Project</ZestButton>
            }
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          </div>
        </>
      )}

      {/* ── STEP 3: Server details ── */}
      {step === 'server' && (
        <>
          <div>
            <h2 className={styles.stepTitle}>Server</h2>
            <p className={styles.stepSub}>Where does this project deploy?</p>
          </div>

          {/* Name field — only on first step for new projects so the user is always asked */}
          {!existing && (
            <div className={styles.section}>
              <span className={styles.sectionTitle}>Project</span>
              <div className={styles.fieldRow}>
                <label className={styles.fieldLabel}>Name <span style={{ color: '#C9A84C' }}>*</span></label>
                <ZestTextbox value={name} onChange={e => setName(e.target.value)}
                  placeholder="e.g. SMS Gateway" zest={{ stretch: true }} />
                {errors['name'] && <p className={styles.errorText}>{errors['name']}</p>}
              </div>
            </div>
          )}

          {/* Global server selector */}
          {globalServers.length > 0 && (
            <div className={styles.section}>
              <span className={styles.sectionTitle}>Linked Server <span style={{ color: '#637389', fontWeight: 400 }}>(optional)</span></span>
              <div className={styles.fieldRow}>
                <label className={styles.fieldLabel}>Choose a global server to pre-fill the fields below.</label>
                <select value={serverId} onChange={e => applyGlobalServer(e.target.value)}
                  style={{ background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', width: '100%' }}>
                  <option value="">— Manual entry —</option>
                  {globalServers.map(s => (
                    <option key={s.id} value={s.id!}>{s.name || s.host}</option>
                  ))}
                </select>
                <Link href="/servers/" style={{ fontSize: 12, color: '#C9A84C' }}>Manage servers →</Link>
              </div>
            </div>
          )}

          <div className={styles.section}>
            <div className={styles.fieldRow}>
              <label className={styles.fieldLabel}>Host (IP or hostname)</label>
              <ZestTextbox value={serverHost} onChange={e => setServerHost(e.target.value)}
                placeholder="3.130.65.46" zest={{ stretch: true }} />
              {errors['server.host'] && <p className={styles.errorText}>{errors['server.host']}</p>}
            </div>

            <div className={styles.fieldRow}>
              <label className={styles.fieldLabel}>Username</label>
              <ZestTextbox value={serverUser} onChange={e => setServerUser(e.target.value)}
                placeholder="ubuntu" zest={{ stretch: true }} />
              {errors['server.username'] && <p className={styles.errorText}>{errors['server.username']}</p>}
            </div>

            <div className={styles.fieldRow}>
              <label className={styles.fieldLabel}>SSH key (.pem)</label>
              {showSshPicker
                ? <FilePicker
                    onSelect={p => { setSshKeyPath(p); setShowSshPicker(false); }}
                    label="Navigate to your .pem key file"
                  />
                : <div className={styles.inputRow}>
                    <ZestTextbox value={sshKeyPath} onChange={e => setSshKeyPath(e.target.value)}
                      placeholder="/home/nyingi/.../.pem" zest={{ stretch: true }} />
                    <ZestButton onClick={() => setShowSshPicker(true)} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Browse</ZestButton>
                  </div>
              }
              {errors['server.sshKeyPath'] && <p className={styles.errorText}>{errors['server.sshKeyPath']}</p>}
            </div>

            <div className={styles.fieldRow}>
              <label className={styles.fieldLabel}>Remote working directory</label>
              {showRemotePicker
                ? <FilePicker
                    dirsOnly
                    sshConfig={{ host: serverHost, user: serverUser, keyPath: sshKeyPath }}
                    initialPath={remoteDir || undefined}
                    label={`Browsing ${serverUser}@${serverHost}`}
                    onSelect={p => { setRemoteDir(p); setShowRemotePicker(false); detectContainersFromServer(); }}
                  />
                : <div className={styles.inputRow}>
                    <ZestTextbox value={remoteDir} onChange={e => setRemoteDir(e.target.value)}
                      placeholder="/home/ubuntu/jattac-sms-gateway-docker" zest={{ stretch: true }} />
                    <ZestButton
                      onClick={() => setShowRemotePicker(true)}
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                      disabled={!serverHost || !serverUser || !sshKeyPath}>
                      Browse
                    </ZestButton>
                  </div>
              }
              {errors['server.remoteWorkingDir'] && <p className={styles.errorText}>{errors['server.remoteWorkingDir']}</p>}
            </div>

            <div className={styles.fieldRow} style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
              <label className={styles.fieldLabel} style={{ marginBottom: 0 }}>Legacy project</label>
              <input type="checkbox" checked={legacyMode} onChange={e => setLegacyMode(e.target.checked)}
                style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
              <span style={{ fontSize: 12, color: '#637389' }}>Show rebuild script field</span>
            </div>
            {legacyMode && (
              <div className={styles.fieldRow}>
                <label className={styles.fieldLabel}>Rebuild script</label>
                <ZestTextbox value={rebuildScript} onChange={e => setRebuildScript(e.target.value)}
                  placeholder="rebuild.sh" zest={{ stretch: true }} />
                {errors['server.rebuildScript'] && <p className={styles.errorText}>{errors['server.rebuildScript']}</p>}
              </div>
            )}
          </div>

          {/* ── Database ── */}
          <div className={styles.section}>
            <span className={styles.sectionTitle}>Database (optional)</span>
            <div className={styles.fieldRow} style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
              <label className={styles.fieldLabel} style={{ marginBottom: 0 }}>Enable database management</label>
              <input type="checkbox" checked={dbEnabled} onChange={e => setDbEnabled(e.target.checked)}
                style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
            </div>
            {dbEnabled && (
              <>
                <div className={styles.fieldRow}>
                  <label className={styles.fieldLabel}>Provider</label>
                  <select value={db.provider}
                    onChange={e => {
                      const p = e.target.value as DbProviderType;
                      setDbField('provider', p);
                      setDbField('rootUser', p === 'MariaDb' ? 'root' : 'sa');
                      setDbContainers([]);
                      setDbDatabases([]);
                    }}
                    style={{ background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', width: '100%' }}>
                    <option value="MariaDb">MariaDB</option>
                    <option value="SqlServer">SQL Server</option>
                  </select>
                </div>
                <div className={styles.fieldRow}>
                  <label className={styles.fieldLabel}>Container name</label>
                  {loadingContainers ? (
                    <span className={styles.spinnerRow}><RiLoader2Line className={styles.spinnerIcon} /> Detecting containers…</span>
                  ) : dbContainers.length > 0 ? (
                    <select value={db.containerName}
                      onChange={e => { setDbField('containerName', e.target.value); detectDatabases(e.target.value, db.provider); }}
                      style={{ width: '100%', background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px' }}>
                      <option value="">Select a container…</option>
                      {dbContainers.map(c => <option key={c.name} value={c.name}>{c.name} — {c.image}</option>)}
                    </select>
                  ) : (
                    <>
                      <ZestTextbox value={db.containerName} onChange={e => setDbField('containerName', e.target.value)}
                        placeholder="e.g. jattac-database" zest={{ stretch: true }} />
                      {serverHost && serverUser && sshKeyPath && (
                        <p className={styles.stepSub} style={{ marginTop: 4 }}>No containers found on server. Type manually or check Docker is running.</p>
                      )}
                    </>
                  )}
                </div>
                <div className={styles.fieldRow}>
                  <label className={styles.fieldLabel}>Database name</label>
                  <CreatableSelect
                    options={dbDatabases.map(d => ({ value: d, label: d }))}
                    value={db.databaseName ? { value: db.databaseName, label: db.databaseName } : null}
                    onChange={(opt) => setDbField('databaseName', (opt as { value: string; label: string } | null)?.value ?? '')}
                    placeholder="Select or type a database name…"
                    isClearable
                    isLoading={loadingDatabases}
                    styles={{
                      control: (b: object) => ({ ...b, background: '#131D30', border: '1px solid rgba(255,255,255,0.12)', minHeight: 36, width: '100%' }),
                      menu: (b: object) => ({ ...b, background: '#1A2640', zIndex: 20 }),
                      option: (b: object, s: { isFocused: boolean }) => ({ ...b, background: s.isFocused ? '#1F2E4A' : 'transparent', color: '#F0F2F5' }),
                      singleValue: (b: object) => ({ ...b, color: '#F0F2F5' }),
                      placeholder: (b: object) => ({ ...b, color: '#637389' }),
                      input: (b: object) => ({ ...b, color: '#F0F2F5' }),
                    }} />
                </div>
                <div className={styles.fieldRow}>
                  <label className={styles.fieldLabel}>Root user</label>
                  <ZestTextbox value={db.rootUser} onChange={e => setDbField('rootUser', e.target.value)}
                    placeholder="root" zest={{ stretch: true }} />
                </div>
              </>
            )}
          </div>

          {Object.keys(errors).some(k => !k.startsWith('server')) && (
            <div className={styles.warningBox}>
              <RiAlertLine /> Some fields on the previous step also have errors — go back to fix them.
            </div>
          )}

          <div className={styles.footer}>
            {existing && <ZestButton onClick={() => setStep('review')} zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>}
            {!existing && <ZestButton onClick={() => setStep('type')} zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>}
            {existing
              ? <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>Save Changes</ZestButton>
              : projectType === 'freeform' && !freeformFeatures.docker && !freeformFeatures.git
                ? <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>Create Project</ZestButton>
                : <ZestButton onClick={() => setStep('pick')} zest={{ visualOptions: { variant: 'standard' } }}>Source code →</ZestButton>
            }
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          </div>
        </>
      )}
    </div>
  );
}
