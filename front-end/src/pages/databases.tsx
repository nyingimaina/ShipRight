import Head from 'next/head';
import { useEffect, useState } from 'react';
import ReactSelect from 'react-select';
import ZestButton from 'jattac.libs.web.zest-button';
import AppShell from '@/modules/AppShell/AppShell';
import DbOperationsPanel from '@/modules/Database/DbOperationsPanel';
import { api } from '@/shared/ApiService';
import { IServerConfig, IDatabaseConfig, emptyDatabaseConfig, DbProviderType } from '@/shared/types/IProject';
import styles from './Styles/Databases.module.css';

const selectStyles = {
  control:     (b: object) => ({ ...b, background: '#1A2640', border: '1px solid rgba(255,255,255,0.08)', minHeight: 36, minWidth: 280 }),
  menu:        (b: object) => ({ ...b, background: '#1A2640', zIndex: 20 }),
  option:      (b: object, s: { isFocused: boolean }) => ({ ...b, background: s.isFocused ? '#1F2E4A' : 'transparent', color: '#F0F2F5' }),
  singleValue: (b: object) => ({ ...b, color: '#F0F2F5' }),
  placeholder: (b: object) => ({ ...b, color: '#637389' }),
  input:       (b: object) => ({ ...b, color: '#F0F2F5' }),
};

const inputStyle: React.CSSProperties = {
  width: '100%', background: '#131D30', color: '#F0F2F5',
  border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
  padding: '6px 10px', fontSize: 14, boxSizing: 'border-box',
};

interface ContainerInfo { name: string; image: string; status: string; }

export default function DatabasesPage() {
  const [servers, setServers] = useState<IServerConfig[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [containers, setContainers] = useState<ContainerInfo[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [dbConfig, setDbConfig] = useState<IDatabaseConfig>(emptyDatabaseConfig());
  const [managing, setManaging] = useState(false);

  useEffect(() => {
    api.get<IServerConfig[]>('/api/servers')
      .then(s => { setServers(s); if (!selected && s.length > 0) setSelected(s[0].id!); })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  const current = servers.find(s => s.id === selected);
  const options = servers.map(s => ({ value: s.id!, label: s.name || s.host }));

  const listContainers = async () => {
    if (!selected) return;
    setBusy(true);
    setContainers(null);
    setManaging(false);
    try {
      const c = await api.get<ContainerInfo[]>(`/api/servers/${selected}/db/containers`);
      setContainers(c);
    } catch { setContainers([]); }
    finally { setBusy(false); }
  };

  const startManaging = (container: string, image: string) => {
    const img = image.toLowerCase();
    const provider: DbProviderType = (img.includes('mssql') || img.includes('mcr.microsoft')) ? 'SqlServer' : 'MariaDb';
    setDbConfig({
      provider,
      containerName: container,
      databaseName: '',
      rootUser: provider === 'MariaDb' ? 'root' : 'sa',
      backupRetainCount: 10,
    });
    setManaging(true);
  };

  const setCfg = <K extends keyof IDatabaseConfig>(key: K, value: IDatabaseConfig[K]) =>
    setDbConfig(prev => ({ ...prev, [key]: value }));

  return (
    <>
      <Head><title>ShipRight — Databases</title></Head>
      <AppShell>
        <h1 className={styles.heading}>Database Manager</h1>
        <p className={styles.sub}>Manage databases on any registered server — backup, restore, and run SQL queries.</p>

        <div className={styles.serverSelect}>
          <span className={styles.selectLabel}>Server</span>
          <ReactSelect
            options={options}
            styles={selectStyles}
            placeholder={loading ? 'Loading…' : 'Select a server'}
            value={options.find(o => o.value === selected) ?? null}
            onChange={opt => { setSelected(opt?.value ?? null); setContainers(null); setManaging(false); }}
            isDisabled={loading}
          />
        </div>

        {current && !managing && (
          <div className={styles.actions}>
            <ZestButton onClick={listContainers} disabled={busy} zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}>
              {busy ? 'Loading…' : 'List Containers'}
            </ZestButton>
          </div>
        )}

        {containers && containers.length > 0 && !managing && (
          <table style={{ width: '100%', borderCollapse: 'collapse', marginBottom: 24 }}>
            <thead>
              <tr style={{ textAlign: 'left', color: '#637389', fontSize: 11, textTransform: 'uppercase' }}>
                <th style={{ padding: '8px 12px', borderBottom: '1px solid rgba(255,255,255,0.08)' }}>Name</th>
                <th style={{ padding: '8px 12px', borderBottom: '1px solid rgba(255,255,255,0.08)' }}>Image</th>
                <th style={{ padding: '8px 12px', borderBottom: '1px solid rgba(255,255,255,0.08)' }}>Status</th>
                <th style={{ padding: '8px 12px', borderBottom: '1px solid rgba(255,255,255,0.08)' }} />
              </tr>
            </thead>
            <tbody>
              {containers.map(c => (
                <tr key={c.name} style={{ borderBottom: '1px solid rgba(255,255,255,0.04)' }}>
                  <td style={{ padding: '8px 12px' }}>{c.name}</td>
                  <td style={{ padding: '8px 12px', color: '#A8B8CC', fontSize: 13 }}>{c.image}</td>
                  <td style={{ padding: '8px 12px', color: '#A8B8CC', fontSize: 13 }}>{c.status}</td>
                  <td style={{ padding: '8px 12px' }}>
                    <ZestButton
                      onClick={() => startManaging(c.name, c.image)}
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
                      Manage
                    </ZestButton>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {containers && containers.length === 0 && !managing && (
          <div className={styles.empty}>No containers found on this server.</div>
        )}

        {managing && (
          <div style={{ marginTop: 8 }}>
            {/* DB Config form */}
            <div style={{ background: '#0D1625', border: '1px solid rgba(255,255,255,0.08)',
              borderRadius: 10, padding: '16px 20px', marginBottom: 20, display: 'flex',
              flexDirection: 'column', gap: 12 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <span style={{ fontSize: 13, color: '#C9A84C', fontWeight: 600 }}>
                  Managing: {dbConfig.containerName}
                </span>
                <ZestButton zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                  onClick={() => setManaging(false)}>
                  Change container
                </ZestButton>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
                <div>
                  <label style={{ display: 'block', fontSize: 11, color: '#637389', marginBottom: 4 }}>Provider</label>
                  <select style={inputStyle} value={dbConfig.provider}
                    onChange={e => setCfg('provider', e.target.value as DbProviderType)}>
                    <option value="MariaDb">MariaDB</option>
                    <option value="SqlServer">SQL Server</option>
                  </select>
                </div>
                <div>
                  <label style={{ display: 'block', fontSize: 11, color: '#637389', marginBottom: 4 }}>Root user</label>
                  <input style={inputStyle} value={dbConfig.rootUser}
                    onChange={e => setCfg('rootUser', e.target.value)} />
                </div>
                <div style={{ gridColumn: '1 / -1' }}>
                  <label style={{ display: 'block', fontSize: 11, color: '#637389', marginBottom: 4 }}>
                    Password <span style={{ color: '#637389', fontStyle: 'italic' }}>(optional — overrides env var)</span>
                  </label>
                  <input style={inputStyle} type="password" value={dbConfig.rootPassword ?? ''}
                    placeholder={dbConfig.provider === 'MariaDb' ? '$MYSQL_ROOT_PASSWORD' : '$SA_PASSWORD'}
                    onChange={e => setCfg('rootPassword', e.target.value)} />
                </div>
                <div>
                  <label style={{ display: 'block', fontSize: 11, color: '#637389', marginBottom: 4 }}>Database name *</label>
                  <input style={inputStyle} placeholder="e.g. myapp" value={dbConfig.databaseName}
                    onChange={e => setCfg('databaseName', e.target.value)} />
                </div>
                <div>
                  <label style={{ display: 'block', fontSize: 11, color: '#637389', marginBottom: 4 }}>Backup retention</label>
                  <input style={inputStyle} type="number" min={1} value={dbConfig.backupRetainCount}
                    onChange={e => setCfg('backupRetainCount', Math.max(1, parseInt(e.target.value) || 10))} />
                </div>
              </div>
            </div>

            {dbConfig.databaseName ? (
              <DbOperationsPanel
                apiBase={`/api/servers/${selected}/db`}
                dbConfig={dbConfig}
              />
            ) : (
              <p style={{ fontSize: 13, color: '#C9943A' }}>
                Enter a database name above to start managing this container.
              </p>
            )}
          </div>
        )}
      </AppShell>
    </>
  );
}