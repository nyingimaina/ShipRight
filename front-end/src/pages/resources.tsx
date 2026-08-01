import Head from 'next/head';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import { ZestResponsiveLayout } from 'jattac.libs.web.zest-responsive-layout';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import OverflowMenu from 'jattac.libs.web.overflow-menu';
import AppShell from '@/modules/AppShell/AppShell';
import { api } from '@/shared/ApiService';
import { IDockerRegistryResource, IScriptResource, ICredentialResource, ScriptPlatform, ExecutionTarget, PipelineScope } from '@/shared/types/IProject';
import styles from './Styles/Resources.module.css';

type Tab = 'registries' | 'scripts' | 'credentials';

type RegistryInput = { id?: string; name: string; registry: string; username: string; password?: string };
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
type CredentialInput = { id?: string; name: string; value: string; hostPattern?: string; projectId?: string };

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

export default function ResourcesPage() {
  const [tab, setTab] = useState<Tab>('registries');
  const [registries, setRegistries] = useState<IDockerRegistryResource[]>([]);
  const [scripts, setScripts] = useState<IScriptResource[]>([]);
  const [credentials, setCredentials] = useState<ICredentialResource[]>([]);
  const [loading, setLoading] = useState(true);
  const [paneTarget, setPaneTarget] = useState<'new-registry' | 'edit-registry' | 'new-script' | 'edit-script' | 'new-credential' | 'edit-credential' | undefined>(undefined);
  const [editItem, setEditItem] = useState<RegistryInput | ScriptInput | CredentialInput | null>(null);

  const load = async () => {
    setLoading(true);
    try {
      const [r, s, c] = await Promise.all([
        api.get<IDockerRegistryResource[]>('/api/resources/registries'),
        api.get<IScriptResource[]>('/api/resources/scripts'),
        api.get<ICredentialResource[]>('/api/resources/credentials'),
      ]);
      setRegistries(r);
      setScripts(s);
      setCredentials(c);
    } catch { toast.error('Failed to load resources.'); }
    finally { setLoading(false); }
  };

  useEffect(() => { load(); }, []);

  const openNewRegistry = () => { setPaneTarget('new-registry'); setEditItem(null); };
  const openEditRegistry = (r: IDockerRegistryResource) => { setPaneTarget('edit-registry'); setEditItem(r); };
  const openNewScript = () => { setPaneTarget('new-script'); setEditItem(null); };
  const openEditScript = (s: IScriptResource) => { setPaneTarget('edit-script'); setEditItem(s); };
  const openNewCredential = () => { setPaneTarget('new-credential'); setEditItem(null); };
  const openEditCredential = (c: ICredentialResource) => { setPaneTarget('edit-credential'); setEditItem(c as CredentialInput); };
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

  const paneOpen = paneTarget !== undefined;

  const paneTitle = paneTarget === 'new-registry' ? 'New Registry Resource'
    : paneTarget === 'edit-registry' ? `Edit: ${(editItem as IDockerRegistryResource)?.name || ''}`
    : paneTarget === 'new-script' ? 'New Script Resource'
    : paneTarget === 'edit-script' ? `Edit: ${(editItem as IScriptResource)?.name || ''}`
    : paneTarget === 'new-credential' ? 'New Credential Resource'
    : paneTarget === 'edit-credential' ? `Edit: ${(editItem as ICredentialResource)?.name || ''}`
    : '';

  const paneContent = paneOpen && paneTarget?.includes('registry') ? (
    <RegistryForm
      initial={paneTarget === 'edit-registry' ? editItem as RegistryInput : { name: '', registry: '', username: '', password: '' }}
      isEdit={paneTarget === 'edit-registry'}
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
      onSave={handleSaveCredential}
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
            ) : (
              <ZestButton onClick={openNewCredential}
                zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
                New Credential
              </ZestButton>
            )}
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
                      <p className={styles.cardDetail}>User: {r.username}</p>
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
        </ZestResponsiveLayout>
      </AppShell>
    </>
  );
}

function RegistryForm({ initial, isEdit, onSave, onCancel }: {
  initial: RegistryInput;
  isEdit: boolean;
  onSave: (r: RegistryInput) => Promise<void>;
  onCancel: () => void;
}) {
  const [form, setForm] = useState(initial);
  const [saving, setSaving] = useState(false);

  const set = (field: string, value: string) => setForm(prev => ({ ...prev, [field]: value }));

  const handleSubmit = async () => {
    if (!form.name.trim()) { toast.error('Name is required.'); return; }
    if (!form.registry.trim()) { toast.error('Registry is required.'); return; }
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
          placeholder="ghcr.io" zest={{ stretch: true }} />
      </div>
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
      <div className={styles.footer}>
        <ZestButton onClick={handleSubmit} disabled={saving}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}>
          {saving ? 'Saving…' : isEdit ? 'Update Registry' : 'Create Registry'}
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
          Cancel
        </ZestButton>
      </div>
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

function CredentialForm({ initial, isEdit, onSave, onCancel }: {
  initial: CredentialInput;
  isEdit: boolean;
  onSave: (c: CredentialInput) => Promise<void>;
  onCancel: () => void;
}) {
  const [form, setForm] = useState(initial);
  const [saving, setSaving] = useState(false);

  const set = (field: string, value: string) => setForm(prev => ({ ...prev, [field]: value }));

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
