import { useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import type { IBundleInspection, IBundleWarning, IImportOutcome } from '@/shared/types/IBundle';

interface Props {
  onClose: () => void;
  onImported: (outcome: IImportOutcome) => void;
}

const COUNT_LABELS: Record<string, string> = {
  services: 'services', servers: 'servers', registries: 'registries', awsProfiles: 'AWS profiles',
  credentials: 'credentials', scripts: 'scripts', pipelines: 'pipelines',
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

const errorMessage = (e: unknown, fallback: string) => (e as { message?: string })?.message ?? fallback;

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
  const [outcome, setOutcome] = useState<IImportOutcome | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const choose = async (chosen: File | undefined) => {
    if (!chosen) return;
    setFile(chosen); setInspection(null); setError(''); setBusy(true);
    try {
      setInspection(await api.upload<IBundleInspection>('/api/projects/import/inspect', chosen));
    } catch (e) {
      setError(errorMessage(e, 'Could not read that file.'));
    } finally { setBusy(false); }
  };

  const doImport = async () => {
    if (!file) return;
    setBusy(true); setError('');
    try {
      const result = await api.upload<IImportOutcome>('/api/projects/import', file);
      setOutcome(result);
      onImported(result);
    } catch (e) {
      setError(errorMessage(e, 'Import failed.'));
    } finally { setBusy(false); }
  };

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
                Choose a ShipRight project export (.zip). Nothing is changed until you confirm.
              </span>
              <input type="file" accept=".zip,application/zip" aria-label="Export file"
                onChange={e => choose(e.target.files?.[0])} disabled={busy} />
            </label>

            {busy && !inspection && <p style={muted}>Reading…</p>}
            {error && <p style={{ color: '#E5604D' }}>{error}</p>}

            {inspection && (
              <>
                <h3 style={{ marginBottom: 4 }}>{inspection.projectName}</h3>
                {Object.entries(inspection.counts).length > 0 && (
                  <p style={muted}>
                    Contains {Object.entries(inspection.counts).filter(([, n]) => n > 0)
                      .map(([k, n]) => `${n} ${COUNT_LABELS[k] ?? k}`).join(', ')}.
                  </p>
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
                  zest={{ visualOptions: { variant: 'standard' } }}>Import project</ZestButton>
              )}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
