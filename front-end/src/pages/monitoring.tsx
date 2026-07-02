import Head from 'next/head';
import { useEffect, useState } from 'react';
import Poller from 'jattac-web-poller';
import AppShell from '@/modules/AppShell/AppShell';
import { api } from '@/shared/ApiService';
import { IServerConfig } from '@/shared/types/IProject';
import styles from './Styles/Monitoring.module.css';

interface DiskMetric      { mount: string; usedGb: number; totalGb: number; }
interface ContainerMetric { name: string; image: string; status: string; cpuPercent: number; memUsedMb: number; memLimitMb: number; }
interface SslCertMetric   { domain: string; issuedUtc: string; expiresUtc: string; }
interface OomEventMetric  { processName: string; pid: number; memoryMb: number; occurredAt: string | null; }
interface ZombieProcess   { pid: number; processName: string; parentName: string; }

interface SystemMetrics {
  reachable: boolean;
  cpuPercent: number;
  memUsedMb: number;   memTotalMb: number;
  swapUsedMb: number;  swapTotalMb: number;
  load1m: number;      load5m: number; load15m: number;
  uptimeSeconds: number;
  disks: DiskMetric[];
  containers: ContainerMetric[];
  certs: SslCertMetric[];
  oomEvents: OomEventMetric[];
  zombies: ZombieProcess[];
  failedServices: string[];
  error?: string;
}
type ServerMetrics = SystemMetrics & { serverId: string };

function fmtUptime(s: number): string {
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  if (d > 0 && h > 0) return `${d}d ${h}h`;
  if (d > 0) return `${d} days`;
  if (h > 0) return `${h} hours`;
  return `${Math.floor(s / 60)} minutes`;
}

function fmtMb(mb: number): string {
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb} MB`;
}

function daysUntil(isoDate: string): number {
  return Math.floor((new Date(isoDate).getTime() - Date.now()) / 86400000);
}

function daysSince(isoDate: string): number {
  return Math.floor((Date.now() - new Date(isoDate).getTime()) / 86400000);
}

function barColor(pct: number, thresholds: [number, number]): string {
  if (pct <= thresholds[0]) return styles.green;
  if (pct <= thresholds[1]) return styles.amber;
  return styles.red;
}

function certBadgeClass(daysLeft: number): string {
  if (daysLeft > 30) return styles.badgeHealthy;
  if (daysLeft > 7)  return styles.badgeRestarting;
  return styles.badgeUnhealthy;
}

function MetricBar({
  label, explainer, usedPct, valueLabel, thresholds,
}: {
  label: string; explainer: string; usedPct: number;
  valueLabel: string; thresholds: [number, number];
}) {
  const pct = Math.min(100, Math.max(0, usedPct));
  return (
    <div className={styles.metric}>
      <div className={styles.metricLabel}>{label}</div>
      <div className={styles.metricExplainer}>{explainer}</div>
      <div className={styles.bar}>
        <div className={`${styles.barFill} ${barColor(pct, thresholds)}`} style={{ width: `${pct}%` }} />
      </div>
      <div className={styles.metricValue}>{valueLabel}</div>
    </div>
  );
}

function containerBadgeClass(status: string): string {
  switch (status) {
    case 'Healthy':    return styles.badgeHealthy;
    case 'Running':    return styles.badgeRunning;
    case 'Stopped':    return styles.badgeStopped;
    case 'Unhealthy':  return styles.badgeUnhealthy;
    case 'Restarting': return styles.badgeRestarting;
    default:           return styles.badgeDefault;
  }
}

function ServerCard({ server, metrics }: { server: IServerConfig; metrics?: ServerMetrics }) {
  const loading = !metrics;
  const offline  = metrics && !metrics.reachable;
  const online   = metrics?.reachable;

  const dotClass = loading ? styles.statusNoKey : online ? styles.statusOnline : styles.statusOffline;

  const largestDisk = online
    ? [...(metrics?.disks ?? [])].sort((a, b) => (b.usedGb / b.totalGb) - (a.usedGb / a.totalGb))[0]
    : null;

  const hasAnomalies = online && (
    (metrics!.failedServices.length > 0) ||
    (metrics!.oomEvents.length > 0) ||
    (metrics!.zombies.length > 0)
  );

  return (
    <div className={styles.serverCard}>
      <div className={styles.cardHeader}>
        <span className={`${styles.statusDot} ${dotClass}`} />
        <span className={styles.serverName}>{server.name || server.host}</span>
        {online && (
          <span className={styles.serverMeta}>Online for {fmtUptime(metrics!.uptimeSeconds)}</span>
        )}
      </div>

      {loading && <p className={styles.offlineMsg}>Fetching metrics…</p>}

      {offline && (
        <p className={styles.offlineMsg}>
          Could not connect — {metrics?.error ?? 'check the server is online and an SSH key is configured.'}
        </p>
      )}

      {online && (
        <>
          {/* Resource bars */}
          <div className={styles.metrics}>
            <MetricBar
              label="Processor load"
              explainer="How hard the server is working. Above 85% for long periods can slow things down."
              usedPct={metrics!.cpuPercent}
              valueLabel={`${metrics!.cpuPercent.toFixed(1)}%`}
              thresholds={[60, 85]}
            />
            <MetricBar
              label="Memory in use"
              explainer="How much working memory is being used. High usage can make apps slow or crash."
              usedPct={metrics!.memTotalMb > 0 ? (metrics!.memUsedMb / metrics!.memTotalMb) * 100 : 0}
              valueLabel={`${fmtMb(metrics!.memUsedMb)} of ${fmtMb(metrics!.memTotalMb)}`}
              thresholds={[70, 85]}
            />
            <MetricBar
              label="Overflow memory"
              explainer="Extra memory borrowed from disk when RAM runs out. Any significant usage means the server needs more RAM."
              usedPct={metrics!.swapTotalMb > 0 ? (metrics!.swapUsedMb / metrics!.swapTotalMb) * 100 : 0}
              valueLabel={
                metrics!.swapTotalMb > 0
                  ? `${fmtMb(metrics!.swapUsedMb)} of ${fmtMb(metrics!.swapTotalMb)}`
                  : 'Not configured'
              }
              thresholds={[1, 20]}
            />
            {largestDisk && (
              <MetricBar
                label={`Storage space (${largestDisk.mount})`}
                explainer="How much disk is used. Running out stops services from writing data."
                usedPct={(largestDisk.usedGb / largestDisk.totalGb) * 100}
                valueLabel={`${largestDisk.usedGb} GB of ${largestDisk.totalGb} GB`}
                thresholds={[70, 85]}
              />
            )}
          </div>

          {/* Docker containers */}
          {metrics!.containers.length > 0 && (
            <>
              <div className={styles.sectionLabel}>Services</div>
              <table className={styles.table}>
                <thead>
                  <tr><th>Service</th><th>Status</th><th>CPU</th><th>Memory</th></tr>
                </thead>
                <tbody>
                  {metrics!.containers.map(c => (
                    <tr key={c.name}>
                      <td>{c.name}</td>
                      <td><span className={`${styles.badge} ${containerBadgeClass(c.status)}`}>{c.status}</span></td>
                      <td>{c.cpuPercent > 0 ? `${c.cpuPercent.toFixed(1)}%` : '—'}</td>
                      <td>{c.memUsedMb > 0 ? fmtMb(c.memUsedMb) : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
          {metrics!.containers.length === 0 && (
            <p className={styles.noContainers}>No Docker services found on this server.</p>
          )}

          {/* SSL certificates */}
          {metrics!.certs.length > 0 && (
            <>
              <div className={styles.sectionLabel}>SSL Certificates</div>
              <table className={styles.table}>
                <thead>
                  <tr><th>Domain</th><th>Expires</th><th>Renewed</th></tr>
                </thead>
                <tbody>
                  {metrics!.certs.map(c => {
                    const days = daysUntil(c.expiresUtc);
                    const renewed = daysSince(c.issuedUtc);
                    return (
                      <tr key={c.domain}>
                        <td>{c.domain}</td>
                        <td>
                          <span className={`${styles.badge} ${certBadgeClass(days)}`}>
                            {days > 0 ? `${days}d left` : `Expired ${Math.abs(days)}d ago`}
                          </span>
                        </td>
                        <td className={styles.dimText}>{renewed}d ago</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </>
          )}

          {/* Anomalies */}
          <div className={styles.sectionLabel}>Anomalies</div>
          {!hasAnomalies && (
            <p className={styles.allClear}>No anomalies detected.</p>
          )}

          {metrics!.failedServices.length > 0 && (
            <div className={styles.anomalyBlock}>
              <div className={styles.anomalyTitle}>Failed services</div>
              <div className={styles.tagList}>
                {metrics!.failedServices.map(s => (
                  <span key={s} className={`${styles.badge} ${styles.badgeUnhealthy}`}>{s}</span>
                ))}
              </div>
            </div>
          )}

          {metrics!.oomEvents.length > 0 && (
            <div className={styles.anomalyBlock}>
              <div className={styles.anomalyTitle}>
                Out of memory kills
                <span className={styles.anomalyHint}> — server ran out of RAM and forcibly stopped a process</span>
              </div>
              <table className={styles.table}>
                <thead>
                  <tr><th>Process</th><th>PID</th><th>Memory freed</th><th>When</th></tr>
                </thead>
                <tbody>
                  {metrics!.oomEvents.map((e, i) => (
                    <tr key={i}>
                      <td>{e.processName}</td>
                      <td className={styles.dimText}>{e.pid}</td>
                      <td>{e.memoryMb > 0 ? fmtMb(e.memoryMb) : '—'}</td>
                      <td className={styles.dimText}>{e.occurredAt ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {metrics!.zombies.length > 0 && (
            <div className={styles.anomalyBlock}>
              <div className={styles.anomalyTitle}>
                Stuck processes
                <span className={styles.anomalyHint}> — processes that have finished but weren't cleaned up by their parent</span>
              </div>
              <table className={styles.table}>
                <thead>
                  <tr><th>Process</th><th>PID</th><th>Parent</th></tr>
                </thead>
                <tbody>
                  {metrics!.zombies.map(z => (
                    <tr key={z.pid}>
                      <td>{z.processName}</td>
                      <td className={styles.dimText}>{z.pid}</td>
                      <td className={styles.dimText}>{z.parentName}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </div>
  );
}

export default function MonitoringPage() {
  const [servers, setServers]         = useState<IServerConfig[]>([]);
  const [metrics, setMetrics]         = useState<Record<string, ServerMetrics>>({});
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [loading, setLoading]         = useState(true);

  useEffect(() => {
    let poller: Poller<ServerMetrics[]> | null = null;

    api.get<IServerConfig[]>('/api/servers')
      .then(srvs => {
        setServers(srvs);
        setLoading(false);
        if (srvs.length === 0) return;

        poller = new Poller<ServerMetrics[]>({
          func: () =>
            Promise.all(
              srvs.map(s =>
                api.get<SystemMetrics>(`/api/servers/${s.id}/metrics`)
                  .then(m => ({ serverId: s.id!, ...m }))
                  .catch(() => ({
                    serverId: s.id!,
                    reachable: false,
                    error: 'Request failed',
                    cpuPercent: 0, memUsedMb: 0, memTotalMb: 0,
                    swapUsedMb: 0, swapTotalMb: 0,
                    load1m: 0, load5m: 0, load15m: 0,
                    uptimeSeconds: 0,
                    disks: [], containers: [], certs: [],
                    oomEvents: [], zombies: [], failedServices: [],
                  } as ServerMetrics))
              )
            ),
          pollIntervalMilliseconds: 30_000,
          onResult: results => {
            setMetrics(Object.fromEntries(results.map(r => [r.serverId, r])));
            setLastUpdated(new Date());
          },
          onError: err => console.error('Metrics poll failed', err),
          maxConsecutiveErrors: 5,
        });
        poller.start();
      })
      .catch(() => setLoading(false));

    return () => { void poller?.stop(); };
  }, []);

  const updatedAgo = lastUpdated
    ? `${Math.round((Date.now() - lastUpdated.getTime()) / 1000)}s ago`
    : null;

  return (
    <>
      <Head><title>Monitoring — ShipRight</title></Head>
      <AppShell>
        <div className={styles.page}>
          <div className={styles.header}>
            <h1 className={styles.title}>Monitoring</h1>
            <p className={styles.sub}>Live health and resource snapshot for your servers.</p>
            {updatedAgo && (
              <p className={styles.lastUpdated}>Last updated {updatedAgo} · refreshes every 30s</p>
            )}
          </div>

          {loading && <p className={styles.empty}>Loading servers…</p>}

          {!loading && servers.length === 0 && (
            <p className={styles.empty}>
              No servers configured. Add one on the Servers page to start monitoring.
            </p>
          )}

          {servers.map(s => (
            <ServerCard key={s.id} server={s} metrics={metrics[s.id!]} />
          ))}
        </div>
      </AppShell>
    </>
  );
}
