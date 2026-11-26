import { useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { IProject } from '@/shared/types/IProject';
import styles from '@/pages/Styles/Backups.module.css';

interface Props {
  project: IProject;
  onSaved: () => void;
}

/** Retention, compression and encryption for one project's backups. Shared by the project tab and the control room. */
export default function BackupPolicyEditor({ project, onSaved }: Props) {
  const cfg = project.database;
  const [keepCount, setKeepCount] = useState(cfg?.backupRetainCount ?? 10);
  const [keepDays, setKeepDays] = useState(cfg?.backupRetainDays ?? 0);
  const [compression, setCompression] = useState(cfg?.backupCompressionLevel ?? 12);
  const [encrypt, setEncrypt] = useState(cfg?.backupEncrypt ?? true);
  const [saving, setSaving] = useState(false);

  const save = async () => {
    setSaving(true);
    try {
      await api.put(`/api/projects/${project.id}/db/backup-policy`, {
        keepCount,
        keepDays,
        compressionLevel: compression,
        encrypt,
      });
      toast.success('Backup policy saved.');
      onSaved();
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not save the backup policy.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className={styles.policy}>
      <label className={styles.policyField}>
        <span>Keep last</span>
        <input
          type="number"
          min={1}
          max={3650}
          value={keepCount}
          onChange={e => setKeepCount(Math.max(1, Math.min(3650, Number(e.target.value) || 1)))}
          aria-label={`Backups to keep for ${project.name}`}
        />
      </label>
      <label className={styles.policyField}>
        <span>Delete older than (days)</span>
        <input
          type="number"
          min={0}
          max={3650}
          value={keepDays}
          onChange={e => setKeepDays(Math.max(0, Math.min(3650, Number(e.target.value) || 0)))}
          aria-label={`Age limit in days for ${project.name}`}
        />
      </label>
      <label className={styles.policyField}>
        <span>Compression</span>
        <select
          value={compression}
          onChange={e => setCompression(Number(e.target.value))}
          aria-label={`Compression level for ${project.name}`}
        >
          <option value={0}>None (fastest)</option>
          <option value={3}>zstd 3 (fast)</option>
          <option value={12}>zstd 12 (balanced)</option>
          <option value={19}>zstd 19 (smallest)</option>
        </select>
      </label>
      <label className={styles.policyCheck}>
        <input type="checkbox" checked={encrypt} onChange={e => setEncrypt(e.target.checked)} />
        <span>Encrypt at rest</span>
      </label>
      <ZestButton
        onClick={save}
        disabled={saving}
        zest={{ semanticType: 'save', visualOptions: { size: 'sm', variant: 'standard' } }}
      >
        {saving ? 'Saving.' : 'Save policy'}
      </ZestButton>
    </div>
  );
}
