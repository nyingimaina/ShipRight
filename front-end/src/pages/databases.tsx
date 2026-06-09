import Head from 'next/head';
import { useEffect, useState } from 'react';
import ReactSelect from 'react-select';
import ZestButton from 'jattac.libs.web.zest-button';
import AppShell from '@/modules/AppShell/AppShell';
import { api } from '@/shared/ApiService';
import { IServerConfig } from '@/shared/types/IProject';
import styles from './Styles/Databases.module.css';

const selectStyles = {
  control:     (b: object) => ({ ...b, background: '#1A2640', border: '1px solid rgba(255,255,255,0.08)', minHeight: 36, minWidth: 280 }),
  menu:        (b: object) => ({ ...b, background: '#1A2640', zIndex: 20 }),
  option:      (b: object, s: { isFocused: boolean }) => ({ ...b, background: s.isFocused ? '#1F2E4A' : 'transparent', color: '#F0F2F5' }),
  singleValue: (b: object) => ({ ...b, color: '#F0F2F5' }),
  placeholder: (b: object) => ({ ...b, color: '#637389' }),
  input:       (b: object) => ({ ...b, color: '#F0F2F5' }),
};

interface ContainerInfo {
  name: string;
  image: string;
  status: string;
}

export default function DatabasesPage() {
  const [servers, setServers] = useState<IServerConfig[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [containers, setContainers] = useState<ContainerInfo[] | null>(null);
  const [databases, setDatabases] = useState<string[] | null>(null);
  const [busy, setBusy] = useState(false);

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
    setDatabases(null);
    try {
      const c = await api.get<ContainerInfo[]>(`/api/servers/${selected}/db/containers`);
      setContainers(c);
    } catch { setContainers([]); }
    finally { setBusy(false); }
  };

  const listDatabases = async (container: string, provider: string) => {
    if (!selected) return;
    setBusy(true);
    setDatabases(null);
    try {
      const d = await api.get<string[]>(`/api/servers/${selected}/db/databases?container=${encodeURIComponent(container)}&provider=${encodeURIComponent(provider)}`);
      setDatabases(d);
    } catch { setDatabases([]); }
    finally { setBusy(false); }
  };

  return (
    <>
      <Head><title>ShipRight — Databases</title></Head>
      <AppShell>
        <h1 className={styles.heading}>Database Explorer</h1>
        <p className={styles.sub}>Discover and explore databases on any registered server.</p>

        <div className={styles.serverSelect}>
          <span className={styles.selectLabel}>Server</span>
          <ReactSelect
            options={options}
            styles={selectStyles}
            placeholder={loading ? 'Loading…' : 'Select a server'}
            value={options.find(o => o.value === selected) ?? null}
            onChange={opt => { setSelected(opt?.value ?? null); setContainers(null); setDatabases(null); }}
            isDisabled={loading}
          />
        </div>

        {current && (
          <div className={styles.actions}>
            <ZestButton onClick={listContainers} disabled={busy} zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}>
              {busy ? 'Loading…' : 'List Containers'}
            </ZestButton>
          </div>
        )}

        {containers && containers.length > 0 && (
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
                      onClick={() => {
                        const img = c.image.toLowerCase();
                        const provider = img.includes('mssql') || img.includes('mcr.microsoft') ? 'SqlServer' : 'MariaDb';
                        listDatabases(c.name, provider);
                      }}
                      disabled={busy}
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
                      Explore
                    </ZestButton>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {containers && containers.length === 0 && (
          <div className={styles.empty}>No containers found on this server.</div>
        )}

        {databases && databases.length > 0 && (
          <>
            <h3 style={{ fontSize: 16, marginBottom: 12 }}>Databases</h3>
            <table style={{ width: '100%', borderCollapse: 'collapse' }}>
              <thead>
                <tr style={{ textAlign: 'left', color: '#637389', fontSize: 11, textTransform: 'uppercase' }}>
                  <th style={{ padding: '8px 12px', borderBottom: '1px solid rgba(255,255,255,0.08)' }}>Name</th>
                </tr>
              </thead>
              <tbody>
                {databases.map(d => (
                  <tr key={d} style={{ borderBottom: '1px solid rgba(255,255,255,0.04)' }}>
                    <td style={{ padding: '8px 12px' }}>{d}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </>
        )}

        {databases && databases.length === 0 && (
          <div className={styles.empty}>No databases found in this container.</div>
        )}
      </AppShell>
    </>
  );
}
