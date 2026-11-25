import { useCallback, useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api, apiUrl } from '@/shared/ApiService';
import { fmtDate } from './backupTypes';
import styles from './RecoveryKeyPanel.module.css';

interface KeyInfo { id: string; createdAt: string; active: boolean; }
interface RecoveryFile { activeKeyId: string; keys: { id: string; key: string; createdAt: string }[]; }

function downloadJson(file: RecoveryFile) {
  const blob = new Blob([JSON.stringify(file, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `shipright-backup-keys-${new Date().toISOString().slice(0, 10)}.json`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

export default function RecoveryKeyPanel() {
  const [keys, setKeys] = useState<KeyInfo[]>([]);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const result = await api.get<{ keys: KeyInfo[] }>('/api/backups/recovery-key');
      setKeys(result.keys);
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not load the backup keys.');
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  const rotate = async () => {
    if (!window.confirm(
      'Create a new backup key?\n\nNew backups use it immediately. Older backups keep their own key until it is retired. ' +
      'Download the recovery file afterwards — it contains every key.')) return;

    setBusy(true);
    try {
      const result = await api.post<{ activeKeyId: string; recovery: RecoveryFile }>(
        '/api/backups/recovery-key/rotate', {});
      downloadJson(result.recovery);
      toast.success('New backup key active. Recovery file downloaded.');
      await load();
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not rotate the backup key.');
    } finally {
      setBusy(false);
    }
  };

  const retire = async (key: KeyInfo) => {
    if (!window.confirm(`Retire key ${key.id.slice(0, 8)}?\n\nOnly do this once no backup uses it.`)) return;

    setBusy(true);
    try {
      await api.post('/api/backups/recovery-key/retire', { keyId: key.id });
      toast.success('Key retired.');
      await load();
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not retire the key.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.wrap}>
      <button
        type="button"
        className={styles.toggle}
        aria-expanded={open}
        aria-controls="recovery-keys"
        onClick={() => setOpen(o => !o)}
      >
        {open ? '▾' : '▸'} Recovery keys
        <span className={styles.subtle}>{keys.length} key{keys.length === 1 ? '' : 's'}</span>
      </button>

      {open && (
        <div id="recovery-keys" className={styles.body}>
          <p className={styles.note}>
            Backups are encrypted at rest with a key the server manages. Download the recovery file and keep it offline:
            it opens every backup, including those made before a rotation. Losing the app data directory without this
            file makes encrypted backups unreadable.
          </p>

          <div className={styles.actions}>
            <a className={styles.download} href={apiUrl('/api/backups/recovery-key/export')}>
              Download recovery file
            </a>
            <ZestButton onClick={rotate} disabled={busy}
              zest={{ buttonStyle: 'outline', semanticType: 'refresh', visualOptions: { size: 'sm' } }}>
              Rotate key
            </ZestButton>
          </div>

          <ul className={styles.keys}>
            {keys.map(key => (
              <li key={key.id} className={styles.keyRow}>
                <code>{key.id.slice(0, 8)}…</code>
                <span className={styles.subtle}>{fmtDate(key.createdAt)}</span>
                {key.active
                  ? <span className={styles.active}>active</span>
                  : (
                    <ZestButton onClick={() => retire(key)} disabled={busy}
                      zest={{ buttonStyle: 'text', semanticType: 'delete', visualOptions: { size: 'sm', variant: 'danger' } }}>
                      Retire
                    </ZestButton>
                  )}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
