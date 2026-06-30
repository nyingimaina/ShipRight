import { useEffect, useState } from 'react';
import { Tab, TabList, TabPanel, Tabs } from 'react-tabs';
import Link from 'next/link';
import CreatableSelect from 'react-select/creatable';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { RiAddLine, RiDeleteBinLine } from 'react-icons/ri';
import { IApiError, IDatabaseConfig, IProjectInput, IServerConfig, DbProviderType, DeployMode, emptyDatabaseConfig, emptyProjectInput } from '@/shared/types/IProject';
import { api } from '@/shared/ApiService';
import styles from './Styles/ProjectConfigForm.module.css';

interface Props {
  initial?: IProjectInput;
  onSave: (data: IProjectInput) => Promise<void>;
  onCancel: () => void;
}

export default function ProjectConfigForm({ initial, onSave, onCancel, projectId }: Props & { projectId?: string }) {
  const [form, setForm] = useState<IProjectInput>(initial ?? emptyProjectInput());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [dbEnabled, setDbEnabled] = useState(!!initial?.database);
  const [db, setDb] = useState<IDatabaseConfig>(initial?.database ?? emptyDatabaseConfig());
  const [legacyMode, setLegacyMode] = useState(false);
  const [containers, setContainers] = useState<{ name: string; image: string; status: string }[]>([]);
  const [databases, setDatabases] = useState<string[]>([]);
  const [loadingContainers, setLoadingContainers] = useState(false);
  const [loadingDatabases, setLoadingDatabases] = useState(false);

  const [globalServers, setGlobalServers] = useState<IServerConfig[]>([]);

  useEffect(() => {
    api.get<IServerConfig[]>('/api/servers')
      .then(setGlobalServers)
      .catch(() => {});
  }, []);

  useEffect(() => {
    if (dbEnabled && projectId) {
      detectContainers();
    }
  }, [dbEnabled, projectId]);

  const applyGlobalServer = (id: string) => {
    const s = globalServers.find(g => g.id === id);
    if (s) {
      setForm(prev => ({
        ...prev,
        serverId: id,
        server: {
          ...prev.server,
          host: s.host,
          username: s.username,
          sshKeyPath: s.sshKeyPath,
          remoteWorkingDir: s.remoteWorkingDir,
          rebuildScript: s.rebuildScript,
          deployMode: s.deployMode,
        },
      }));
    }
  };

  const set = (path: string, value: string) => {
    const parts = path.split('.');
    setForm(prev => {
      const next = structuredClone(prev) as Record<string, unknown>;
      let obj = next;
      for (let i = 0; i < parts.length - 1; i++) obj = obj[parts[i]] as Record<string, unknown>;
      obj[parts[parts.length - 1]] = value;
      return next as IProjectInput;
    });
    setErrors(prev => { const e = { ...prev }; delete e[path]; return e; });
  };

  const setService = (i: number, field: string, value: string) => {
    setForm(prev => ({
      ...prev,
      services: prev.services.map((s, idx) => idx === i ? { ...s, [field]: value } : s),
    }));
    setErrors(prev => { const e = { ...prev }; delete e[`services[${i}].${field}`]; return e; });
  };

  const addService = () => setForm(prev => ({
    ...prev,
    services: [...prev.services, { name: '', versionFilePath: '', buildContextPath: '', dockerImageName: '', dockerRegistry: '', composeServiceName: '', dockerUsername: '', dockerPassword: '' }],
  }));

  const removeService = (i: number) => setForm(prev => ({
    ...prev, services: prev.services.filter((_, idx) => idx !== i),
  }));

  const setGitRepo = (i: number, field: 'repoPath' | 'deployBranch', value: string) => {
    setForm(prev => ({
      ...prev,
      gitRepos: prev.gitRepos.map((r, idx) => idx === i ? { ...r, [field]: value } : r),
    }));
    setErrors(prev => { const e = { ...prev }; delete e[`gitRepos[${i}].${field}`]; return e; });
  };

  const addGitRepo = () => setForm(prev => ({
    ...prev,
    gitRepos: [...prev.gitRepos, { repoPath: '', deployBranch: 'master' }],
  }));

  const removeGitRepo = (i: number) => setForm(prev => ({
    ...prev, gitRepos: prev.gitRepos.filter((_, idx) => idx !== i),
  }));

  const detectContainers = async () => {
    if (!projectId) return;
    setLoadingContainers(true);
    setContainers([]);
    setDatabases([]);
    try {
      const list = await api.get<{ name: string; image: string; status: string }[]>(
        `/api/projects/${projectId}/db/containers`);
      setContainers(list);
    } catch { /* server not reachable yet — user can type manually */ }
    finally { setLoadingContainers(false); }
  };

  const detectDatabases = async (containerName: string, provider: DbProviderType) => {
    if (!containerName) return;
    setLoadingDatabases(true);
    setDatabases([]);
    try {
      const list = projectId
        ? await api.post<string[]>(`/api/projects/${projectId}/db/databases`,
            { container: containerName, provider, rootUser: db.rootUser, rootPassword: db.rootPassword ?? '' })
        : await api.post<string[]>('/api/servers/db/databases-inline',
            { host: form.server.host, username: form.server.username, sshKeyPath: form.server.sshKeyPath, container: containerName, provider, rootUser: db.rootUser, rootPassword: db.rootPassword ?? '' });
      setDatabases(list ?? []);
    } catch { /* ignore */ }
    finally { setLoadingDatabases(false); }
  };

  const setDbField = <K extends keyof IDatabaseConfig>(key: K, value: IDatabaseConfig[K]) => {
    setDb(prev => ({ ...prev, [key]: value }));
  };

  const handleSave = async () => {
    setErrors({});
    try {
      const payload: IProjectInput = {
        ...form,
        database: dbEnabled ? db : undefined,
      };
      await onSave(payload);
    } catch (errs: unknown) {
      const apiErrors: Record<string, string> = {};
      const list = Array.isArray(errs) ? errs : [errs];
      (list as IApiError[]).forEach(e => { if (e.field) apiErrors[e.field] = e.message; });
      setErrors(apiErrors);
      throw errs;
    }
  };

  const tabHasError = (keys: string[]) =>
    keys.some(k => Object.keys(errors).some(e => e.startsWith(k)));

  const tabClass = (keys: string[]) =>
    [styles.tab, tabHasError(keys) ? styles.tabError : ''].join(' ');

  return (
    <div>
      <Tabs>
        <TabList className={styles.tabList}>
          {[
            { label: 'General',   keys: ['name'] },
            { label: 'Services',  keys: ['services'] },
            { label: 'Git & WSL', keys: ['gitRepos', 'wsl'] },
            { label: 'Server',    keys: ['server'] },
            { label: 'Database',  keys: ['database'] },
          ].map(({ label, keys }) => (
            <Tab key={label} className={tabClass(keys)} selectedClassName={styles.tabActive}>
              {label}
              {tabHasError(keys) && <span className={styles.errorDot}>●</span>}
            </Tab>
          ))}
        </TabList>

        <TabPanel className={styles.panel} selectedClassName={styles.panelActive}>
          <Field label="Project Name" error={errors['name']}>
            <ZestTextbox value={form.name} onChange={e => set('name', e.target.value)}
              placeholder="e.g. SMS Gateway" maxLength={100} zest={{ stretch: true, zSize: 'md' }} />
          </Field>
        </TabPanel>

        <TabPanel className={styles.panel} selectedClassName={styles.panelActive}>
          {form.services.map((svc, i) => (
            <div key={i} className={styles.serviceCard}>
              <div className={styles.serviceCardHeader}>
                <span className={styles.serviceLabel}>Service {i + 1}</span>
                {form.services.length > 1 && (
                  <ZestButton zest={{ visualOptions: { variant: 'danger', size: 'sm' } }} onClick={() => removeService(i)}>
                    <RiDeleteBinLine />
                  </ZestButton>
                )}
              </div>
              <Field label="Service Name" error={errors[`services[${i}].name`]}>
                <ZestTextbox value={svc.name} onChange={e => setService(i, 'name', e.target.value)}
                  placeholder="e.g. API" maxLength={100} zest={{ stretch: true }} />
              </Field>
              <Field label="Version File Path" error={errors[`services[${i}].versionFilePath`]}>
                <ZestTextbox value={svc.versionFilePath} onChange={e => setService(i, 'versionFilePath', e.target.value)}
                  placeholder="/mnt/d/work/.../version.txt" zest={{ stretch: true }} />
              </Field>
              <Field label="Build Context Path" error={errors[`services[${i}].buildContextPath`]}>
                <ZestTextbox value={svc.buildContextPath} onChange={e => setService(i, 'buildContextPath', e.target.value)}
                  placeholder="/mnt/d/work/..." zest={{ stretch: true }} />
              </Field>
              <Field label="Docker Image Name" error={errors[`services[${i}].dockerImageName`]}>
                <ZestTextbox value={svc.dockerImageName} onChange={e => setService(i, 'dockerImageName', e.target.value)}
                  placeholder="nyingi/jattac-sms" zest={{ stretch: true }} />
              </Field>
              <Field label="Docker Registry" error={errors[`services[${i}].dockerRegistry`]}>
                <ZestTextbox value={svc.dockerRegistry ?? ''} onChange={e => setService(i, 'dockerRegistry', e.target.value)}
                  placeholder="ghcr.io (leave empty for Docker Hub)" zest={{ stretch: true }} />
                <p style={{ margin: '4px 0 0', fontSize: 11, color: '#637389' }}>
                  Optional — only needed for non-Docker Hub registries.
                </p>
              </Field>
              <Field label="Compose Service Name" error={errors[`services[${i}].composeServiceName`]}>
                <ZestTextbox value={svc.composeServiceName} onChange={e => setService(i, 'composeServiceName', e.target.value)}
                  placeholder="api (key in docker-compose.yml services:)" zest={{ stretch: true }} />
                <p style={{ margin: '4px 0 0', fontSize: 11, color: '#637389' }}>
                  Optional — when set on all services, only those containers are restarted (nginx/minio stay up).
                </p>
              </Field>
              <Field label="Docker Username" error={errors[`services[${i}].dockerUsername`]}>
                <ZestTextbox value={svc.dockerUsername ?? ''} onChange={e => setService(i, 'dockerUsername', e.target.value)}
                  placeholder="Docker Hub / registry username" zest={{ stretch: true }} />
                <p style={{ margin: '4px 0 0', fontSize: 11, color: '#637389' }}>
                  Optional — saved credentials skip the login prompt during push.
                </p>
              </Field>
              <Field label="Docker Password / Token">
                <input type="password" value={svc.dockerPassword ?? ''} onChange={e => setService(i, 'dockerPassword', e.target.value)}
                  placeholder="Enter to set or update" autoComplete="new-password"
                  style={{ width: '100%', background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)',
                    borderRadius: 6, padding: '6px 10px', fontSize: 14, boxSizing: 'border-box' }} />
                <p style={{ margin: '4px 0 0', fontSize: 11, color: '#637389' }}>
                  Encrypted at rest with AES-256-GCM. Leave blank to keep existing or be prompted at build time.
                </p>
              </Field>
            </div>
          ))}
          {form.services.length < 10 && (
            <ZestButton onClick={addService} zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline' }}>
              <RiAddLine /> Add Service
            </ZestButton>
          )}
        </TabPanel>

        <TabPanel className={styles.panel} selectedClassName={styles.panelActive}>
          {errors['gitRepos'] && <p className={styles.errorText}>{errors['gitRepos']}</p>}
          {form.gitRepos.map((repo, i) => (
            <div key={i} className={styles.serviceCard}>
              <div className={styles.serviceCardHeader}>
                <span className={styles.serviceLabel}>Repository {i + 1}</span>
                {form.gitRepos.length > 1 && (
                  <ZestButton zest={{ visualOptions: { variant: 'danger', size: 'sm' } }} onClick={() => removeGitRepo(i)}>
                    <RiDeleteBinLine />
                  </ZestButton>
                )}
              </div>
              <Field label="Repository Path" error={errors[`gitRepos[${i}].repoPath`]}>
                <ZestTextbox value={repo.repoPath} onChange={e => setGitRepo(i, 'repoPath', e.target.value)}
                  placeholder="/mnt/d/work/nyingi/code/systems/sms-gateway" zest={{ stretch: true }} />
              </Field>
              <Field label="Deploy Branch" error={errors[`gitRepos[${i}].deployBranch`]}>
                <ZestTextbox value={repo.deployBranch} onChange={e => setGitRepo(i, 'deployBranch', e.target.value)}
                  placeholder="master" maxLength={100} zest={{ stretch: true }} />
              </Field>
            </div>
          ))}
          {form.gitRepos.length < 10 && (
            <ZestButton onClick={addGitRepo} zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline' }}>
              <RiAddLine /> Add Repository
            </ZestButton>
          )}
          <Field label="WSL Working Directory" error={errors['wsl.workingDir']}>
            <ZestTextbox value={form.wsl.workingDir} onChange={e => set('wsl.workingDir', e.target.value)}
              placeholder="/home/nyingi/work/jattac/docker/..." zest={{ stretch: true }} />
          </Field>
        </TabPanel>

        <TabPanel className={styles.panel} selectedClassName={styles.panelActive}>
          {globalServers.length > 0 && (
            <div className={styles.formRow}>
              <label className={styles.label}>Linked Server <span style={{ color: '#637389' }}>(optional)</span></label>
              <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <select value={form.serverId ?? ''} onChange={e => applyGlobalServer(e.target.value)}
                  style={{ flex: 1, background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px' }}>
                  <option value="">— None (manual entry) —</option>
                  {globalServers.map(s => (
                    <option key={s.id} value={s.id!}>{s.name || s.host}</option>
                  ))}
                </select>
                <Link href="/servers/" style={{ fontSize: 12, color: '#C9A84C', whiteSpace: 'nowrap' }}>Manage</Link>
              </div>
            </div>
          )}
          <Field label="Host" error={errors['server.host']}>
            <ZestTextbox value={form.server.host} onChange={e => set('server.host', e.target.value)}
              placeholder="3.130.65.46" zest={{ stretch: true }} />
          </Field>
          <Field label="Username" error={errors['server.username']}>
            <ZestTextbox value={form.server.username} onChange={e => set('server.username', e.target.value)}
              placeholder="ubuntu" zest={{ stretch: true }} />
          </Field>
          <Field label="SSH Key Path" error={errors['server.sshKeyPath']}>
            <ZestTextbox value={form.server.sshKeyPath} onChange={e => set('server.sshKeyPath', e.target.value)}
              placeholder="/home/nyingi/.../key.pem" zest={{ stretch: true }} />
          </Field>
          <Field label="Remote Working Directory" error={errors['server.remoteWorkingDir']}>
            <ZestTextbox value={form.server.remoteWorkingDir} onChange={e => set('server.remoteWorkingDir', e.target.value)}
              placeholder="/home/ubuntu/jattac-sms-gateway-docker" zest={{ stretch: true }} />
          </Field>
          <div className={styles.formRow}>
            <label className={styles.label}>Legacy project</label>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <input type="checkbox" checked={legacyMode} onChange={e => setLegacyMode(e.target.checked)}
                style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
              <span style={{ fontSize: 12, color: '#637389' }}>Show rebuild script field</span>
            </div>
          </div>
          {legacyMode && (
            <Field label="Rebuild Script" error={errors['server.rebuildScript']}>
              <ZestTextbox value={form.server.rebuildScript} onChange={e => set('server.rebuildScript', e.target.value)}
                placeholder="rebuild.sh" maxLength={100} zest={{ stretch: true }} />
            </Field>
          )}
          <Field label="Deploy Mode">
            <select value={form.server.deployMode ?? 'GitScript'}
              onChange={e => set('server.deployMode', e.target.value as DeployMode)}
              style={{ background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', width: '100%' }}>
              <option value="GitScript">Git + Script — git pull then run your script (no rollback)</option>
              <option value="GitCompose">Git + Compose — git pull then docker compose up (enables rollback)</option>
              <option value="EnvCompose">Env + Compose — inject image tags at deploy, docker compose up (enables rollback)</option>
            </select>
          </Field>
          {(form.server.deployMode ?? 'GitScript') === 'EnvCompose' && form.services.length > 0 && (
            <div style={{ marginTop: 8, background: '#131D30', border: '1px solid rgba(74,127,168,0.3)',
              borderRadius: 8, padding: '12px 16px' }}>
              <p style={{ margin: '0 0 8px', fontSize: 12, color: '#4A7FA8', fontWeight: 600 }}>
                Required env vars in docker-compose.yml
              </p>
              <p style={{ margin: '0 0 8px', fontSize: 11, color: '#637389' }}>
                Use these substitutions for image tags so ShipRight can inject versions at deploy time:
              </p>
              <table style={{ borderCollapse: 'collapse', width: '100%', fontFamily: "'JetBrains Mono', monospace", fontSize: 11 }}>
                <thead>
                  <tr>
                    <th style={{ textAlign: 'left', color: '#637389', paddingBottom: 4, fontWeight: 400 }}>Service</th>
                    <th style={{ textAlign: 'left', color: '#637389', paddingBottom: 4, fontWeight: 400 }}>Env var</th>
                  </tr>
                </thead>
                <tbody>
                  {form.services.filter(s => s.name).map(s => (
                    <tr key={s.name}>
                      <td style={{ color: '#A8B8CC', padding: '2px 16px 2px 0' }}>{s.name}</td>
                      <td style={{ color: '#C9A84C' }}>
                        {'${' + s.name.toUpperCase().replace(/[^A-Z0-9]/g, '_') + '_TAG}'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <p style={{ margin: '8px 0 0', fontSize: 11, color: '#637389' }}>
                Example: <span style={{ color: '#A8B8CC', fontFamily: "'JetBrains Mono', monospace" }}>
                  image: nyingi/app:{'${APP_TAG:-latest}'}
                </span>
              </p>
            </div>
          )}
        </TabPanel>
        <TabPanel className={styles.panel} selectedClassName={styles.panelActive}>
          <div className={styles.formRow} style={{ alignItems: 'center', flexDirection: 'row', gap: 12 }}>
            <label className={styles.label} style={{ marginBottom: 0 }}>Enable database management</label>
            <input type="checkbox" checked={dbEnabled} onChange={e => setDbEnabled(e.target.checked)}
              style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
          </div>

          {dbEnabled && (
            <>
              <Field label="Provider">
                <select value={db.provider}
                  onChange={e => {
                    const p = e.target.value as DbProviderType;
                    setDbField('provider', p);
                    setDbField('rootUser', p === 'MariaDb' ? 'root' : 'sa');
                    setContainers([]);
                    setDatabases([]);
                  }}
                  style={{ background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', width: '100%' }}>
                  <option value="MariaDb">MariaDB</option>
                  <option value="SqlServer">SQL Server</option>
                </select>
              </Field>

              <Field label="Root user">
                <ZestTextbox value={db.rootUser} onChange={e => setDbField('rootUser', e.target.value)}
                  placeholder="root" zest={{ stretch: true }} />
              </Field>

              <Field label="Password (optional)">
                <ZestTextbox value={db.rootPassword ?? ''} onChange={e => setDbField('rootPassword', e.target.value)}
                  placeholder={`Leave blank to use $${db.provider === 'MariaDb' ? 'MYSQL_ROOT_PASSWORD' : 'SA_PASSWORD'} env var`}
                  type="password" zest={{ stretch: true }} />
              </Field>

              <Field label="Container name">
                {loadingContainers ? (
                  <span style={{ color: '#637389', fontSize: 12 }}>Detecting containers…</span>
                ) : containers.length > 0 ? (
                  <select value={db.containerName}
                    onChange={e => {
                      setDbField('containerName', e.target.value);
                      detectDatabases(e.target.value, db.provider);
                    }}
                    style={{ width: '100%', background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px' }}>
                    <option value="">Select a container…</option>
                    {containers.map(c => (
                      <option key={c.name} value={c.name}>{c.name} — {c.image}</option>
                    ))}
                  </select>
                ) : (
                  <>
                    <ZestTextbox value={db.containerName} onChange={e => setDbField('containerName', e.target.value)}
                      placeholder="e.g. jattac-database" zest={{ stretch: true }} />
                    {form.server.host && form.server.username && form.server.sshKeyPath && (
                      <span style={{ color: '#637389', fontSize: 12, marginTop: 4 }}>
                        No containers found on server. Type manually or check Docker is running.
                      </span>
                    )}
                  </>
                )}
              </Field>

              <Field label="Database name">
                <CreatableSelect
                  options={databases.map(d => ({ value: d, label: d }))}
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
              </Field>

              <Field label="Backup retain count">
                <ZestTextbox value={String(db.backupRetainCount)}
                  onChange={e => setDbField('backupRetainCount', Number(e.target.value) || 10)}
                  placeholder="10" zest={{ stretch: true }} />
              </Field>
            </>
          )}
        </TabPanel>
      </Tabs>

      <div className={styles.footer}>
        <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}>
          Save Project
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
          Cancel
        </ZestButton>
      </div>
    </div>
  );
}

function Field({ label, error, children }: { label: string; error?: string; children: React.ReactNode }) {
  return (
    <div className={styles.formRow}>
      <label className={styles.label}>{label}</label>
      {children}
      {error && <p className={styles.errorText}>{error}</p>}
    </div>
  );
}
