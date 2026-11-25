import { useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import { api, apiUrl } from '@/shared/ApiService';
import styles from './DecryptBackupPanel.module.css';

/**
 * Manual recovery: take a backup file copied from the host (or downloaded raw) and the exported recovery
 * JSON, and get the plain dump back. Nothing is stored after the response completes.
 */
export default function DecryptBackupPanel() {
  const [artifact, setArtifact] = useState<File | null>(null);
  const [recovery, setRecovery] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const submit = async (event?: React.FormEvent) => {
    event?.preventDefault();
    if (!artifact) {
      setError('Choose the backup file you want to decrypt.');
      return;
    }

    setBusy(true);
    setError('');
    try {
      const form = new FormData();
      form.append('artifact', artifact);
      if (recovery) form.append('recovery', recovery);

      // The server validates the key first; the download then streams straight to disk (no browser buffering).
      const result = await api.uploadForm<{ token: string; fileName: string }>('/api/backups/decrypt', form);
      const anchor = document.createElement('a');
      anchor.href = apiUrl(`/api/backups/decrypt/${result.token}`);
      anchor.download = result.fileName;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
    } catch (failure) {
      setError((failure as { message?: string })?.message ?? 'Could not decrypt that file.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className={styles.wrap} onSubmit={submit}>
      <p className={styles.note}>
        Have a backup file but not a working ShipRight? Choose the file and the recovery JSON you exported from
        the Backups page. Encrypted <code>.sql.zst.enc</code> files are decrypted and decompressed; plain
        <code> .sql</code> or <code>.bak</code> files pass straight through. The upload is temporary and deleted
        when the download finishes.
      </p>

      <div className={styles.fields}>
        <label className={styles.field}>
          <span>Backup file</span>
          <input
            type="file"
            aria-label="Backup file to decrypt"
            onChange={e => { setArtifact(e.target.files?.[0] ?? null); setError(''); }}
          />
          <small>{artifact ? artifact.name : 'Choose the file copied from the backup folder'}</small>
        </label>

        <label className={styles.field}>
          <span>Recovery file (JSON)</span>
          <input
            type="file"
            accept="application/json,.json"
            aria-label="Recovery JSON file"
            onChange={e => { setRecovery(e.target.files?.[0] ?? null); setError(''); }}
          />
          <small>
            {recovery ? recovery.name : 'Only needed for encrypted files; exported from the Recovery keys panel'}
          </small>
        </label>
      </div>

      {error && <p role="alert" className={styles.error}>{error}</p>}

      <ZestButton
        onClick={submit}
        disabled={busy}
        zest={{ semanticType: 'submit', visualOptions: { variant: 'standard' } }}
      >
        {busy ? 'Decrypting.' : 'Decrypt and download'}
      </ZestButton>
    </form>
  );
}
