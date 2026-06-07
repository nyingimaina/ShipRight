import { useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { RiCheckLine, RiAlertLine } from 'react-icons/ri';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { api } from '@/shared/ApiService';
import { IDetectedProjectConfig } from '@/shared/types/IDetectedProject';
import { IProject, IProjectInput, IApiError, IDatabaseConfig, DbProviderType, emptyDatabaseConfig } from '@/shared/types/IProject';
import styles from './Styles/ProjectSetupWizard.module.css';

interface Props {
  existing?: IProject;           // pre-filled when editing
  onSaved: (project: IProject) => void;
  onCancel: () => void;
}

type Step = 'pick' | 'review' | 'server';

function uncToLinuxPath(p: string): string {
  if (!p.startsWith('\\\\')) return p;
  const parts = p.split('\\').filter(Boolean);
  // \\wsl.localhost\Ubuntu\home\... → /home/...  (skip server + distro segments)
  return parts.length >= 3 ? '/' + parts.slice(2).join('/') : p;
}

export default function ProjectSetupWizard({ existing, onSaved, onCancel }: Props) {
  const [step, setStep] = useState<Step>(existing ? 'review' : 'server');
  const [rootPath, setRootPath] = useState(existing?.gitRepos[0]?.repoPath ?? '');
  const [detected, setDetected] = useState<IDetectedProjectConfig | null>(null);
  const [detecting, setDetecting] = useState(false);
  const [detectError, setDetectError] = useState<string | null>(null);

  // Editable fields from detection + manual
  const [name, setName]                 = useState(existing?.name ?? '');
  const [services, setServices]         = useState(existing
    ? existing.services.map(s => ({ name: s.name, versionFilePath: s.versionFilePath, buildContextPath: s.buildContextPath, dockerImageName: s.dockerImageName }))
    : [] as { name: string; versionFilePath: string; buildContextPath: string; dockerImageName: string }[]);
  const [gitRepos, setGitRepos]         = useState<{ repoPath: string; deployBranch: string }[]>(existing?.gitRepos ?? []);
  const [wslWorkingDir, setWslWorkingDir] = useState(existing?.wsl.workingDir ?? '');
  const [serverHost, setServerHost]     = useState(existing?.server.host ?? '');
  const [serverUser, setServerUser]     = useState(existing?.server.username ?? 'ubuntu');
  const [sshKeyPath, setSshKeyPath]     = useState(existing?.server.sshKeyPath ?? '');
  const [remoteDir, setRemoteDir]       = useState(existing?.server.remoteWorkingDir ?? '');
  const [rebuildScript, setRebuildScript] = useState(existing?.server.rebuildScript ?? 'rebuild.sh');
  const [errors, setErrors]             = useState<Record<string, string>>({});
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
    } catch { /* ignore — user can type manually */ }
    finally { setLoadingContainers(false); }
  };

  const detectDatabases = async (containerName: string, provider: DbProviderType) => {
    if (!existing?.id || !containerName) return;
    setLoadingDatabases(true);
    setDbDatabases([]);
    try {
      const list = await api.get<string[]>(
        `/api/projects/${existing.id}/db/databases?container=${encodeURIComponent(containerName)}&provider=${provider}`);
      setDbDatabases(list);
    } catch { /* ignore */ }
    finally { setLoadingDatabases(false); }
  };

  const setDbField = <K extends keyof IDatabaseConfig>(key: K, value: IDatabaseConfig[K]) =>
    setDb(prev => ({ ...prev, [key]: value }));

  // Save
  const handleSave = async () => {
    setErrors({});
    const input: IProjectInput = {
      name,
      services,
      gitRepos,
      wsl: { workingDir: wslWorkingDir },
      server: { host: serverHost, username: serverUser, sshKeyPath, remoteWorkingDir: remoteDir, rebuildScript },
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
      {/* Step dots */}
      <div className={styles.steps}>
        {['server', 'pick', 'review'].map((s, i) => (
          <>
            {i > 0 && <div key={`line-${i}`} className={styles.stepDotLine} />}
            <div key={s} className={`${styles.stepDot} ${i < stepIndex ? styles.stepDotDone : i === stepIndex ? styles.stepDotActive : ''}`} />
          </>
        ))}
      </div>

      {/* ── STEP 1: Pick root directory ── */}
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
            {services.map((svc, i) => (
              <div key={i} className={styles.serviceCard}>
                <div className={styles.serviceHeader}>
                  <span className={styles.serviceName}>{svc.name || `Service ${i + 1}`}</span>
                  {svc.dockerImageName
                    ? <span className={styles.detectedBadge}>image detected</span>
                    : <span className={styles.needsBadge}>needs image name</span>}
                </div>

                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Service name</span>
                  <ZestTextbox value={svc.name}
                    onChange={e => setServices(prev => prev.map((s, j) => j === i ? { ...s, name: e.target.value } : s))}
                    zest={{ stretch: true, zSize: 'sm' }} />
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
                  <span className={styles.fieldLabel}>Version file</span>
                  <div className={styles.fieldValue}>{svc.versionFilePath}</div>
                </div>
                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Build context</span>
                  <div className={styles.fieldValue}>{svc.buildContextPath}</div>
                </div>
              </div>
            ))}
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
            {!existing && <ZestButton onClick={() => setStep('pick')} zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>}
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
                : <div style={{ display: 'flex', gap: 8 }}>
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
                    onSelect={p => { setRemoteDir(p); setShowRemotePicker(false); }}
                  />
                : <div style={{ display: 'flex', gap: 8 }}>
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

            <div className={styles.fieldRow}>
              <label className={styles.fieldLabel}>Rebuild script</label>
              <ZestTextbox value={rebuildScript} onChange={e => setRebuildScript(e.target.value)}
                placeholder="rebuild.sh" zest={{ stretch: true }} />
              {errors['server.rebuildScript'] && <p className={styles.errorText}>{errors['server.rebuildScript']}</p>}
            </div>
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
                  <div style={{ display: 'flex', gap: 8 }}>
                    {dbContainers.length > 0
                      ? <select value={db.containerName}
                          onChange={e => { setDbField('containerName', e.target.value); detectDatabases(e.target.value, db.provider); }}
                          style={{ flex: 1, background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px' }}>
                          <option value="">Select a container…</option>
                          {dbContainers.map(c => <option key={c.name} value={c.name}>{c.name} — {c.image}</option>)}
                        </select>
                      : <ZestTextbox value={db.containerName} onChange={e => setDbField('containerName', e.target.value)}
                          placeholder="e.g. jattac-database" zest={{ stretch: true }} />
                    }
                    <ZestButton onClick={detectContainers} disabled={loadingContainers || !existing?.id}
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
                      {loadingContainers ? '…' : 'Detect'}
                    </ZestButton>
                  </div>
                  {!existing?.id && <p className={styles.stepSub} style={{ marginTop: 4 }}>Save the project first to enable auto-detect.</p>}
                </div>
                <div className={styles.fieldRow}>
                  <label className={styles.fieldLabel}>Database name</label>
                  {dbDatabases.length > 0
                    ? <select value={db.databaseName} onChange={e => setDbField('databaseName', e.target.value)}
                        style={{ width: '100%', background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px' }}>
                        <option value="">Select a database…</option>
                        {dbDatabases.map(d => <option key={d} value={d}>{d}</option>)}
                      </select>
                    : <ZestTextbox value={db.databaseName} onChange={e => setDbField('databaseName', e.target.value)}
                        placeholder="e.g. jattac_sms" zest={{ stretch: true }} />
                  }
                  {loadingDatabases && <p className={styles.stepSub} style={{ marginTop: 4 }}>Fetching databases…</p>}
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
            {existing
              ? <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>Save Changes</ZestButton>
              : <ZestButton onClick={() => setStep('pick')} zest={{ visualOptions: { variant: 'standard' } }}>Source code →</ZestButton>
            }
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          </div>
        </>
      )}
    </div>
  );
}
