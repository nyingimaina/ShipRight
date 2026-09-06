import { useEffect, useState } from 'react';
import CreatableSelect from 'react-select/creatable';
import { RiLoader2Line } from 'react-icons/ri';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { api } from '@/shared/ApiService';
import { DbProviderType } from '@/shared/types/IProject';
import styles from '../Styles/ProjectSetupWizard.module.css';

export interface DatabaseDraft {
  enabled: boolean;
  provider: DbProviderType;
  containerName: string;
  databaseName: string;
  rootUser: string;
  rootPassword: string;
}

interface Props {
  draft: DatabaseDraft;
  existingId?: string;
  serverDraft: { host: string; username: string; sshKeyPath: string };
  refreshToken: number;
  onDraftChange: (patch: Partial<DatabaseDraft>) => void;
}

export default function DatabaseAtom({ draft, existingId, serverDraft, refreshToken, onDraftChange }: Props) {
  const [containers, setContainers] = useState<{ name: string; image: string }[]>([]);
  const [databases, setDatabases] = useState<string[]>([]);
  const [loadingContainers, setLoadingContainers] = useState(false);
  const [loadingDatabases, setLoadingDatabases] = useState(false);

  useEffect(() => {
    if (draft.enabled && refreshToken > 0) detectContainers();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refreshToken]);

  const detectContainers = async () => {
    setLoadingContainers(true);
    setContainers([]);
    setDatabases([]);
    try {
      const list = existingId
        ? await api.get<{ name: string; image: string }[]>(`/api/projects/${existingId}/db/containers`)
        : await api.post<{ name: string; image: string }[]>(
            '/api/servers/db/containers-inline', { host: serverDraft.host, username: serverDraft.username, sshKeyPath: serverDraft.sshKeyPath });
      setContainers(list ?? []);
    } catch {
      setContainers([]);
    } finally {
      setLoadingContainers(false);
    }
  };

  const detectDatabases = async (containerName: string, provider: DbProviderType) => {
    if (!containerName) return;
    setLoadingDatabases(true);
    setDatabases([]);
    try {
      const list = existingId
        ? await api.post<string[]>(`/api/projects/${existingId}/db/databases`,
            { container: containerName, provider, rootUser: draft.rootUser, rootPassword: draft.rootPassword })
        : await api.post<string[]>('/api/servers/db/databases-inline',
            { host: serverDraft.host, username: serverDraft.username, sshKeyPath: serverDraft.sshKeyPath, container: containerName, provider, rootUser: draft.rootUser, rootPassword: draft.rootPassword });
      setDatabases(list ?? []);
    } catch {
      setDatabases([]);
    } finally {
      setLoadingDatabases(false);
    }
  };

  const onProviderChange = (p: DbProviderType) => {
    onDraftChange({ provider: p, rootUser: p === 'MariaDb' ? 'root' : 'sa', containerName: '', databaseName: '' });
    setContainers([]);
    setDatabases([]);
  };

  if (!draft.enabled) {
    return (
      <div className={styles.section}>
        <span className={styles.sectionTitle}>Database (optional)</span>
        <div className={styles.fieldRow} style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
          <label className={styles.fieldLabel} style={{ marginBottom: 0 }}>Enable database management</label>
          <input type="checkbox" checked={false} onChange={e => onDraftChange({ enabled: e.target.checked })}
            style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
        </div>
      </div>
    );
  }

  return (
    <div className={styles.section}>
      <span className={styles.sectionTitle}>Database (optional)</span>
      <div className={styles.fieldRow} style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
        <label className={styles.fieldLabel} style={{ marginBottom: 0 }}>Enable database management</label>
        <input type="checkbox" checked={true} onChange={e => onDraftChange({ enabled: e.target.checked })}
          style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
      </div>
      <div className={styles.fieldRow}>
        <label className={styles.fieldLabel}>Provider</label>
        <select value={draft.provider}
          onChange={e => onProviderChange(e.target.value as DbProviderType)}
          style={{ background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', width: '100%' }}>
          <option value="MariaDb">MariaDB</option>
          <option value="SqlServer">SQL Server</option>
        </select>
      </div>
      <div className={styles.fieldRow}>
        <label className={styles.fieldLabel}>Root user</label>
        <ZestTextbox value={draft.rootUser} onChange={e => onDraftChange({ rootUser: e.target.value })}
          placeholder="root" zest={{ stretch: true }} />
      </div>
      <div className={styles.fieldRow}>
        <label className={styles.fieldLabel}>Password <span style={{ color: '#637389', fontWeight: 400 }}>(optional)</span></label>
        <ZestTextbox value={draft.rootPassword} onChange={e => onDraftChange({ rootPassword: e.target.value })}
          placeholder={`Leave blank to use $${draft.provider === 'MariaDb' ? 'MYSQL_ROOT_PASSWORD' : 'SA_PASSWORD'} env var`}
          type="password" zest={{ stretch: true }} />
      </div>
      <div className={styles.fieldRow}>
        <label className={styles.fieldLabel}>Container name</label>
        {loadingContainers ? (
          <span className={styles.spinnerRow}><RiLoader2Line className={styles.spinnerIcon} /> Detecting containers…</span>
        ) : containers.length > 0 ? (
          <select value={draft.containerName}
            onChange={e => { onDraftChange({ containerName: e.target.value }); detectDatabases(e.target.value, draft.provider); }}
            style={{ width: '100%', background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px' }}>
            <option value="">Select a container…</option>
            {containers.map(c => <option key={c.name} value={c.name}>{c.name} — {c.image}</option>)}
          </select>
        ) : (
          <>
            <ZestTextbox value={draft.containerName} onChange={e => onDraftChange({ containerName: e.target.value })}
              placeholder="e.g. jattac-database" zest={{ stretch: true }} />
            {(serverDraft.host && serverDraft.username && serverDraft.sshKeyPath) && (
              <p className={styles.stepSub} style={{ marginTop: 4 }}>No containers found on server. Type manually or check Docker is running.</p>
            )}
          </>
        )}
      </div>
      <div className={styles.fieldRow}>
        <label className={styles.fieldLabel}>Database name</label>
        <CreatableSelect
          options={databases.map(d => ({ value: d, label: d }))}
          value={draft.databaseName ? { value: draft.databaseName, label: draft.databaseName } : null}
          onChange={(opt) => onDraftChange({ databaseName: (opt as { value: string; label: string } | null)?.value ?? '' })}
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
    </div>
  );
}
