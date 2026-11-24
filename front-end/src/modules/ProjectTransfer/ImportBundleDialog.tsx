import { useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import type { IBundleInspection, IBundleWarning, IImportOutcome } from '@/shared/types/IBundle';
import styles from './ImportBundleDialog.module.css';

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
const fileRow: React.CSSProperties = { display: 'flex', alignItems: 'center', gap: 12, marginBottom: 12 };

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

/** Import a bundle. Passphrase and destination root can be entered before or after choosing the file. */
export default function ImportBundleDialog({ onClose, onImported }: Props) {
  const [file, setFile] = useState<File | null>(null);
  const [inspection, setInspection] = useState<IBundleInspection | null>(null);
  const [passphrase, setPassphrase] = useState('');
  const [targetRoot, setTargetRoot] = useState('');
  const [outcome, setOutcome] = useState<IImportOutcome | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const passphraseHeaders = (): Record<string, string> =>
    passphrase.length > 0 ? { 'X-ShipRight-Passphrase': passphrase } : {};

  const importHeaders = (): Record<string, string> => {
    const h = passphraseHeaders();
    if (targetRoot.trim()) h['X-ShipRight-Target-Root'] = targetRoot.trim();
    return h;
  };

  const withHeaders = <T,>(path: string, headers: Record<string, string>) =>
    Object.keys(headers).length ? api.upload<T>(path, file!, headers) : api.upload<T>(path, file!);

  const read = async (chosen: File) => {
    setInspection(null);
    setError('');
    setBusy(true);
    try {
      const headers = passphraseHeaders();
      setInspection(Object.keys(headers).length
        ? await api.upload<IBundleInspection>('/api/projects/import/inspect', chosen, headers)
        : await api.upload<IBundleInspection>('/api/projects/import/inspect', chosen));
    } catch (e) {
      setError(errorMessage(e, 'Could not read that file.'));
    } finally {
      setBusy(false);
    }
  };

  const choose = (chosen: File | undefined) => {
    if (!chosen) return;
    setFile(chosen);
    void read(chosen);
  };

  const changePassphrase = (value: string) => {
    setPassphrase(value);
    // Inspection headers change with the passphrase, so a previous result is no longer valid.
    setInspection(null);
    setError('');
  };

  const doImport = async () => {
    if (!file) return;
    setBusy(true);
    setError('');
    try {
      const result = await withHeaders<IImportOutcome>('/api/projects/import', importHeaders());
      setOutcome(result);
      onImported(result);
    } catch (e) {
      setError(errorMessage(e, 'Import failed.'));
    } finally {
      setBusy(false);
    }
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
            <p style={{ ...muted, marginTop: 0, marginBottom: 12 }}>
              Choose a ShipRight export (.zip) or protected export (.shrsec). Nothing is changed until you confirm.
            </p>
            <div style={fileRow}>
              <label htmlFor="import-bundle-file" className={styles.fileButton}>Choose file</label>
              <input id="import-bundle-file" type="file" aria-label="Export file"
                accept=".zip,.shrsec,application/zip,application/octet-stream"
                className={styles.hiddenFile} onChange={e => choose(e.target.files?.[0])} disabled={busy} />
              <span className={styles.fileName} style={muted}>{file?.name ?? 'No file chosen'}</span>
            </div>

            <label style={label}>
              Bundle passphrase (for protected exports)
              <input aria-label="Bundle passphrase" type="password" style={field} value={passphrase}
                onChange={e => changePassphrase(e.target.value)} disabled={busy} autoComplete="off" />
              <span style={{ ...muted, display: 'block', marginTop: 4 }}>
                For a .shrsec file, enter its passphrase. If the file was already chosen, select Read bundle again.
              </span>
            </label>

            <div style={{ display: 'flex', marginTop: 12 }}>
              <ZestButton onClick={() => file && void read(file)} disabled={!file || busy} zest={{ buttonStyle: 'outline' }}>
                Read bundle
              </ZestButton>
            </div>

            {busy && !inspection && <p style={muted}>Reading…</p>}
            {error && <p style={{ color: '#E5604D' }}>{error}</p>}

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
                  zest={{ visualOptions: { variant: 'standard' } }}>Import project</ZestButton>
              )}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
