import { useEffect, useState } from 'react';
import Poller from 'jattac-web-poller';
import { api } from '@/shared/ApiService';
import Spinner from '@/shared/components/Busy/Spinner';
import { IServerConfig } from '@/shared/types/IProject';
import { ServerCard, ServerMetrics, SystemMetrics } from '@/pages/monitoring';

const unreachable = (serverId: string): ServerMetrics => ({
  serverId, reachable: false, error: 'Request failed',
  cpuPercent: 0, memUsedMb: 0, memTotalMb: 0, swapUsedMb: 0, swapTotalMb: 0,
  load1m: 0, load5m: 0, load15m: 0, uptimeSeconds: 0,
  disks: [], containers: [], certs: [], oomEvents: [], zombies: [], failedServices: [],
});

/** Live stats for one server. Mounted only while the Stats tab is active, so polling stops otherwise. */
export default function ServerStatsTab({ server }: { server: IServerConfig }) {
  const [metrics, setMetrics] = useState<ServerMetrics | undefined>(undefined);

  useEffect(() => {
    const id = server.id!;
    const poller = new Poller<ServerMetrics>({
      func: () => api.get<SystemMetrics>(`/api/servers/${id}/metrics`)
        .then(m => ({ serverId: id, ...m }))
        .catch(() => unreachable(id)),
      pollIntervalMilliseconds: 30_000,
      onResult: setMetrics,
      onError: err => console.error('Metrics poll failed', err),
      maxConsecutiveErrors: 5,
    });
    poller.start();
    return () => { void poller.stop(); };
  }, [server.id]);

  if (!metrics) {
    return <Spinner label="Connecting to the server over SSH…" hint="Slow connections can take up to half a minute for the first reading." />;
  }
  return <ServerCard server={server} metrics={metrics} />;
}
