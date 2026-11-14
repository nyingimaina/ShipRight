import { useEffect, useState } from 'react';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';
import styles from './Styles/JitDeploy.module.css';

interface Props {
  base: string;
  repoUrl: string;
  /** The saved credential chosen for this project, if any. */
  value: string | null;
  onChange: (id: string | null) => void;
}

interface CredentialOption { id: string; name: string; hostPattern: string | null }


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
    return <div className={styles.box}><p className={`${styles.muted} ${styles.mt0}`}>No saved credentials yet. Add an access token under Resources, then Credentials, to use it here.</p></div>;
  }

  const known = value === null || options.some(o => o.id === value);

  return (
    <div className={styles.box}>
      <label htmlFor="jit-credential">Saved credential (optional)</label>
      <HoverTip label="Saved credential">
        {'For a private repository on an https address, pick an access token you already saved under Resources.\nThe token is never saved in the address, the settings, the logs or the server\'s git settings. It is used only while a check or a deploy runs, in a temporary file only the login user can read, and anyone with administrator access to the server could see it during that time. A deploy key (SSH address) avoids that.'}
      </HoverTip>
      <div className={styles.mt6}>
        <select id="jit-credential" aria-label="Saved credential" className={styles.input} value={value ?? ''} onChange={e => onChange(e.target.value === '' ? null : e.target.value)}>
          <option value="">No saved credential</option>
          {options.map(o => <option key={o.id} value={o.id}>{o.name}{o.hostPattern ? ` (${o.hostPattern})` : ''}</option>)}
        </select>
      </div>
      {!known && <p role="alert" className={`${styles.muted} ${styles.bad}`}>The credential you chose is not in the list any more. Pick another one.</p>}
    </div>
  );
}
