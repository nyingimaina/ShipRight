import { useState } from 'react';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { api } from '@/shared/ApiService';

export interface ComposeCandidate {
  name: string;
  imageRef: string;
  tagVariable: string;
  built: boolean;
  suggested: boolean;
  note: string | null;
}

export interface ComposeReading {
  relativePath: string;
  repoRoot: string;
  services: ComposeCandidate[];
  leftAlone: string[];
  notes: string[];
}

export interface ServiceHelp {
  explanation: string;
  steps: string[];
  found: ComposeReading | null;
}

export interface ConfiguredService {
  name: string;
  imageRef: string;
  tagVariable: string;
}

interface Props {
  base: string;
  /** True when build-on-server has no services of the project's own, so the services must come from the compose file. */
  needsServices: boolean;
  /** The services already taken from a compose file and kept in the settings. */
  current: ConfiguredService[];
  /** Why there are no services and what to do, when that is the problem. */
  help: ServiceHelp | null;
  /** `services` is null when only the file path should be set. */
  onApply: (composeFile: string, services: ConfiguredService[] | null) => void;
  onClear: () => void;
}

const box: React.CSSProperties = { marginTop: 8, padding: '10px 12px', borderRadius: 8, border: '1px solid var(--border-subtle)', fontSize: 13 };
const muted: React.CSSProperties = { margin: '6px 0 0', fontSize: 12, color: 'var(--text-muted)' };
const btn: React.CSSProperties = { padding: '6px 14px', borderRadius: 6, border: '1px solid var(--border-subtle)', background: 'transparent', color: 'inherit', cursor: 'pointer', fontSize: 13 };
const primary: React.CSSProperties = { ...btn, background: 'var(--accent, #C9A84C)', color: '#10192E', border: 'none', fontWeight: 600 };

const messageOf = (e: unknown, fallback: string) =>
  e && typeof e === 'object' && 'message' in e && typeof (e as { message: unknown }).message === 'string' ? (e as { message: string }).message : fallback;

const toConfigured = (c: ComposeCandidate): ConfiguredService => ({ name: c.name, imageRef: c.imageRef, tagVariable: c.tagVariable });

/**
 * Pick the compose file on this computer. ShipRight works out where it sits in the repository and which of its services to build,
 * and when the project has no services of its own it explains why and how to fix that, using the compose file it can find.
 */
export default function JitComposeServices({ base, needsServices, current, help, onApply, onClear }: Props) {
  const [picking, setPicking] = useState(false);
  const [reading, setReading] = useState<ComposeReading | null>(null);
  const [ticked, setTicked] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const read = async (path: string) => {
    setPicking(false); setBusy(true); setError(null); setReading(null);
    try {
      const r = await api.post<ComposeReading>(`${base}/compose-file`, { path });
      setReading(r);
      setTicked(new Set(r.services.filter(s => s.suggested).map(s => s.name)));
    } catch (e) {
      setError(messageOf(e, 'Could not read that file.'));
    } finally { setBusy(false); }
  };

  const toggle = (name: string) => setTicked(prev => { const next = new Set(prev); if (next.has(name)) next.delete(name); else next.add(name); return next; });

  return (
    <div>
      {help && (
        <div style={{ ...box, borderColor: '#C9A84C' }} role="note" aria-label="Why there are no services">
          <strong>Why this cannot deploy yet</strong>
          <p style={muted}>{help.explanation}</p>
          <ol style={{ margin: '6px 0 0', paddingLeft: 18, fontSize: 12 }}>
            {help.steps.map(s => <li key={s} style={{ marginTop: 3 }}>{s}</li>)}
          </ol>
          {help.found && help.found.services.length > 0 && (
            <div style={{ marginTop: 8 }}>
              <span style={muted}>Found in {help.found.relativePath}: {help.found.services.map(s => s.name).join(', ')}</span>{' '}
              <button type="button" style={primary} onClick={() => onApply(help.found!.relativePath, help.found!.services.map(toConfigured))}>Use these services</button>
            </div>
          )}
        </div>
      )}

      {current.length > 0 && (
        <p style={muted}>
          Services to build, from your compose file: {current.map(s => s.name).join(', ')}{' '}
          <button type="button" style={btn} onClick={onClear}>Clear these services</button>
        </p>
      )}

      {picking ? (
        <div style={{ marginTop: 8 }}>
          <FilePicker storageKey="compose-file" label="Your compose file" onSelect={read} />
          <button type="button" style={{ ...btn, marginTop: 6 }} onClick={() => setPicking(false)}>Cancel</button>
        </div>
      ) : (
        <div style={{ marginTop: 8 }}>
          <button type="button" style={btn} disabled={busy} onClick={() => setPicking(true)}>{busy ? 'Reading…' : 'Browse for the compose file'}</button>
        </div>
      )}

      {error && <p role="alert" style={{ ...muted, color: '#E08A8A' }}>{error}</p>}

      {reading && (
        <div style={box}>
          <strong>{reading.relativePath}</strong>
          <span style={muted}> (its place in your repository)</span>
          {reading.services.length > 0 && needsServices && (
            <>
              <p style={muted}>ShipRight found these services to build:</p>
              <ul style={{ listStyle: 'none', padding: 0, margin: '6px 0 0' }}>
                {reading.services.map(s => (
                  <li key={s.name} style={{ marginTop: 6 }}>
                    <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                      <input type="checkbox" aria-label={`Build ${s.name}`} checked={ticked.has(s.name)} onChange={() => toggle(s.name)} />
                      <strong>{s.name}</strong> <span style={muted}>{s.imageRef}</span>
                    </label>
                    {s.note && <div style={muted}>{s.note}</div>}
                  </li>
                ))}
              </ul>
            </>
          )}
          {reading.services.length > 0 && !needsServices && (
            <p style={muted}>This file builds: {reading.services.map(s => s.name).join(', ')}.</p>
          )}
          {reading.leftAlone.length > 0 && <p style={muted}>Left alone (not built by ShipRight): {reading.leftAlone.join(', ')}</p>}
          {reading.notes.map(n => <p key={n} style={muted}>{n}</p>)}
          {reading.services.length > 0 && (
            <div style={{ marginTop: 8 }}>
              {needsServices ? (
                <button type="button" style={primary} disabled={ticked.size === 0}
                  onClick={() => onApply(reading.relativePath, reading.services.filter(s => ticked.has(s.name)).map(toConfigured))}>
                  Use this compose file and these services
                </button>
              ) : (
                <button type="button" style={primary} onClick={() => onApply(reading.relativePath, null)}>Use this compose file</button>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
