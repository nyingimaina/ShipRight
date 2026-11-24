import { useServerVersion } from '@/shared/serverInfo';

const footer: React.CSSProperties = {
  display: 'flex', justifyContent: 'center', gap: 8, marginTop: 32, paddingTop: 12,
  borderTop: '1px solid var(--border-subtle)', fontSize: 11, color: 'var(--text-muted)',
};

/** One footer for every signed-in page, so build/version details live in a single place. */
export default function AppFooter() {
  const version = useServerVersion();
  return (
    <footer style={footer}>
      <span>ShipRight</span><span>·</span><span>Build. Ship. Done.</span>
      {version && <><span>·</span><span>v{version}</span></>}
    </footer>
  );
}
