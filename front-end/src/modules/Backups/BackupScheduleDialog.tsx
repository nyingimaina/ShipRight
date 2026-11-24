import { useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { runZestAction } from '@/shared/components/ZestAsync/zestAction';
import { IProject } from '@/shared/types/IProject';
import styles from './BackupScheduleDialog.module.css';

interface Props {
  projects: IProject[];
  onClose: () => void;
  onScheduled: () => void;
}

interface Preset {
  id: string;
  label: string;
  cron?: string;
}

const PRESETS: Preset[] = [
  { id: 'daily-2', label: 'Every day at 02:00', cron: '0 2 * * *' },
  { id: 'daily-14', label: 'Every day at 14:00', cron: '0 14 * * *' },
  { id: 'every-6h', label: 'Every 6 hours', cron: '0 */6 * * *' },
  { id: 'weekly-sun-3', label: 'Every Sunday at 03:00', cron: '0 3 * * 0' },
  { id: 'custom', label: 'Custom cron expression' },
];

/** Creates a backup schedule for a project that already has a database. */
export default function BackupScheduleDialog({ projects, onClose, onScheduled }: Props) {
  const databaseProjects = projects.filter((p) => p.database?.databaseName);
  const [projectId, setProjectId] = useState(databaseProjects[0]?.id ?? '');
  const [presetId, setPresetId] = useState(PRESETS[0].id);
  const [cron, setCron] = useState(PRESETS[0].cron ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const errorRef = useRef<HTMLParagraphElement>(null);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape' && !busy) onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [busy, onClose]);

  const project = databaseProjects.find((p) => p.id === projectId);
  const effectiveCron = PRESETS.find((p) => p.id === presetId)?.cron ?? cron;

  const submit = async () => {
    if (!project) {
      setError('Choose a project with a database.');
      return;
    }
    if (!effectiveCron.trim()) {
      setError('Choose a schedule or enter a cron expression.');
      return;
    }

    setBusy(true);
    setError('');
    try {
      await runZestAction(
        () => api.post('/api/scheduler/schedules', {
          projectId: project.id,
          cronExpression: effectiveCron.trim(),
          timeZone: project.timeZone || 'UTC',
        }),
        { fallbackMessage: 'Could not schedule the backup.', onError: setError, errorRef },
      );
      toast.success('Backup scheduled.');
      onScheduled();
      onClose();
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.overlay} role="presentation" onMouseDown={(e) => { if (e.target === e.currentTarget && !busy) onClose(); }}>
      <div className={styles.dialog} role="dialog" aria-modal="true" aria-label="Schedule a backup">
        <div className={styles.headingRow}>
          <div>
            <h2 className={styles.title}>Schedule a backup</h2>
            <p className={styles.subtitle}>ShipRight will run it automatically and keep the history here.</p>
          </div>
          <button type="button" className={styles.close} aria-label="Close" onClick={onClose} disabled={busy}>×</button>
        </div>

        {databaseProjects.length === 0 ? (
          <p className={styles.empty}>Add a database to a project before scheduling backups.</p>
        ) : (
          <div className={styles.fields}>
            <label className={styles.field}>
              <span>Project</span>
              <select value={projectId} onChange={(e) => setProjectId(e.target.value)}>
                {databaseProjects.map((p) => (
                  <option key={p.id} value={p.id}>{p.name} · {p.database?.databaseName}</option>
                ))}
              </select>
            </label>

            <label className={styles.field}>
              <span>Schedule</span>
              <select
                value={presetId}
                onChange={(e) => {
                  setPresetId(e.target.value);
                  const preset = PRESETS.find((p) => p.id === e.target.value);
                  if (preset?.cron) setCron(preset.cron);
                }}
              >
                {PRESETS.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
              </select>
            </label>

            {presetId === 'custom' && (
              <label className={styles.field}>
                <span>Cron expression</span>
                <input
                  value={cron}
                  onChange={(e) => setCron(e.target.value)}
                  placeholder="0 2 * * *"
                  spellCheck={false}
                  autoCapitalize="none"
                />
                <small>Minute hour day month weekday. Example: 0 2 * * *</small>
              </label>
            )}

            <p className={styles.note}>Times use the project time zone ({project?.timeZone || 'UTC'}).</p>
          </div>
        )}

        {error && <p ref={errorRef} role="alert" className={styles.error}>{error}</p>}

        <div className={styles.actions}>
          <ZestButton onClick={onClose} disabled={busy} zest={{ buttonStyle: 'text', semanticType: 'cancel' }}>
            Cancel
          </ZestButton>
          <ZestButton
            onClick={submit}
            disabled={busy || databaseProjects.length === 0}
            zest={{ semanticType: 'save', visualOptions: { variant: 'standard' } }}
          >
            {busy ? 'Scheduling…' : 'Schedule backup'}
          </ZestButton>
        </div>
      </div>
    </div>
  );
}
