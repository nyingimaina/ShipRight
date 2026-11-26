import { useCallback, useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { IProject } from '@/shared/types/IProject';
import { formatDateTime } from '@/shared/time';
import BackupFiles from './BackupFiles';
import BackupPolicyEditor from './BackupPolicyEditor';
import BackupScheduleDialog, { EditableSchedule } from './BackupScheduleDialog';
import styles from './ProjectBackupsPanel.module.css';

interface ProjectSchedule {
  scheduleId: string;
  cron: string;
  timeZone: string;
  nextFireAt: string | null;
  lastFiredAt: string | null;
  totalFired: number;
  totalMissed: number;
}

/**
 * Everything about this project's backups, in the project itself: policy, schedule, history and files. The shared
 * Backups page is the control room across projects; this is where a single project is actually looked after.
 */
export default function ProjectBackupsPanel({ project, onProjectChanged }: {
  project: IProject;
  onProjectChanged?: () => void;
}) {
  const [schedules, setSchedules] = useState<ProjectSchedule[]>([]);
  const [loading, setLoading] = useState(true);
  const [revision, setRevision] = useState(0);
  const [dialog, setDialog] = useState<{ initial?: EditableSchedule } | null>(null);
  const [backingUp, setBackingUp] = useState(false);

  const load = useCallback(async () => {
    try {
      setSchedules(await api.get<ProjectSchedule[]>(`/api/projects/${project.id}/backup-schedule`));
    } catch {
      setSchedules([]);
    } finally {
      setLoading(false);
    }
  }, [project.id]);

  useEffect(() => { void load(); }, [load]);

  const remove = async (scheduleId: string) => {
    try {
      await api.delete(`/api/scheduler/schedules/${scheduleId}`);
      toast.success('Schedule removed.');
      await load();
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not remove the schedule.');
    }
  };

  const backupNow = async () => {
    setBackingUp(true);
    try {
      await api.post(`/api/projects/${project.id}/db/backup`, {});
      toast.success('Backup started. It will appear below when it finishes.');
      // Give the container a moment, then refresh the file list.
      window.setTimeout(() => setRevision(r => r + 1), 2500);
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not start the backup.');
    } finally {
      setBackingUp(false);
    }
  };

  return (
    <div className={styles.panel}>
      <div className={styles.head}>
        <div>
          <h2 className={styles.title}>Backups</h2>
          <p className={styles.subtitle}>
            {project.database?.databaseName} · kept on this ShipRight server, not on the database host.
          </p>
        </div>
        <ZestButton
          onClick={backupNow}
          disabled={backingUp}
          zest={{ visualOptions: { size: 'sm', variant: 'standard' }, semanticType: 'submit' }}
        >
          {backingUp ? 'Starting…' : 'Back up now'}
        </ZestButton>
      </div>

      <BackupPolicyEditor
        project={project}
        onSaved={() => { onProjectChanged?.(); setRevision(r => r + 1); }}
      />

      <div className={styles.scheduleSection}>
        <div className={styles.sectionHead}>
          <div>
            <h3>Schedule</h3>
            <p>Saved schedules run even after a restart.</p>
          </div>
          <ZestButton
            onClick={() => setDialog({})}
            zest={{ buttonStyle: 'outline', semanticType: 'add', visualOptions: { size: 'sm' } }}
          >
            New schedule
          </ZestButton>
        </div>

        {loading ? (
          <p className={styles.muted}>Loading schedules…</p>
        ) : schedules.length === 0 ? (
          <button type="button" className={styles.emptySchedule} onClick={() => setDialog({})}>
            <span>No schedule yet</span>
            <span className={styles.emptyHint}>Add one so this database is protected without anyone remembering.</span>
          </button>
        ) : (
          <ul className={styles.scheduleList}>
            {schedules.map(schedule => (
              <li key={schedule.scheduleId} className={styles.scheduleRow}>
                <div className={styles.scheduleMain}>
                  <span className={styles.cron}>{schedule.cron}</span>
                  <span className={styles.scheduleMeta}>
                    {(schedule.timeZone || 'Africa/Nairobi').replace(/_/g, ' ')}
                    {schedule.nextFireAt ? ` · next ${formatDateTime(schedule.nextFireAt)}` : ''}
                  </span>
                  <span className={styles.scheduleMeta}>
                    {schedule.lastFiredAt ? `Last ran ${formatDateTime(schedule.lastFiredAt)}` : 'Not run yet'}
                    {schedule.totalMissed > 0 ? ` · ${schedule.totalMissed} missed` : ''}
                  </span>
                </div>
                <div className={styles.scheduleActions}>
                  <ZestButton
                    onClick={() => setDialog({
                      initial: {
                        scheduleId: schedule.scheduleId,
                        projectId: project.id,
                        cron: schedule.cron,
                        timeZone: schedule.timeZone,
                      },
                    })}
                    zest={{ buttonStyle: 'outline', semanticType: 'edit', visualOptions: { size: 'sm' } }}
                  >
                    Edit
                  </ZestButton>
                  <ZestButton
                    onClick={() => void remove(schedule.scheduleId)}
                    zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                  >
                    Remove
                  </ZestButton>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className={styles.files}>
        <BackupFiles
          key={`${project.id}-${revision}-${project.database?.backupRetainCount}-${project.database?.backupRetainDays}`}
          projectId={project.id}
          onChanged={() => setRevision(r => r + 1)}
        />
      </div>

      {dialog && (
        <BackupScheduleDialog
          projects={[project]}
          initial={dialog.initial ?? null}
          onClose={() => setDialog(null)}
          onScheduled={() => { void load(); setRevision(r => r + 1); }}
        />
      )}
    </div>
  );
}
