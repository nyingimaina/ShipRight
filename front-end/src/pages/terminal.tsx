import Head from 'next/head';
import { useEffect, useState } from 'react';
import dynamic from 'next/dynamic';
import ReactSelect from 'react-select';
import AppShell from '@/modules/AppShell/AppShell';
import { api } from '@/shared/ApiService';
import { IServerConfig } from '@/shared/types/IProject';
import styles from './Styles/Terminal.module.css';

const SshTerminal = dynamic(() => import('@/modules/Ssh/SshTerminal'), { ssr: false });

const selectStyles = {
  control:     (b: object) => ({ ...b, background: '#1A2640', border: '1px solid rgba(255,255,255,0.08)', minHeight: 36, minWidth: 280 }),
  menu:        (b: object) => ({ ...b, background: '#1A2640', zIndex: 20 }),
  option:      (b: object, s: { isFocused: boolean }) => ({ ...b, background: s.isFocused ? '#1F2E4A' : 'transparent', color: '#F0F2F5' }),
  singleValue: (b: object) => ({ ...b, color: '#F0F2F5' }),
  placeholder: (b: object) => ({ ...b, color: '#637389' }),
  input:       (b: object) => ({ ...b, color: '#F0F2F5' }),
};

export default function TerminalPage() {
  const [servers, setServers] = useState<IServerConfig[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.get<IServerConfig[]>('/api/servers')
      .then(s => { setServers(s); if (!selected && s.length > 0) setSelected(s[0].id!); })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  const current = servers.find(s => s.id === selected);
  const options = servers.map(s => ({ value: s.id!, label: s.name || s.host }));

  return (
    <>
      <Head><title>ShipRight — Terminal</title></Head>
      <AppShell>
        <h1 className={styles.heading}>SSH Terminal</h1>
        <p className={styles.sub}>Execute commands on any registered server.</p>

        <div className={styles.serverSelect}>
          <span className={styles.selectLabel}>Server</span>
          <ReactSelect
            options={options}
            styles={selectStyles}
            placeholder={loading ? 'Loading…' : 'Select a server'}
            value={options.find(o => o.value === selected) ?? null}
            onChange={opt => setSelected(opt?.value ?? null)}
            isDisabled={loading}
          />
        </div>

        {current ? (
          <SshTerminal
            serverId={current.id}
            serverLabel={`${current.username}@${current.host}`}
          />
        ) : (
          <div className={styles.empty}>
            {loading ? 'Loading servers…' : 'No servers registered. Add one in Settings → Servers.'}
          </div>
        )}
      </AppShell>
    </>
  );
}
