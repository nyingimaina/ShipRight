import Head from 'next/head';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import { ZestResponsiveLayout } from 'jattac.libs.web.zest-responsive-layout';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import OverflowMenu from 'jattac.libs.web.overflow-menu';
import AppShell from '@/modules/AppShell/AppShell';
import { api } from '@/shared/ApiService';
import { IDockerRegistryResource, IScriptResource, ICredentialResource, IAwsProfileResource, IAwsProfileValidationResult, RegistryAuthType, ScriptPlatform, ExecutionTarget, PipelineScope } from '@/shared/types/IProject';
import TagsInput from '@/shared/components/TagsInput';
import InfoTip from '@/shared/components/InfoTip';
import AwsCliInstallButton from '@/shared/components/AwsCliInstallButton';
import { guidedErrorFor } from '@/shared/awsGuided';
import { useServerMode } from '@/shared/serverInfo';
import AwsProfileWizard from '@/modules/Resources/AwsProfileWizard';
import styles from './Styles/Resources.module.css';

type Tab = 'registries' | 'scripts' | 'credentials' | 'aws-profiles' | 'wsl-disk';

type RegistryInput = {
  id?: string;
  name: string;
  registry: string;
  username: string;
  password?: string;
  authType?: RegistryAuthType;
  awsRegion?: string;
  awsProfileResourceId?: string;
  tags?: string[];
};

type AwsProfileInput = {
  id?: string;
  name: string;
  profileName?: string;
  accessKeyId?: string;
  secretAccessKey?: string;
  sessionToken?: string;
  defaultRegion?: string;
  tags?: string[];
};
type ScriptInput = {
  id?: string;
  name: string;
  content: string;
  platform: ScriptPlatform;
  target: ExecutionTarget;
  scope: PipelineScope;
  projectId?: string;
  workingDirectory?: string;
  variables?: Record<string, string>;
};
type CredentialInput = { id?: string; name: string; value: string; hostPattern?: string; projectId?: string; tags?: string[] };

type WslDiskReport = {
  vhdxFiles: { path: string; sizeBytes: number }[];
  distros: { name: string; state: string }[];
  dockerDfSummary?: string | null;
  error?: string | null;
};

type WslCompactResult = {
  succeeded: boolean;
  blockedReason?: string | null;
  files: { path: string; beforeBytes: number; afterBytes: number }[];
  messages: string[];
};

const formatBytes = (bytes: number): string => {
  if (!bytes || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let idx = 0;
  let value = bytes;
  while (value >= 1024 && idx < units.length - 1) { value /= 1024; idx++; }
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[idx]}`;
};

const emptyScript = (): ScriptInput => ({
  name: '',
  content: '',
  platform: 'Bash',
  target: 'Local',
  scope: 'Global',
});

const emptyCredential = (): CredentialInput => ({
  name: '',
  value: '',
  hostPattern: '',
});

const emptyAwsProfile = (): AwsProfileInput => ({
  name: '',
  profileName: '',
  accessKeyId: '',
  secretAccessKey: '',
  sessionToken: '',
  defaultRegion: '',
});

const commonAwsRegions = [
  'us-east-1', 'us-east-2', 'us-west-1', 'us-west-2',
  'eu-west-1', 'eu-west-2', 'eu-west-3', 'eu-central-1',
  'ap-southeast-1', 'ap-southeast-2', 'ap-northeast-1', 'ap-south-1',
  'sa-east-1', 'ca-central-1',
];

export default function ResourcesPage() {
  const [tab, setTab] = useState<Tab>('registries');
  const [registries, setRegistries] = useState<IDockerRegistryResource[]>([]);
  const [scripts, setScripts] = useState<IScriptResource[]>([]);
  const [credentials, setCredentials] = useState<ICredentialResource[]>([]);
  const [awsProfiles, setAwsProfiles] = useState<IAwsProfileResource[]>([]);
  const [wslReport, setWslReport] = useState<WslDiskReport | null>(null);
  const [compactResult, setCompactResult] = useState<WslCompactResult | null>(null);
  const [compacting, setCompacting] = useState(false);
  const [loading, setLoading] = useState(true);
  const [paneTarget, setPaneTarget] = useState<'new-registry' | 'edit-registry' | 'new-script' | 'edit-script' | 'new-credential' | 'edit-credential' | 'new-aws-profile' | 'edit-aws-profile' | undefined>(undefined);
  const [editItem, setEditItem] = useState<RegistryInput | ScriptInput | CredentialInput | AwsProfileInput | null>(null);

  const load = async () => {
    setLoading(true);
    try {
      const [r, s, c, a] = await Promise.all([
        api.get<IDockerRegistryResource[]>('/api/resources/registries'),
        api.get<IScriptResource[]>('/api/resources/scripts'),
        api.get<ICredentialResource[]>('/api/resources/credentials'),
        api.get<IAwsProfileResource[]>('/api/resources/aws-profiles'),
      ]);
      setRegistries(r);
      setScripts(s);
      setCredentials(c);
      setAwsProfiles(a);
    } catch { toast.error('Failed to load resources.'); }
    finally { setLoading(false); }
  };

  useEffect(() => { load(); }, []);

  useEffect(() => {
    if (tab === 'wsl-disk') refreshWslReport();
  }, [tab]);

  const refreshWslReport = async () => {
    try {
      setWslReport(await api.get<WslDiskReport>('/api/system/wsl-disk'));
    } catch {
      setWslReport(null);
      toast.error('Failed to read WSL disk state.');
    }
  };

  const compactWslDisk = async () => {
    if (!window.confirm('This stops all running WSL distros (wsl --shutdown), then compacts every WSL2 virtual disk. Continue?')) return;
    setCompacting(true);
    setCompactResult(null);
    try {
      const res = await api.post<WslCompactResult>('/api/system/wsl-disk/compact', {});
      setCompactResult(res);
      toast.success('WSL disk compaction complete.');
    } catch (e: any) {
      setCompactResult({
        succeeded: false,
        blockedReason: e?.error ?? e?.message ?? 'Compaction blocked.',
        files: [],
        messages: [],
      });
      toast.error(e?.error ?? e?.message ?? 'Compaction failed.');
    } finally {
      setCompacting(false);
      refreshWslReport();
    }
  };

  const openNewRegistry = () => { setPaneTarget('new-registry'); setEditItem(null); };
  const openEditRegistry = (r: IDockerRegistryResource) => { setPaneTarget('edit-registry'); setEditItem(r); };
  const openNewScript = () => { setPaneTarget('new-script'); setEditItem(null); };
  const openEditScript = (s: IScriptResource) => { setPaneTarget('edit-script'); setEditItem(s); };
  const openNewCredential = () => { setPaneTarget('new-credential'); setEditItem(null); };
  const openEditCredential = (c: ICredentialResource) => { setPaneTarget('edit-credential'); setEditItem(c as CredentialInput); };
  const openNewAwsProfile = () => { setPaneTarget('new-aws-profile'); setEditItem(null); };
  const openEditAwsProfile = (a: IAwsProfileResource) => { setPaneTarget('edit-aws-profile'); setEditItem(a); };
  const closePane = () => { setPaneTarget(undefined); setEditItem(null); };

  const handleSaveRegistry = async (input: RegistryInput) => {
    try {
      if (input.id) {
        await api.put(`/api/resources/registries/${input.id}`, input);
        toast.success('Registry updated.');
      } else {
        await api.post('/api/resources/registries', input);
        toast.success('Registry created.');
      }
      closePane();
      load();
    } catch (e: any) {
      toast.error(e?.message || 'Save failed.');
    }
  };

  const handleSaveScript = async (input: ScriptInput) => {
    try {
      if (input.id) {
        await api.put(`/api/resources/scripts/${input.id}`, input);
        toast.success('Script updated.');
      } else {
        await api.post('/api/resources/scripts', input);
        toast.success('Script created.');
      }
      closePane();
      load();
    } catch (e: any) {
      toast.error(e?.message || 'Save failed.');
    }
  };

  const handleSaveCredential = async (input: CredentialInput) => {
    try {
      if (input.id) {
        await api.put(`/api/resources/credentials/${input.id}`, input);
        toast.success('Credential updated.');
      } else {
        await api.post('/api/resources/credentials', input);
        toast.success('Credential created.');
      }
      closePane();
      load();
    } catch (e: any) {
      toast.error(e?.message || 'Save failed.');
    }
  };

  const handleSaveAwsProfile = async (input: AwsProfileInput) => {
    try {
      if (input.id) {
        await api.put(`/api/resources/aws-profiles/${input.id}`, input);
        toast.success('AWS profile updated.');
      } else {
        await api.post('/api/resources/aws-profiles', input);
        toast.success('AWS profile created.');
      }
      closePane();
      load();
    } catch (e: any) {
      toast.error(e?.message || 'Save failed.');
    }
  };

  const handleDeleteRegistry = async (r: IDockerRegistryResource) => {
    try {
      await api.delete(`/api/resources/registries/${r.id}`);
      toast.success(`'${r.name}' deleted.`);
      setRegistries(prev => prev.filter(x => x.id !== r.id));
    } catch (e: any) {
      if (e?.status === 409) {
        toast.error(e.message || 'Cannot delete — resource is in use by projects.');
      } else {
        toast.error('Failed to delete.');
      }
    }
  };

  const handleDeleteScript = async (s: IScriptResource) => {
    try {
      await api.delete(`/api/resources/scripts/${s.id}`);
      toast.success(`'${s.name}' deleted.`);
      setScripts(prev => prev.filter(x => x.id !== s.id));
    } catch (e: any) {
      if (e?.status === 409) {
        toast.error(e.message || 'Cannot delete — resource is in use by projects.');
      } else {
        toast.error('Failed to delete.');
      }
    }
  };

  const handleDeleteCredential = async (c: ICredentialResource) => {
    try {
      await api.delete(`/api/resources/credentials/${c.id}`);
      toast.success(`'${c.name}' deleted.`);
      setCredentials(prev => prev.filter(x => x.id !== c.id));
    } catch (e: any) {
      if (e?.status === 409) {
        toast.error(e.message || 'Cannot delete — credential is in use by projects.');
      } else {
        toast.error('Failed to delete.');
      }
    }
  };

  const handleDeleteAwsProfile = async (a: IAwsProfileResource) => {
    try {
      await api.delete(`/api/resources/aws-profiles/${a.id}`);
      toast.success(`'${a.name}' deleted.`);
      setAwsProfiles(prev => prev.filter(x => x.id !== a.id));
    } catch (e: any) {
      if (e?.status === 409) {
        toast.error(e.message || 'Cannot delete — AWS profile is in use by registries.');
      } else {
        toast.error('Failed to delete.');
      }
    }
  };

  const paneOpen = paneTarget !== undefined;

  const allTags = Array.from(new Set([
    ...registries.flatMap(r => r.tags ?? []),
    ...credentials.flatMap(c => c.tags ?? []),
    ...awsProfiles.flatMap(p => p.tags ?? []),
  ]));

  const paneTitle = paneTarget === 'new-registry' ? 'New Registry Resource'
    : paneTarget === 'edit-registry' ? `Edit: ${(editItem as IDockerRegistryResource)?.name || ''}`
    : paneTarget === 'new-script' ? 'New Script Resource'
    : paneTarget === 'edit-script' ? `Edit: ${(editItem as IScriptResource)?.name || ''}`
    : paneTarget === 'new-credential' ? 'New Credential Resource'
    : paneTarget === 'edit-credential' ? `Edit: ${(editItem as ICredentialResource)?.name || ''}`
    : paneTarget === 'new-aws-profile' ? 'New AWS Profile Resource'
    : paneTarget === 'edit-aws-profile' ? `Edit: ${(editItem as IAwsProfileResource)?.name || ''}`
    : '';

  const paneContent = paneOpen && paneTarget?.includes('registry') ? (
    <RegistryForm
      initial={paneTarget === 'edit-registry' ? editItem as RegistryInput : { name: '', registry: '', username: '', password: '', authType: 'Password' }}
      isEdit={paneTarget === 'edit-registry'}
      awsProfiles={awsProfiles}
      allTags={allTags}
      onSave={handleSaveRegistry}
      onCancel={closePane}
    />
  ) : paneOpen && paneTarget?.includes('script') ? (
    <ScriptForm
      initial={paneTarget === 'edit-script' ? editItem as ScriptInput : emptyScript()}
      isEdit={paneTarget === 'edit-script'}
      onSave={handleSaveScript}
      onCancel={closePane}
    />
  ) : paneOpen && paneTarget?.includes('credential') ? (
    <CredentialForm
      initial={paneTarget === 'edit-credential' ? editItem as CredentialInput : emptyCredential()}
      isEdit={paneTarget === 'edit-credential'}
      allTags={allTags}
      onSave={handleSaveCredential}
      onCancel={closePane}
    />
  ) : paneOpen && paneTarget?.includes('aws-profile') ? (
    <AwsProfileWizard
      initial={paneTarget === 'edit-aws-profile' ? editItem as AwsProfileInput : emptyAwsProfile()}
      isEdit={paneTarget === 'edit-aws-profile'}
      allTags={allTags}
      onSave={handleSaveAwsProfile}
      onCancel={closePane}
    />
  ) : undefined;

  return (
    <>
      <Head><title>ShipRight — Resources</title></Head>
      <AppShell>
        <ZestResponsiveLayout
          sidePaneWidth="480px"
          closeOnDesktopOverlayClick
          sidePane={{
            visible: paneOpen,
            title: paneTitle,
            onClose: closePane,
            content: paneContent,
          }}
        >
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 24 }}>
            <h1 className={styles.heading}>Resources</h1>
            {tab === 'registries' ? (
              <ZestButton onClick={openNewRegistry}
                zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
                New Registry
              </ZestButton>
            ) : tab === 'scripts' ? (
              <ZestButton onClick={openNewScript}
                zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
                New Script
              </ZestButton>
            ) : tab === 'credentials' ? (
              <ZestButton onClick={openNewCredential}
                zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
                New Credential
              </ZestButton>
            ) : tab === 'aws-profiles' ? (
              <ZestButton onClick={openNewAwsProfile}
                zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
                New AWS Profile
              </ZestButton>
            ) : null}
          </div>

          <div className={styles.tabs}>
            <button
              className={`${styles.tab} ${tab === 'registries' ? styles.tabActive : ''}`}
              onClick={() => setTab('registries')}
            >
              Docker Registries ({registries.length})
            </button>
            <button
              className={`${styles.tab} ${tab === 'scripts' ? styles.tabActive : ''}`}
              onClick={() => setTab('scripts')}
            >
              Scripts ({scripts.length})
            </button>
            <button
              className={`${styles.tab} ${tab === 'credentials' ? styles.tabActive : ''}`}
              onClick={() => setTab('credentials')}
            >
              Credentials ({credentials.length})
            </button>
            <button
              className={`${styles.tab} ${tab === 'aws-profiles' ? styles.tabActive : ''}`}
              onClick={() => setTab('aws-profiles')}
            >
              AWS Profiles ({awsProfiles.length})
            </button>
            <button
              className={`${styles.tab} ${tab === 'wsl-disk' ? styles.tabActive : ''}`}
              onClick={() => setTab('wsl-disk')}
            >
              WSL Disk
            </button>
          </div>

          {tab === 'registries' && (
            <div className={styles.grid}>
              {loading && [0, 1, 2].map(i => (
                <div key={i} className={`${styles.card} ${styles.skeletonCard}`}>
                  <div className={`skeleton ${styles.skeletonTitle}`} />
                </div>
              ))}
              {!loading && registries.map(r => (
                <div key={r.id} className={styles.card}>
                  <div className={styles.cardTop}>
                    <div>
                      <h3 className={styles.cardTitle}>{r.name}</h3>
                      <p className={styles.cardDetail}>{r.registry}</p>
                      {r.authType === 'AwsEcr' ? (
                        <p className={styles.cardDetail}>
                          ECR{r.awsRegion ? ` · ${r.awsRegion}` : ''}
                          {r.awsProfileResourceId && awsProfiles.find(p => p.id === r.awsProfileResourceId) &&
                            ` · ${awsProfiles.find(p => p.id === r.awsProfileResourceId)!.name}`}
                        </p>
                      ) : (
                        <p className={styles.cardDetail}>User: {r.username}</p>
                      )}
                      {r.tags && r.tags.length > 0 && (
                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginTop: 6 }}>
                          {r.tags.map(tag => (
                            <span key={tag} style={{ fontSize: 11, padding: '2px 6px', borderRadius: 4, background: 'rgba(201,168,76,0.15)', color: '#C9A84C' }}>
                              {tag}
                            </span>
                          ))}
                        </div>
                      )}
                    </div>
                    <OverflowMenu items={[
                      { content: 'Edit', onClick: () => openEditRegistry(r) },
                      { content: 'Delete', onClick: () => handleDeleteRegistry(r) },
                    ]} />
                  </div>
                </div>
              ))}
              {!loading && registries.length === 0 && (
                <p className={styles.empty}>
                  No registry resources.{' '}
                  <button onClick={openNewRegistry}
                    style={{ background: 'none', border: 'none', color: '#C9A84C', cursor: 'pointer' }}>
                    Add one
                  </button>.
                </p>
              )}
            </div>
          )}

          {tab === 'scripts' && (
            <div className={styles.grid}>
              {loading && [0, 1, 2].map(i => (
                <div key={i} className={`${styles.card} ${styles.skeletonCard}`}>
                  <div className={`skeleton ${styles.skeletonTitle}`} />
                </div>
              ))}
              {!loading && scripts.map(s => (
                <div key={s.id} className={styles.card}>
                  <div className={styles.cardTop}>
                    <div style={{ flex: 1 }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
                        <h3 className={styles.cardTitle}>{s.name}</h3>
                        <span style={{ fontSize: 11, padding: '2px 6px', borderRadius: 4, background: 'rgba(201,168,76,0.2)', color: '#C9A84C' }}>
                          {s.platform}
                        </span>
                      </div>
                      <pre className={styles.scriptPreview}>{s.content.length > 120 ? s.content.slice(0, 120) + '…' : s.content}</pre>
                    </div>
                    <OverflowMenu items={[
                      { content: 'Edit', onClick: () => openEditScript(s) },
                      { content: 'Delete', onClick: () => handleDeleteScript(s) },
                    ]} />
                  </div>
                </div>
              ))}
              {!loading && scripts.length === 0 && (
                <p className={styles.empty}>
                  No script resources.{' '}
                  <button onClick={openNewScript}
                    style={{ background: 'none', border: 'none', color: '#C9A84C', cursor: 'pointer' }}>
                    Add one
                  </button>.
                </p>
              )}
            </div>
          )}

          {tab === 'credentials' && (
            <div className={styles.grid}>
              {loading && [0, 1, 2].map(i => (
                <div key={i} className={`${styles.card} ${styles.skeletonCard}`}>
                  <div className={`skeleton ${styles.skeletonTitle}`} />
                </div>
              ))}
              {!loading && credentials.map(c => (
                <div key={c.id} className={styles.card}>
                  <div className={styles.cardTop}>
                    <div>
                      <h3 className={styles.cardTitle}>{c.name}</h3>
                      <p className={styles.cardDetail}>Value: {'•'.repeat(Math.min(20, c.name.length + 8))}</p>
                      {c.hostPattern && <p className={styles.cardDetail}>Host: {c.hostPattern}</p>}
                      {c.tags && c.tags.length > 0 && (
                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginTop: 6 }}>
                          {c.tags.map(tag => (
                            <span key={tag} style={{ fontSize: 11, padding: '2px 6px', borderRadius: 4, background: 'rgba(201,168,76,0.15)', color: '#C9A84C' }}>
                              {tag}
                            </span>
                          ))}
                        </div>
                      )}
                    </div>
                    <OverflowMenu items={[
                      { content: 'Edit', onClick: () => openEditCredential(c) },
                      { content: 'Delete', onClick: () => handleDeleteCredential(c) },
                    ]} />
                  </div>
                </div>
              ))}
              {!loading && credentials.length === 0 && (
                <p className={styles.empty}>
                  No credential resources.{' '}
                  <button onClick={openNewCredential}
                    style={{ background: 'none', border: 'none', color: '#C9A84C', cursor: 'pointer' }}>
                    Add one
                  </button>.
                </p>
              )}
            </div>
          )}

          {tab === 'aws-profiles' && (
            <div className={styles.grid}>
              {loading && [0, 1, 2].map(i => (
                <div key={i} className={`${styles.card} ${styles.skeletonCard}`}>
                  <div className={`skeleton ${styles.skeletonTitle}`} />
                </div>
              ))}
              {!loading && awsProfiles.map(a => (
                <div key={a.id} className={styles.card}>
                  <div className={styles.cardTop}>
                    <div>
                      <h3 className={styles.cardTitle}>{a.name}</h3>
                      <p className={styles.cardDetail}>
                        {a.profileName ? `Profile: ${a.profileName}` : 'Explicit keys'}
                        {a.defaultRegion && ` · ${a.defaultRegion}`}
                      </p>
                      {a.tags && a.tags.length > 0 && (
                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginTop: 6 }}>
                          {a.tags.map(tag => (
                            <span key={tag} style={{ fontSize: 11, padding: '2px 6px', borderRadius: 4, background: 'rgba(201,168,76,0.15)', color: '#C9A84C' }}>
                              {tag}
                            </span>
                          ))}
                        </div>
                      )}
                    </div>
                    <OverflowMenu items={[
                      { content: 'Edit', onClick: () => openEditAwsProfile(a) },
                      { content: 'Delete', onClick: () => handleDeleteAwsProfile(a) },
                    ]} />
                  </div>
                </div>
              ))}
              {!loading && awsProfiles.length === 0 && (
                <p className={styles.empty}>
                  No AWS profile resources.{' '}
                  <button onClick={openNewAwsProfile}
                    style={{ background: 'none', border: 'none', color: '#C9A84C', cursor: 'pointer' }}>
                    Add one
                  </button>.
                </p>
              )}
            </div>
          )}

          {tab === 'wsl-disk' && (
            <div className={styles.grid} style={{ gridTemplateColumns: '1fr' }}>
              <div className={styles.card}>
                <div className={styles.cardTop}>
                  <div>
                    <h3 className={styles.cardTitle}>WSL Virtual Disk</h3>
                    <p className={styles.cardDetail}>
                      Docker prune frees space inside the WSL ext4.vhdx, but the Windows-side file only
                      shrinks after <code>wsl --shutdown</code> + <code>Optimize-VHD -Mode Full</code>.
                      Compaction stops all running WSL distros and is blocked while any build is active.
                    </p>
                  </div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <ZestButton onClick={refreshWslReport}
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
                      Refresh
                    </ZestButton>
                    <ZestButton onClick={compactWslDisk} disabled={compacting}
                      zest={{ visualOptions: { variant: 'danger', size: 'sm' } }}>
                      {compacting ? 'Compacting…' : 'Compact Disk'}
                    </ZestButton>
                  </div>
                </div>

                {wslReport?.error && (
                  <p className={styles.cardDetail} style={{ color: '#e8a838', marginTop: 8 }}>
                    {wslReport.error}
                  </p>
                )}

                {wslReport && wslReport.distros.length > 0 && (
                  <div style={{ marginTop: 12 }}>
                    <p className={styles.cardDetail} style={{ fontWeight: 600, marginBottom: 4 }}>Distros</p>
                    {wslReport.distros.map(d => (
                      <span key={d.name} className={styles.cardDetail}
                        style={{ display: 'inline-block', marginRight: 8, marginBottom: 4 }}>
                        {d.name} · {d.state}
                      </span>
                    ))}
                  </div>
                )}

                {wslReport && wslReport.vhdxFiles.length > 0 && (
                  <div style={{ marginTop: 12 }}>
                    <p className={styles.cardDetail} style={{ fontWeight: 600, marginBottom: 4 }}>Virtual disks</p>
                    {wslReport.vhdxFiles.map(f => (
                      <div key={f.path} className={styles.cardDetail}
                        style={{ display: 'flex', justifyContent: 'space-between', gap: 12, padding: '6px 0', borderBottom: '1px solid rgba(255,255,255,0.08)' }}>
                        <span style={{ wordBreak: 'break-all' }}>{f.path}</span>
                        <span style={{ whiteSpace: 'nowrap', color: '#C9A84C' }}>{formatBytes(f.sizeBytes)}</span>
                      </div>
                    ))}
                  </div>
                )}

                {compactResult && (
                  <div style={{ marginTop: 12 }}>
                    <p className={styles.cardDetail} style={{ fontWeight: 600, color: compactResult.succeeded ? '#6FCF97' : '#e8a838' }}>
                      {compactResult.succeeded ? 'Compaction complete' : compactResult.blockedReason}
                    </p>
                    {compactResult.messages.map((m, i) => (
                      <p key={i} className={styles.cardDetail} style={{ fontSize: 12 }}>{m}</p>
                    ))}
                  </div>
                )}
              </div>
            </div>
          )}
        </ZestResponsiveLayout>
      </AppShell>
    </>
  );
}

function RegistryForm({ initial, isEdit, onSave, onCancel, awsProfiles, allTags }: {
  initial: RegistryInput;
  isEdit: boolean;
  awsProfiles: IAwsProfileResource[];
  allTags: string[];
  onSave: (r: RegistryInput) => Promise<void>;
  onCancel: () => void;
}) {
  const [form, setForm] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [checking, setChecking] = useState(false);
  const [checkResult, setCheckResult] = useState<IAwsProfileValidationResult | null>(null);
  const serverMode = useServerMode();

  const set = (field: string, value: string) => setForm(prev => ({ ...prev, [field]: value }));
  const setTags = (tags: string[]) => setForm(prev => ({ ...prev, tags }));

  const isEcr = form.authType === 'AwsEcr';
  const whereLabel = serverMode === 'cloud' ? 'the server' : 'this machine';

  const testProfile = async () => {
    if (!form.awsProfileResourceId) return;
    setChecking(true);
    setCheckResult(null);
    try {
      const r = await api.post<IAwsProfileValidationResult>('/api/resources/aws-profiles/validate', {
        profileId: form.awsProfileResourceId,
        profile: null,
      });
      setCheckResult(r);
      if (r.ok) toast.success('Profile connection OK');
    } catch {
      toast.error('Profile test failed.');
    } finally {
      setChecking(false);
    }
  };

  const profileGuide = checkResult ? guidedErrorFor(checkResult) : null;

  const handleSubmit = async () => {
    if (!form.name.trim()) { toast.error('Name is required.'); return; }
    if (!form.registry.trim()) { toast.error('Registry is required.'); return; }
    if (isEcr && !form.awsRegion?.trim() && !form.awsProfileResourceId) {
      toast.error('Set an AWS region or choose an AWS profile for ECR auth.');
      return;
    }
    setSaving(true);
    try { await onSave(form); }
    catch { toast.error('Save failed.'); }
    finally { setSaving(false); }
  };

  return (
    <div>
      <div className={styles.formRow}>
        <label className={styles.label}>Name <span style={{ color: '#C9A84C' }}>*</span></label>
        <ZestTextbox value={form.name} onChange={e => set('name', e.target.value)}
          placeholder="e.g. Company GHCR" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Registry <span style={{ color: '#C9A84C' }}>*</span></label>
        <ZestTextbox value={form.registry} onChange={e => set('registry', e.target.value)}
          placeholder="ghcr.io or 123456789012.dkr.ecr.us-east-1.amazonaws.com" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Auth Type</label>
        <select
          value={form.authType ?? 'Password'}
          onChange={e => set('authType', e.target.value)}
          style={{
            width: '100%', background: '#131D30', color: '#F0F2F5',
            border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
            padding: '8px 12px', fontSize: 14,
          }}
        >
          <option value="Password">Password / Token</option>
          <option value="AwsEcr">AWS ECR</option>
        </select>
      </div>
      {isEcr && (
        <>
          <div className={styles.formRow}>
            <label className={styles.label}>AWS Profile</label>
            <select
              value={form.awsProfileResourceId ?? ''}
              onChange={e => set('awsProfileResourceId', e.target.value)}
              style={{
                width: '100%', background: '#131D30', color: '#F0F2F5',
                border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
                padding: '8px 12px', fontSize: 14,
              }}
            >
              <option value="">— None (use region + AWS CLI env) —</option>
              {awsProfiles.map(p => (
                <option key={p.id} value={p.id}>{p.name}</option>
              ))}
            </select>
            <div style={{ fontSize: 11, color: '#637389', marginTop: 4 }}>
              Optional — a saved AWS credential used for{' '}
              <code>aws ecr get-login-password</code> on {whereLabel}.
            </div>
            {form.awsProfileResourceId && (
              <>
                <button
                  type="button"
                  onClick={testProfile}
                  disabled={checking}
                  style={{
                    marginTop: 6, background: 'rgba(201,168,76,0.12)', color: '#C9A84C',
                    border: '1px solid rgba(201,168,76,0.4)', borderRadius: 6,
                    padding: '4px 10px', fontSize: 12, cursor: 'pointer',
                  }}
                >
                  {checking ? 'Testing…' : 'Validate profile — does it work?'}
                </button>
                {checkResult?.ok && (
                  <div style={{ marginTop: 6, fontSize: 12, color: '#7CD9A8' }}>
                    ✓ Connected{checkResult.arn ? ` (${checkResult.arn})` : ''}
                  </div>
                )}
                {profileGuide && (
                  <div style={{
                    marginTop: 6, border: '1px solid rgba(224,102,102,0.5)', borderRadius: 6,
                    padding: '8px 10px', fontSize: 12, background: 'rgba(224,102,102,0.08)',
                  }}>
                    <div style={{ color: '#E06060', fontWeight: 600 }}>{profileGuide.title}</div>
                    {profileGuide.hint && <div style={{ color: '#C7D2E0', marginTop: 4 }}>{profileGuide.hint}</div>}
                    {checkResult?.errorCode === 'aws-cli-missing' && <AwsCliInstallButton onInstalled={() => testProfile()} />}
                  </div>
                )}
              </>
            )}
            <InfoTip title="What does this do?" id="reg-profile-tip">
              {`When a build pushes to this registry, ShipRight runs "aws ecr get-login-password" on ${whereLabel} to mint a temporary token. This profile decides which keys that command uses.`}
            </InfoTip>
          </div>
          <div className={styles.formRow}>
            <label className={styles.label}>AWS Region {!form.awsProfileResourceId ? <span style={{ color: '#C9A84C' }}>*</span> : ''}</label>
            <ZestTextbox value={form.awsRegion ?? ''} onChange={e => set('awsRegion', e.target.value)}
              placeholder="us-east-1" zest={{ stretch: true }} list="aws-region-suggestions" />
            <InfoTip title="Which region?" id="reg-region-tip">
              The AWS region of your ECR registry (from the registry URL, e.g. 123456789012.dkr.ecr.us-east-1.amazonaws.com → us-east-1).
            </InfoTip>
          </div>
          <div className={styles.formRow}>
            <label className={styles.label}>Tags</label>
            <TagsInput value={form.tags ?? []} onChange={setTags} allTags={allTags} id="cregistry-tags" />
          </div>
        </>
      )}
      {!isEcr && (
        <>
          <div className={styles.formRow}>
            <label className={styles.label}>Username</label>
            <ZestTextbox value={form.username} onChange={e => set('username', e.target.value)}
              placeholder="docker username" zest={{ stretch: true }} />
          </div>
          <div className={styles.formRow}>
            <label className={styles.label}>Password / Token</label>
            <ZestTextbox value={form.password ?? ''} onChange={e => set('password', e.target.value)}
              placeholder="ghp_xxxx or registry token" zest={{ stretch: true }} />
          </div>
        </>
      )}
      {!isEcr && (
        <div className={styles.formRow}>
          <label className={styles.label}>Tags</label>
          <TagsInput value={form.tags ?? []} onChange={setTags} allTags={allTags} id="cregistry-tags" />
        </div>
      )}
      <div className={styles.footer}>
        <ZestButton onClick={handleSubmit} disabled={saving}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}>
          {saving ? 'Saving…' : isEdit ? 'Update Registry' : 'Create Registry'}
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
          Cancel
        </ZestButton>
      </div>
      <datalist id="aws-region-suggestions">
        {commonAwsRegions.map(r => <option key={r} value={r} />)}
      </datalist>
    </div>
  );
}

function ScriptForm({ initial, isEdit, onSave, onCancel }: {
  initial: ScriptInput;
  isEdit: boolean;
  onSave: (s: ScriptInput) => Promise<void>;
  onCancel: () => void;
}) {
  const [form, setForm] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [newVarName, setNewVarName] = useState('');
  const [newVarValue, setNewVarValue] = useState('');

  const set = (field: string, value: unknown) => setForm(prev => ({ ...prev, [field]: value }));

  const addVariable = () => {
    const name = newVarName.trim();
    if (!name) return;
    set('variables', { ...(form.variables || {}), [name]: newVarValue });
    setNewVarName('');
    setNewVarValue('');
  };

  const removeVariable = (name: string) => {
    const vars = { ...(form.variables || {}) };
    delete vars[name];
    set('variables', Object.keys(vars).length > 0 ? vars : undefined);
  };

  const handleSubmit = async () => {
    if (!form.name.trim()) { toast.error('Name is required.'); return; }
    setSaving(true);
    try { await onSave(form); }
    catch { toast.error('Save failed.'); }
    finally { setSaving(false); }
  };

  return (
    <div>
      <div className={styles.formRow}>
        <label className={styles.label}>Name <span style={{ color: '#C9A84C' }}>*</span></label>
        <ZestTextbox value={form.name} onChange={e => set('name', e.target.value)}
          placeholder="e.g. Deploy Script" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Platform</label>
        <select
          value={form.platform}
          onChange={e => set('platform', e.target.value)}
          style={{
            width: '100%', background: '#131D30', color: '#F0F2F5',
            border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
            padding: '8px 12px', fontSize: 14,
          }}
        >
          <option value="Bash">Bash</option>
          <option value="PowerShell">PowerShell</option>
          <option value="Cmd">Cmd</option>
          <option value="Python">Python</option>
          <option value="Sh">Sh</option>
        </select>
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Target</label>
        <select
          value={form.target}
          onChange={e => set('target', e.target.value)}
          style={{
            width: '100%', background: '#131D30', color: '#F0F2F5',
            border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
            padding: '8px 12px', fontSize: 14,
          }}
        >
          <option value="Local">Local</option>
          <option value="Remote">Remote</option>
        </select>
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Scope</label>
        <select
          value={form.scope}
          onChange={e => set('scope', e.target.value)}
          style={{
            width: '100%', background: '#131D30', color: '#F0F2F5',
            border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
            padding: '8px 12px', fontSize: 14,
          }}
        >
          <option value="Global">Global</option>
          <option value="Project">Project</option>
        </select>
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Script Content</label>
        <textarea
          value={form.content}
          onChange={e => set('content', e.target.value)}
          placeholder="#!/bin/bash&#10;echo 'Hello from ShipRight'"
          rows={12}
          style={{
            width: '100%', fontFamily: 'monospace', fontSize: 13,
            background: '#131D30', color: '#F0F2F5',
            border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
            padding: '8px 10px', resize: 'vertical',
          }}
        />
      </div>

      <button
        onClick={() => setShowAdvanced(!showAdvanced)}
        style={{
          background: 'none', border: 'none', color: '#C9A84C',
          cursor: 'pointer', fontSize: 13, marginBottom: 12,
        }}
      >
        {showAdvanced ? '▼ Hide' : '▶ Show'} Advanced
      </button>

      {showAdvanced && (
        <>
          <div className={styles.formRow}>
            <label className={styles.label}>Default Working Directory</label>
            <ZestTextbox value={form.workingDirectory || ''} onChange={e => set('workingDirectory', e.target.value || undefined)}
              placeholder="{ProjectDir}" zest={{ stretch: true }} />
            <div style={{ fontSize: 11, color: '#637389', marginTop: 4 }}>
              Default: {'{ProjectDir}'}. Variables: {'{ProjectDir}'} {'{ScriptDir}'} {'{TempDir}'}
            </div>
          </div>
          <div className={styles.formRow}>
            <label className={styles.label}>Default Variables</label>
            {(form.variables && Object.keys(form.variables).length > 0) && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginBottom: 8 }}>
                {Object.entries(form.variables).map(([name, value]) => (
                  <div key={name} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '4px 8px', background: '#1A2540', borderRadius: 4, fontSize: 13 }}>
                    <span style={{ color: '#C9A84C', fontFamily: 'monospace' }}>{'{' + name + '}'}</span>
                    <span style={{ flex: 1, color: '#F0F2F5', fontFamily: 'monospace', overflow: 'hidden', textOverflow: 'ellipsis' }}>{value}</span>
                    <button onClick={() => removeVariable(name)} style={{ background: 'none', border: 'none', color: '#637389', cursor: 'pointer' }}>×</button>
                  </div>
                ))}
              </div>
            )}
            <div style={{ display: 'flex', gap: 4 }}>
              <input
                value={newVarName}
                onChange={e => setNewVarName(e.target.value)}
                placeholder="Name"
                style={{ width: 100, background: '#131D30', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 4, color: '#F0F2F5', padding: '4px 8px', fontSize: 13, fontFamily: 'monospace' }}
              />
              <input
                value={newVarValue}
                onChange={e => setNewVarValue(e.target.value)}
                placeholder="Value (e.g. {ProjectDir}/deploy)"
                style={{ flex: 1, background: '#131D30', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 4, color: '#F0F2F5', padding: '4px 8px', fontSize: 13, fontFamily: 'monospace' }}
              />
              <ZestButton onClick={addVariable} disabled={!newVarName.trim()} zest={{ buttonStyle: 'outline' }}>
                +
              </ZestButton>
            </div>
          </div>
        </>
      )}

      <div className={styles.footer}>
        <ZestButton onClick={handleSubmit} disabled={saving}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}>
          {saving ? 'Saving…' : isEdit ? 'Update Script' : 'Create Script'}
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
          Cancel
        </ZestButton>
      </div>
    </div>
  );
}

function CredentialForm({ initial, isEdit, onSave, onCancel, allTags }: {
  initial: CredentialInput;
  isEdit: boolean;
  allTags: string[];
  onSave: (c: CredentialInput) => Promise<void>;
  onCancel: () => void;
}) {
  const [form, setForm] = useState(initial);
  const [saving, setSaving] = useState(false);

  const set = (field: string, value: string) => setForm(prev => ({ ...prev, [field]: value }));
  const setTags = (tags: string[]) => setForm(prev => ({ ...prev, tags }));

  const handleSubmit = async () => {
    if (!form.name.trim()) { toast.error('Name is required.'); return; }
    if (!form.value.trim()) { toast.error('Value is required.'); return; }
    setSaving(true);
    try { await onSave(form); }
    catch { toast.error('Save failed.'); }
    finally { setSaving(false); }
  };

  return (
    <div>
      <div className={styles.formRow}>
        <label className={styles.label}>Name <span style={{ color: '#C9A84C' }}>*</span></label>
        <ZestTextbox value={form.name} onChange={e => set('name', e.target.value)}
          placeholder="e.g. Azure DevOps PAT" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Value (PAT / Token / Password) <span style={{ color: '#C9A84C' }}>*</span></label>
        <ZestTextbox value={form.value} onChange={e => set('value', e.target.value)}
          placeholder="ghp_xxxxxxxxxxxxxx" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Host Pattern (optional)</label>
        <ZestTextbox value={form.hostPattern ?? ''} onChange={e => set('hostPattern', e.target.value || '')}
          placeholder="e.g. dev.azure.com or github.com" zest={{ stretch: true }} />
        <div style={{ fontSize: 11, color: '#637389', marginTop: 4 }}>
          Used to select this credential automatically. Leave empty for manual assignment.
        </div>
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Tags</label>
        <TagsInput value={form.tags ?? []} onChange={setTags} allTags={allTags} id="ccred-tags" />
      </div>
      <div className={styles.footer}>
        <ZestButton onClick={handleSubmit} disabled={saving}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}>
          {saving ? 'Saving…' : isEdit ? 'Update Credential' : 'Create Credential'}
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
          Cancel
        </ZestButton>
      </div>
    </div>
  );
}
