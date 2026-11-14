import { useEffect, useState } from 'react';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';

interface Props {
  base: string;
  repoUrl: string;
  /** The saved credential chosen for this project, if any. */
  value: string | null;
  onChange: (id: string | null) => void;
}

interface CredentialOption { id: string; name: string; hostPattern: string | null }

const box: React.CSSProperties = { marginTop: 10, padding: '10px 12px', borderRadius: 8, border: '1px solid var(--border-subtle)', fontSize: 13 };
const muted: React.CSSProperties = { margin: '6px 0 0', fontSize: 12, color: 'var(--text-muted)' };
const input: React.CSSProperties = {
  padding: '7px 9px', borderRadius: 6, border: '1px solid var(--border-subtle)', background: 'var(--bg-input, transparent)',
  color: 'inherit', fontSize: 13, maxWidth: 420, width: '100%',
};

export const isHttps = (url: string) => /^https:\/\//i.test(url.trim());

/**
 * Chooses one of the access tokens already saved under Resources to read a private https repository. Only names are listed. The
 * token is never part of the address and is never saved on the server: it is used for one command at a time.
 */
export default function JitCredentialPicker({ base, repoUrl, value, onChange }: Props) {
  const [options, setOptions] = useState<CredentialOption[] | null>(null);
  const https = isHttps(repoUrl);

  useEffect(() => {
    if (!https) return;
    let cancelled = false;
    api.get<CredentialOption[]>(`${base}/credentials`)
      .then(r => { if (!cancelled) setOptions(r); })
      .catch(() => { if (!cancelled) setOptions(null); });
    return () => { cancelled = true; };
  }, [base, https]);

  if (!https || options === null) return null;

  if (options.length === 0) {
    return <div style={box}><p style={{ ...muted, marginTop: 0 }}>No saved credentials yet. Add an access token under Resources, then Credentials, to use it here.</p></div>;
  }

  const known = value === null || options.some(o => o.id === value);

  return (
    <div style={box}>
      <label htmlFor="jit-credential"><strong>Use a saved credential</strong></label>
      <HoverTip label="Use a saved credential">
        {'For a private repository on an https address, pick an access token you already saved under Resources.\nIt is used only while the check or the deploy runs, then removed from the server.'}
      </HoverTip>
      <div style={{ marginTop: 6 }}>
        <select id="jit-credential" aria-label="Saved credential" style={input} value={value ?? ''} onChange={e => onChange(e.target.value === '' ? null : e.target.value)}>
          <option value="">No saved credential</option>
          {options.map(o => <option key={o.id} value={o.id}>{o.name}{o.hostPattern ? ` (${o.hostPattern})` : ''}</option>)}
        </select>
      </div>
      {!known && <p role="alert" style={{ ...muted, color: '#E08A8A' }}>The credential you chose is not in the list any more. Pick another one.</p>}
      <p style={muted}>
        The token is never saved in the address, the settings, the logs or the server&apos;s git settings. While it is in use it is held in a
        temporary file that only the login user can read, and anyone with administrator access to the server could see it during that time.
        A deploy key (SSH address) avoids that.
      </p>
    </div>
  );
}
