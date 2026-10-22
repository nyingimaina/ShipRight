import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestTabs, { ZestTabItem } from 'jattac.libs.web.zest-tabs';
import AppShell from '@/modules/AppShell/AppShell';
import ServerActionsTab from '@/modules/ServerDetail/ServerActionsTab';
import Spinner from '@/shared/components/Busy/Spinner';
import ServerFilesTab from '@/modules/ServerDetail/ServerFilesTab';
import RemoteTerminal from '@/shared/components/RemoteTerminal/RemoteTerminal';
import ServerStatsTab from '@/modules/ServerDetail/ServerStatsTab';
import { ServerForm, ServerInput } from '@/pages/servers';
import { api } from '@/shared/ApiService';
import { IServerConfig } from '@/shared/types/IProject';
import styles from '@/modules/ServerDetail/Styles/ServerDetail.module.css';

type Tab = 'actions' | 'stats' | 'files' | 'terminal' | 'settings';
const TABS: ZestTabItem<Tab>[] = [
  { label: 'Actions', value: 'actions' },
  { label: 'Stats', value: 'stats' },
  { label: 'Files', value: 'files' },
  { label: 'Terminal', value: 'terminal' },
  { label: 'Settings', value: 'settings' },
];
const isTab = (v: unknown): v is Tab =>
  v === 'actions' || v === 'stats' || v === 'files' || v === 'terminal' || v === 'settings';

export default function ServerDetailPage() {
  const router = useRouter();
  const id = typeof router.query.id === 'string' ? router.query.id : undefined;
  const [server, setServer] = useState<IServerConfig | null>(null);
  const [failed, setFailed] = useState(false);
  const [tab, setTab] = useState<Tab>(isTab(router.query.tab) ? router.query.tab : 'actions');

  useEffect(() => { if (isTab(router.query.tab)) setTab(router.query.tab); }, [router.query.tab]);

  const load = async (serverId: string) => {
    try { setServer(await api.get<IServerConfig>(`/api/servers/${serverId}`)); setFailed(false); }
    catch { setFailed(true); }
  };
  useEffect(() => { if (id) load(id); }, [id]);

  const save = async (input: ServerInput) => {
    await api.put(`/api/servers/${input.id}`, input);
    toast.success('Server updated.');
    await load(input.id!);
  };

  const title = server ? (server.name || server.host) : 'Server';

  return (
    <>
      <Head><title>{`ShipRight — ${title}`}</title></Head>
      <AppShell>
        <div className={styles.page}>
          <p className={styles.breadcrumb}><Link href="/servers/">Servers</Link>{' / '}{title}</p>

          {failed && <p className={styles.message}>This server could not be loaded. It may have been deleted.</p>}
          {!failed && !server && <Spinner label="Loading server…" />}

          {server && (
            <>
              <div className={styles.header}>
                <h1 className={styles.heading}>{title}</h1>
                <span className={styles.sub}>{server.username}@{server.host}</span>
              </div>
              <ZestTabs id="server-detail" items={TABS} activeValue={tab} onChange={setTab} />
              <div className={styles.panel}>
                {tab === 'actions' && <ServerActionsTab serverId={server.id!} serverName={title} />}
                {tab === 'stats' && <ServerStatsTab server={server} />}
                {tab === 'files' && <ServerFilesTab server={server} />}
                {tab === 'terminal' && (
                  <RemoteTerminal serverId={server.id!} serverLabel={`${server.username}@${server.host}`}
                    startDir={server.remoteWorkingDir} />
                )}
                {tab === 'settings' && (
                  <div className={styles.settingsWrap}>
                    <ServerForm initial={server} onSave={save} onCancel={() => load(server.id!)} hideDeployActions />
                  </div>
                )}
              </div>
            </>
          )}
        </div>
      </AppShell>
    </>
  );
}
