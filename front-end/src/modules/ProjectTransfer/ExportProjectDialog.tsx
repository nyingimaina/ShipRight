import { useRef, useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import { runZestAction } from '@/shared/components/ZestAsync/zestAction';
import { exportProject } from './exportProject';

interface Props {
  projectId: string;
  projectName: string;
  onClose: () => void;
  onExported: () => void;
}

const overlay: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.6)', display: 'flex',
  alignItems: 'center', justifyContent: 'center', zIndex: 1000,
};
const box: React.CSSProperties = {
  background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 10,
  padding: 24, width: 560, maxWidth: '92vw', maxHeight: '86vh', overflowY: 'auto',
};
const muted: React.CSSProperties = { fontSize: 12, color: '#A8B8CC' };
const field: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', marginTop: 4, padding: 8, borderRadius: 6,
  border: '1px solid rgba(255,255,255,0.2)', background: '#0D1627', color: '#F0F2F5',
};
const label: React.CSSProperties = { ...muted, display: 'block', marginTop: 12 };

/** Opt-in path for a passphrase-protected bundle containing live secrets and keys. */
export default function ExportProjectDialog({ projectId, projectName, onClose, onExported }: Props) {
  const [passphrase, setPassphrase] = useState('');
  const [confirm, setConfirm] = useState('');
  const [acknowledged, setAcknowledged] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const errorRef = useRef<HTMLParagraphElement>(null);

  const mismatch = confirm.length > 0 && passphrase !== confirm;
  const canExport = acknowledged && passphrase.length > 0 && !mismatch && !busy;

  const submit = async () => {
    if (!canExport) return;
    setBusy(true); setError('');
    try {
      await runZestAction(
        () => exportProject(projectId, { includeSecrets: true, passphrase }),
        { fallbackMessage: 'Export failed.', onError: setError, errorRef },
      );
      onExported();
      onClose();
    } finally { setBusy(false); }
  };

  return (
    <div style={overlay} role="dialog" aria-label="Export with secrets">
      <div style={box}>
        <h2 style={{ marginTop: 0 }}>Export with secrets</h2>
        <p style={{ color: '#E5604D', marginTop: 0 }}>
          This encrypted bundle contains live secrets, saved key files, build-on-server settings and
          the ShipRight-managed SSH key. Anyone with the file and its passphrase can use them.
        </p>
        <p style={muted}>Exporting <strong>{projectName}</strong>. Share the file and passphrase by different routes.</p>

        <label style={label}>
          Passphrase
          <input type="password" style={field} value={passphrase}
            onChange={e => setPassphrase(e.target.value)} disabled={busy} autoComplete="new-password" />
        </label>
        <label style={label}>
          Confirm passphrase
          <input type="password" style={field} value={confirm}
            onChange={e => setConfirm(e.target.value)} disabled={busy} autoComplete="new-password" />
        </label>
        {mismatch && <p style={{ color: '#E5604D', marginBottom: 0 }}>The passphrases do not match.</p>}
        <label style={{ ...label, display: 'flex', alignItems: 'flex-start', gap: 8 }}>
          <input type="checkbox" checked={acknowledged} onChange={e => setAcknowledged(e.target.checked)} disabled={busy} />
          <span>I understand that this export contains live secrets and keys, and that I am responsible for keeping it safe.</span>
        </label>
        {error && <p ref={errorRef} role="alert" style={{ color: '#E5604D' }}>{error}</p>}

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 16 }}>
          <ZestButton onClick={onClose} zest={{ buttonStyle: 'outline' }}>Cancel</ZestButton>
          <ZestButton onClick={submit} disabled={!canExport}
            zest={{ semanticType: 'export', visualOptions: { variant: 'standard' } }}>
            Export with secrets
          </ZestButton>
        </div>
      </div>
    </div>
  );
}
