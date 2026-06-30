import Head from 'next/head';
import Link from 'next/link';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import { ZestResponsiveLayout } from 'jattac.libs.web.zest-responsive-layout';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import OverflowMenu from 'jattac.libs.web.overflow-menu';
import AppShell from '@/modules/AppShell/AppShell';
import { api } from '@/shared/ApiService';
import { IServerConfig, IProject, DeployMode } from '@/shared/types/IProject';
import styles from './Styles/Servers.module.css';

type ServerInput = Omit<IServerConfig, 'id'> & { id?: string };
const emptyServer = (): ServerInput => ({
  name: '', host: '', username: 'ubuntu', sshKeyPath: '',
  remoteWorkingDir: '', rebuildScript: 'rebuild.sh', deployMode: 'GitScript' as DeployMode,
});

export default function ServersPage() {
  const [servers, setServers] = useState<IServerConfig[]>([]);
  const [projects, setProjects] = useState<IProject[]>([]);
  const [loading, setLoading] = useState(true);
  const [paneTarget, setPaneTarget] = useState<ServerInput | undefined>(undefined);

  const load = async () => {
    setLoading(true);
    try {
      const [s, p] = await Promise.all([
        api.get<IServerConfig[]>('/api/servers'),
        api.get<IProject[]>('/api/projects'),
      ]);
      setServers(s);
      setProjects(p);
    } catch { toast.error('Failed to load data.'); }
    finally { setLoading(false); }
  };

  useEffect(() => { load(); }, []);

  const openNew = () => setPaneTarget(emptyServer());
  const openEdit = (s: IServerConfig) => setPaneTarget(s);
  const closePane = () => setPaneTarget(undefined);

  const handleSave = async (input: ServerInput) => {
    if (input.id) {
      await api.put(`/api/servers/${input.id}`, input);
      toast.success('Server updated.');
    } else {
      await api.post('/api/servers', input);
      toast.success('Server created.');
    }
    closePane();
    load();
  };

  const handleDelete = async (s: IServerConfig) => {
    try {
      await api.delete(`/api/servers/${s.id}`);
      toast.success(`'${s.name || s.host}' deleted.`);
      setServers(prev => prev.filter(x => x.id !== s.id));
    } catch { toast.error('Failed to delete server.'); }
  };

  const projectsByServer = (serverId: string) =>
    projects.filter(p => p.serverId === serverId);

  const paneOpen = paneTarget !== undefined;

  return (
    <>
      <Head><title>ShipRight — Servers</title></Head>
      <AppShell>
        <ZestResponsiveLayout
          sidePaneWidth="480px"
          closeOnDesktopOverlayClick
          sidePane={{
            visible: paneOpen,
            title: paneTarget?.id ? `Edit: ${paneTarget.name || paneTarget.host}` : 'New Server',
            onClose: closePane,
            content: paneOpen ? (
              <ServerForm initial={paneTarget} onSave={handleSave} onCancel={closePane} />
            ) : undefined,
          }}
        >
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 24 }}>
            <h1 className={styles.heading}>Servers</h1>
            <ZestButton onClick={openNew}
              zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
              New Server
            </ZestButton>
          </div>

          <div className={styles.grid}>
            {loading && [0, 1, 2].map(i => (
              <div key={i} className={`${styles.card} ${styles.skeletonCard}`}>
                <div className={`skeleton ${styles.skeletonTitle}`} />
                <div className={styles.skeletonChips}>
                  <div className={`skeleton ${styles.skeletonChip}`} />
                </div>
                <div className={`skeleton ${styles.skeletonLink}`} />
              </div>
            ))}
            {!loading && servers.map(s => {
              const linked = projectsByServer(s.id!);
              return (
                <div key={s.id} className={styles.card}>
                  <div className={styles.cardTop}>
                    <div>
                      <h3 className={styles.cardTitle}>{s.name || s.host}</h3>
                      <p className={styles.cardHost}>{s.username}@{s.host}</p>
                      <p className={styles.cardDetail}>Key: {s.sshKeyPath.split('/').pop()}</p>
                    </div>
                    <OverflowMenu items={[
                      { content: 'Edit', onClick: () => openEdit(s) },
                      { content: 'Delete', onClick: () => handleDelete(s) },
                    ]} />
                  </div>
                  <p className={styles.cardDetail}>
                    {s.deployMode === 'EnvCompose' ? 'Env + Compose' : s.deployMode === 'GitCompose' ? 'Git + Compose' : 'Git + Script'}
                    {s.remoteWorkingDir ? ` · ${s.remoteWorkingDir}` : ''}
                  </p>
                  {linked.length > 0 && (
                    <div className={styles.projectsSection}>
                      <div className={styles.projectsLabel}>Projects ({linked.length})</div>
                      <div>
                        {linked.map(p => (
                          <Link key={p.id} href={`/projects/?detail=${p.id}`} className={styles.projectChip}>
                            {p.name}
                          </Link>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>

          {!loading && servers.length === 0 && (
            <p className={styles.empty}>
              No servers registered.{' '}
              <button onClick={openNew}
                style={{ background: 'none', border: 'none', color: '#C9A84C', cursor: 'pointer' }}>
                Add one
              </button>.
            </p>
          )}
        </ZestResponsiveLayout>
      </AppShell>
    </>
  );
}

function ServerForm({ initial, onSave, onCancel }: {
  initial: ServerInput;
  onSave: (s: ServerInput) => Promise<void>;
  onCancel: () => void;
}) {
  const [form, setForm] = useState<ServerInput>(initial);
  const [saving, setSaving] = useState(false);

  const set = (field: keyof ServerInput, value: string) =>
    setForm(prev => ({ ...prev, [field]: value }));

  const handleSubmit = async () => {
    if (!form.host.trim()) { toast.error('Host is required.'); return; }
    if (!(form.name ?? '').trim()) { toast.error('Server name is required.'); return; }
    setSaving(true);
    try { await onSave(form); }
    catch { toast.error('Save failed.'); }
    finally { setSaving(false); }
  };

  return (
    <div>
      <div className={styles.formRow}>
        <label className={styles.label}>Display Name <span style={{ color: '#C9A84C' }}>*</span></label>
        <ZestTextbox value={form.name} onChange={e => set('name', e.target.value)}
          placeholder="e.g. Production Web" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Host</label>
        <ZestTextbox value={form.host} onChange={e => set('host', e.target.value)}
          placeholder="3.130.65.46" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Username</label>
        <ZestTextbox value={form.username} onChange={e => set('username', e.target.value)}
          placeholder="ubuntu" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>SSH Key Path</label>
        <ZestTextbox value={form.sshKeyPath} onChange={e => set('sshKeyPath', e.target.value)}
          placeholder="/home/nyingi/.../key.pem" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Remote Working Directory</label>
        <ZestTextbox value={form.remoteWorkingDir} onChange={e => set('remoteWorkingDir', e.target.value)}
          placeholder="/home/ubuntu/jattac-docker" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Rebuild Script</label>
        <ZestTextbox value={form.rebuildScript} onChange={e => set('rebuildScript', e.target.value)}
          placeholder="rebuild.sh" zest={{ stretch: true }} />
      </div>
      <div className={styles.formRow}>
        <label className={styles.label}>Deploy Mode</label>
        <select value={form.deployMode ?? 'GitScript'}
          onChange={e => set('deployMode', e.target.value)}
          style={{ background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', width: '100%' }}>
          <option value="GitScript">Git + Script</option>
          <option value="GitCompose">Git + Compose</option>
          <option value="EnvCompose">Env + Compose</option>
        </select>
      </div>
      <div className={styles.footer}>
        <ZestButton onClick={handleSubmit} disabled={saving}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}>
          {saving ? 'Saving…' : initial.id ? 'Update Server' : 'Create Server'}
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
          Cancel
        </ZestButton>
      </div>
    </div>
  );
}
