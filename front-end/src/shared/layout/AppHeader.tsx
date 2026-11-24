import { useAuthStore } from '@/shared/auth';
import { useSystemStats } from './useSystemStats';
import { useHostSwap } from './useHostSwap';
import HostSwapMetric from './HostSwapMetric';
import styles from './AppHeader.module.css';

/** Global account and host strip, kept cheap enough to poll every few seconds. */
export default function AppHeader() {
  const user = useAuthStore((s) => s.user);
  const { stats } = useSystemStats();
  const { swap, refresh: refreshSwap } = useHostSwap();

  const nearCapacity = stats !== null && (
    !stats.available || stats.diskUsedPercent >= 80 || stats.ramUsedPercent >= 85 || stats.processCpuPercent >= 80
  );

  return (
    <header aria-label="Account and host status" className={styles.bar}>
      <div className={styles.identity}>
        <span className={styles.name}>{user?.name ?? 'Signed in'}</span>
        <span className={styles.sub}>{user?.companyName || '—'} · {user?.role ?? '—'}</span>
      </div>
      <span className={styles.metric}>Disk {stats ? `${Math.round(stats.diskUsedPercent)}%` : '—'}</span>
      <span className={styles.metric}>RAM {stats ? `${Math.round(stats.ramUsedPercent)}%` : '—'}</span>
      <span className={`${styles.metric} ${styles.processMetric}`}>Process {stats ? `${Math.round(stats.processCpuPercent)}%` : '—'}</span>
      <span className={`${styles.metric} ${styles.processCount}`}>{stats ? `${stats.processCount} processes` : '—'}</span>
      <HostSwapMetric swap={swap} canCreate={user?.role === 'Admin'} onChanged={refreshSwap} />
      {nearCapacity && <span role="status" aria-label="Near capacity" title="Near capacity" className={styles.warn}>Near capacity</span>}
    </header>
  );
}
