import { useAuthStore } from '@/shared/auth';
import { useSystemStats } from './useSystemStats';
import styles from './AppHeader.module.css';

/** Global account and host strip, kept cheap enough to poll every few seconds. */
export default function AppHeader() {
  const user = useAuthStore((s) => s.user);
  const { stats } = useSystemStats();

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
      <span className={styles.metric}>Process {stats ? `${Math.round(stats.processCpuPercent)}%` : '—'}</span>
      <span className={styles.metric}>{stats ? `${stats.processCount} processes` : '—'}</span>
      {nearCapacity && <span role="status" className={styles.warn}>Near capacity</span>}
    </header>
  );
}
