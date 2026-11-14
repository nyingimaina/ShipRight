import { useEffect, useState } from 'react';
import CopyBox from '@/modules/ServerWizard/CopyBox';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';

interface Props {
  base: string;
  repoUrl: string;
  useDeployKey: boolean;
  /** Turning the deploy key on or off is part of the settings, so it is saved with them. */
  onUseDeployKey: (on: boolean) => void;
}

interface AccessResult { ok: boolean; usesDeployKey: boolean; message: string }
interface KeyResult { publicKey: string | null; created: boolean; keyName: string | null; hostLabel: string; pageUrl: string | null; error: string | null }

const box: React.CSSProperties = { marginTop: 10, padding: '10px 12px', borderRadius: 8, border: '1px solid var(--border-subtle)', fontSize: 13 };
const muted: React.CSSProperties = { margin: '6px 0 0', fontSize: 12, color: 'var(--text-muted)' };
const btn: React.CSSProperties = { padding: '6px 14px', borderRadius: 6, border: '1px solid var(--border-subtle)', background: 'transparent', color: 'inherit', cursor: 'pointer', fontSize: 13 };

const messageOf = (e: unknown, fallback: string) =>
  e && typeof e === 'object' && 'message' in e && typeof (e as { message: unknown }).message === 'string' ? (e as { message: string }).message : fallback;

/**
 * "Can the server read your code?": checks it, and when it cannot, makes a read-only key on the server itself and shows it
 * so it can be pasted into the git host. No password or token is ever involved, and nothing leaves the server but the public half.
 */
export default function JitGitAccess({ base, repoUrl, useDeployKey, onUseDeployKey }: Props) {
  const [access, setAccess] = useState<AccessResult | null>(null);
  const [key, setKey] = useState<KeyResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => { setAccess(null); setError(null); }, [repoUrl]);

  const check = async () => {
    setBusy(true); setError(null);
    try {
      setAccess(await api.post<AccessResult>(`${base}/git-access`, { repoUrl, useDeployKey }));
    } catch (e) {
      setAccess(null);
      setError(messageOf(e, 'Could not check.'));
    } finally { setBusy(false); }
  };

  const makeKey = async () => {
    setBusy(true); setError(null);
    try {
      const k = await api.post<KeyResult>(`${base}/deploy-key`, { repoUrl });
      if (k.error || !k.publicKey) { setError(k.error ?? 'Could not make a key.'); return; }
      setKey(k);
      onUseDeployKey(true);
    } catch (e) {
      setError(messageOf(e, 'Could not make a key.'));
    } finally { setBusy(false); }
  };

  return (
    <div style={box}>
      <strong>Can the server read your code?</strong>
      <HoverTip label="Can the server read your code">
        {'The build happens on the server, so the server must be allowed to download your repository.\nFor a public repository nothing is needed. For a private one, ShipRight can make a read-only key on the server and you paste it into GitHub, GitLab or Bitbucket. No password or token is stored anywhere.'}
      </HoverTip>

      {useDeployKey && !key && (
        <p style={muted}>
          This project reads your code with a deploy key made on the server.{' '}
          <button type="button" style={btn} onClick={makeKey} disabled={busy}>Show the deploy key</button>{' '}
          <button type="button" style={btn} onClick={() => onUseDeployKey(false)}>Stop using the deploy key</button>
        </p>
      )}

      <div style={{ marginTop: 8 }}>
        <button type="button" style={btn} disabled={repoUrl.trim() === '' || busy} onClick={check}>{busy ? 'Checking…' : 'Check access'}</button>
      </div>

      {access?.ok && <p role="status" style={{ ...muted, color: '#3D9970' }}>✓ {access.message}{access.usesDeployKey ? ' (using the deploy key)' : ''}</p>}
      {access && !access.ok && (
        <div>
          <p role="alert" style={{ ...muted, color: '#E08A8A' }}>{access.message}</p>
          {!key && <button type="button" style={{ ...btn, marginTop: 8 }} disabled={busy} onClick={makeKey}>Create a deploy key on the server</button>}
        </div>
      )}
      {error && <p role="alert" style={{ ...muted, color: '#E08A8A' }}>{error}</p>}

      {key?.publicKey && (
        <div style={{ marginTop: 10 }}>
          <p style={muted}>
            1. {key.pageUrl
              ? <a href={key.pageUrl} target="_blank" rel="noreferrer noopener">Open {key.hostLabel}&apos;s deploy keys page</a>
              : <>In {key.hostLabel || 'your git host'}, open the repository&apos;s settings and add a deploy key.</>}
          </p>
          <p style={muted}>2. Add a new deploy key and paste the key below. Leave write access off: this key only lets the server read.</p>
          <CopyBox label="the deploy key" text={key.publicKey} where="Paste this into your git host" />
          <p style={muted}>3. Save these settings, then press Check access again.</p>
        </div>
      )}
    </div>
  );
}
