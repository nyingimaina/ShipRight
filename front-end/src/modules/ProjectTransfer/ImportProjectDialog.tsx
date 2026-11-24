import { useRef, useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { errorMessage, runZestAction } from '@/shared/components/ZestAsync/zestAction';
import type { IBundleInspection, IBundleWarning, IImportOutcome } from '@/shared/types/IBundle';

interface Props {
  onClose: () => void;
  onImported: (outcome: IImportOutcome) => void;
}

const COUNT_LABELS: Record<string, string> = {
  services: 'services', servers: 'servers', registries: 'registries', awsProfiles: 'AWS profiles',
  credentials: 'credentials', keyFiles: 'saved key files', scripts: 'scripts', pipelines: 'pipelines', jit: 'build-on-server config',
};

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

function Warnings({ warnings }: { warnings: IBundleWarning[] }) {
  if (warnings.length === 0) return <p style={muted}>Nothing to double-check.</p>;
  return (
    <ul style={{ margin: '8px 0', paddingLeft: 18, display: 'flex', flexDirection: 'column', gap: 6 }}>
      {warnings.map((w, i) => (
        <li key={i} style={{ fontSize: 13, color: w.severity === 'info' ? '#A8B8CC' : '#C9A84C' }}>{w.message}</li>
      ))}
    </ul>
  );
}

/** Pick an export file, see what is inside and what needs attention, then import it. */
export default function ImportProjectDialog({ onClose, onImported }: Props) {
  const [file, setFile] = useState<File | null>(null);
  const [inspection, setInspection] = useState<IBundleInspection | null>(null);
  const [passphrase, setPassphrase] = useState('');
  const [targetRoot, setTargetRoot] = useState('');
  const [outcome, setOutcome] = useState<IImportOutcome | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const errorRef = useRef<HTMLParagraphElement>(null);

  const headers = () => {
    const h: Record<string, string> = {};
    if (passphrase) h['X-ShipRight-Passphrase'] = passphrase;
    if (targetRoot.trim()) h['X-ShipRight-Target-Root'] = targetRoot.trim();
    return h;
  };
  const inspectHeaders = () => {
    const h: Record<string, string> = {};
    if (passphrase) h['X-ShipRight-Passphrase'] = passphrase;
    return h;
  };
  const withHeaders = <T,>(path: string, h: Record<string, string>) =>
    Object.keys(h).length ? api.upload<T>(path, file!, h) : api.upload<T>(path, file!);

  const inspect = async (chosen: File) => {
    setInspection(null); setError(''); setBusy(true);
    try {
      return await runZestAction(async () => {
        const h = inspectHeaders();
        setInspection(Object.keys(h).length
          ? await api.upload<IBundleInspection>('/api/projects/import/inspect', chosen, h)
          : await api.upload<IBundleInspection>('/api/projects/import/inspect', chosen));
      }, { fallbackMessage: 'Could not read that file.', onError: setError, errorRef });
    } finally { setBusy(false); }
  };

  const choose = (chosen: File | undefined) => {
    if (!chosen) return;
    setFile(chosen);
    void inspect(chosen).catch(() => undefined);
  };

  const doImport = async () => {
    if (!file) return;
    setBusy(true); setError('');
    try {
      const result = await runZestAction(
        () => withHeaders<IImportOutcome>('/api/projects/import', headers()),
        { fallbackMessage: 'Import failed.', onError: setError, errorRef },
      );
      setOutcome(result);
      onImported(result);
    } finally { setBusy(false); }
  };

  const needsRoot = Boolean(inspection?.requiredInputs?.length || inspection?.relocatedPaths?.length);

  return (
    <div style={overlay} role="dialog" aria-label="Import project">
      <div style={box}>
        <h2 style={{ marginTop: 0 }}>Import project</h2>

        {outcome ? (
          <>
            <p>Imported as <strong>{outcome.projectName}</strong>.</p>
            <p style={muted}>Please check these before building or deploying:</p>
            <Warnings warnings={outcome.warnings} />
            <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 16 }}>
              <ZestButton onClick={onClose} zest={{ visualOptions: { variant: 'standard' } }}>Done</ZestButton>
            </div>
          </>
        ) : (
          <>
            <label style={{ display: 'block', marginBottom: 12 }}>
              <span style={{ ...muted, display: 'block', marginBottom: 4 }}>
                Choose a ShipRight export (.zip) or protected export (.shrsec). Nothing is changed until you confirm.
              </span>
              <input type="file" accept=".zip,.shrsec,application/zip,application/octet-stream" aria-label="Export file"
                onChange={e => choose(e.target.files?.[0])} disabled={busy} />
            </label>
            <label style={label}>
              Bundle passphrase (for protected exports)
              <input aria-label="Bundle passphrase" type="password" style={field} value={passphrase}
                onChange={e => setPassphrase(e.target.value)} disabled={busy} autoComplete="off" />
            </label>

            {busy && !inspection && <p style={muted}>Reading…</p>}
            {error && <p ref={errorRef} role="alert" style={{ color: '#E5604D' }}>{error}</p>}

            {inspection && (
              <>
                <h3 style={{ marginBottom: 4 }}>{inspection.projectName}</h3>
                {inspection.secretsIncluded && (
                  <p style={{ color: '#C9A84C' }}>
                    This bundle contains live secrets, saved keys and the managed SSH key. Keep the file and its passphrase safe.
                  </p>
                )}
                {Object.entries(inspection.counts).length > 0 && (
                  <p style={muted}>
                    Contains {Object.entries(inspection.counts).filter(([, n]) => n > 0)
                      .map(([k, n]) => `${n} ${COUNT_LABELS[k] ?? k}`).join(', ')}.
                  </p>
                )}
                {needsRoot && (
                  <label style={label}>
                    Destination project root
                    <input aria-label="Destination project root" style={field} value={targetRoot}
                      onChange={e => setTargetRoot(e.target.value)} disabled={busy}
                      placeholder="e.g. /opt/app or C:\apps\myapp" />
                    <span>The export uses portable path tokens. Enter the folder this project lives in on this machine.</span>
                  </label>
                )}
                {inspection.errors.map((m, i) => <p key={i} style={{ color: '#E5604D' }}>{m}</p>)}
                <p style={{ ...muted, marginBottom: 0 }}>Things to check after importing:</p>
                <Warnings warnings={inspection.warnings} />
              </>
            )}

            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 16 }}>
              <ZestButton onClick={onClose} zest={{ buttonStyle: 'outline' }}>Cancel</ZestButton>
              {inspection && (
                <ZestButton onClick={doImport} disabled={busy || !inspection.importable}
                  zest={{ semanticType: 'import', visualOptions: { variant: 'standard' } }}>Import project</ZestButton>
              )}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
