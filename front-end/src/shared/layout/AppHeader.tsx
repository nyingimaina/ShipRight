import { useAuthStore } from '@/shared/auth';
import { useSystemStats } from './useSystemStats';

const bar: React.CSSProperties = {
  display: 'flex', alignItems: 'center', justifyContent: 'flex-end', flexWrap: 'wrap', gap: 16,
  padding: '10px 16px', marginBottom: 20, borderRadius: 8,
  border: '1px solid var(--border-subtle)', background: 'var(--bg-elevated, transparent)',
  fontSize: 12, color: 'var(--text-primary)',
};
const identity: React.CSSProperties = { display: 'flex', flexDirection: 'column', alignItems: 'flex-end', lineHeight: 1.35 };
const strong: React.CSSProperties = { fontWeight: 600, fontSize: 13 };
const metric: React.CSSProperties = { color: 'var(--text-muted)', whiteSpace: 'nowrap' };
const warn: React.CSSProperties = {
  color: '#C9A84C', border: '1px solid #C9A84C', borderRadius: 6, padding: '3px 8px', whiteSpace: 'nowrap',
};

/** Global account and host strip, kept cheap enough to poll every few seconds. */
export default function AppHeader() {
  const user = useAuthStore((s) => s.user);
  const { stats } = useSystemStats();

  const nearCapacity = stats !== null && (
    !stats.available || stats.diskUsedPercent >= 80 || stats.ramUsedPercent >= 85 || stats.processCpuPercent >= 80
  );

  return (
    <header aria-label="Account and host status" style={bar}>
      <div style={identity}>
        <span style={strong}>{user?.name ?? 'Signed in'}</span>
        <span style={metric}>{user?.companyName || '—'} · {user?.role ?? '—'}</span>
      </div>
      <span style={metric}>Disk {stats ? `${Math.round(stats.diskUsedPercent)}%` : '—'}</span>
      <span style={metric}>RAM {stats ? `${Math.round(stats.ramUsedPercent)}%` : '—'}</span>
      <span style={metric}>Process {stats ? `${Math.round(stats.processCpuPercent)}%` : '—'}</span>
      <span style={metric}>{stats ? `${stats.processCount} processes` : '—'}</span>
      {nearCapacity && <span role="status" style={warn}>Near capacity</span>}
    </header>
  );
}
