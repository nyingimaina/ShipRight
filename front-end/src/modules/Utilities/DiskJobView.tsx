import ZestButton from 'jattac.libs.web.zest-button';
import { IActiveBuild, IDiskJob, IDiskJobStep } from '@/shared/types/IDiskCleanup';
import { formatDuration, progressOf } from './jobProgress';
import BlockedBuilds from './BlockedBuilds';
import styles from './DiskCleanupPanel.module.css';

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];

export function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B';
  let value = bytes;
  let i = 0;
  while (value >= 1024 && i < UNITS.length - 1) { value /= 1024; i++; }
  return `${Math.round(value * 10) / 10} ${UNITS[i]}`;
}

const STEP_MARK: Record<IDiskJobStep['status'], string> = {
  Pending: '○', Running: '●', Done: '✓', Failed: '✕', Skipped: '–',
};

function stepTime(s: IDiskJobStep, nowMs: number): string {
  const start = s.startedAt ? Date.parse(s.startedAt) : null;
  const end = s.finishedAt ? Date.parse(s.finishedAt) : null;
  if (s.status === 'Running' && start) return `running for ${formatDuration(Math.max(0, (nowMs - start) / 1000))}`;
  if ((s.status === 'Done' || s.status === 'Failed') && start && end) return `took ${formatDuration(Math.max(0, (end - start) / 1000))}`;
  if (s.status === 'Pending') return `about ${formatDuration(s.estimatedSeconds)}`;
  return '';
}

interface Props {
  job: IDiskJob;
  nowMs: number;
  busy: boolean;
  /** Builds the person chose to stop, one or many. */
  onAbortBuilds: (builds: IActiveBuild[]) => void;
  /** Builds that could not be cancelled because nothing is running behind them; null when there are none. */
  stale: IActiveBuild[] | null;
  onForceClean: () => void;
  onLeaveStale: () => void;
  onRetry: () => void;
  onClose: () => void;
}

const TITLES: Record<IDiskJob['status'], string> = {
  Running: 'Working on it…',
  Succeeded: 'All done',
  Failed: 'Something went wrong',
  Blocked: 'A build is in the way',
};

export default function DiskJobView({ job, nowMs, busy, onAbortBuilds, stale, onForceClean, onLeaveStale, onRetry, onClose }: Props) {
  const p = progressOf(job, nowMs);
  const elapsed = Math.max(0, ((job.finishedAt ? Date.parse(job.finishedAt) : nowMs) - Date.parse(job.startedAt)) / 1000);
  const running = job.steps.findIndex(s => s.status === 'Running');
  const stepNumber = running >= 0 ? running + 1 : Math.min(p.doneCount + 1, p.total);
  const runningStep = job.steps[running];

  return (
    <div className={styles.job} aria-live="polite">
      <h3 className={styles.jobTitle}>{TITLES[job.status]}</h3>

      {job.status !== 'Blocked' && (
        <>
          <div
            className={styles.bar}
            role="progressbar"
            aria-label="Overall progress"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={p.percent}
          >
            <div className={`${styles.barFill} ${job.status === 'Failed' ? styles.barFailed : ''}`} style={{ width: `${p.percent}%` }} />
          </div>
          <div className={styles.stats}>
            <span>{p.percent}%</span>
            <span>Step {stepNumber} of {p.total}</span>
            <span>Elapsed {formatDuration(elapsed)}</span>
            {job.status === 'Running' && p.remainingSeconds !== null && !p.overrun && (
              <span>About {formatDuration(p.remainingSeconds)} left (estimate)</span>
            )}
            {job.status === 'Running' && p.overrun && (
              <span>Taking longer than expected — please keep this window open</span>
            )}
          </div>

          <ol className={styles.steps}>
            {job.steps.map(s => (
              <li key={s.id} className={`${styles.step} ${styles[`step${s.status}`]}`}>
                <span className={styles.stepMark} aria-hidden="true">{STEP_MARK[s.status]}</span>
                <span className={styles.stepLabel}>{s.label}</span>
                <span className={styles.stepTime}>{stepTime(s, nowMs)}</span>
              </li>
            ))}
          </ol>
          {runningStep?.detail && <p className={styles.note}>{runningStep.detail}</p>}
          {job.status === 'Running' && runningStep?.id === 'shrink' && (
            <p className={styles.note}>
              Shrinking cannot report its own progress, so the bar is an estimate based on the size of your disk file.
            </p>
          )}
        </>
      )}

      {job.status === 'Blocked' && (
        <BlockedBuilds
          job={job}
          busy={busy}
          stale={stale}
          onAbortBuilds={onAbortBuilds}
          onForceClean={onForceClean}
          onLeaveStale={onLeaveStale}
          onRetry={onRetry}
          onClose={onClose}
        />
      )}

      {job.status === 'Succeeded' && (
        <div className={styles.summary}>
          {job.cleanup && job.cleanup.items.length > 0 && (
            <p className={styles.ok}>Removed {job.cleanup.items.filter(i => i.removed).length} of {job.cleanup.items.length} images.</p>
          )}
          {job.cleanup?.items.filter(i => !i.removed).map(i => (
            <p key={i.reference} className={styles.warn}>Kept {i.reference}{i.note ? ` — ${i.note}` : ''}</p>
          ))}
          {job.cleanup?.danglingOutput && <p className={styles.ok}>Leftover layers: {job.cleanup.danglingOutput}</p>}
          {job.cleanup?.buildCacheOutput && <p className={styles.ok}>Build cache: {job.cleanup.buildCacheOutput}</p>}
          {job.shrink?.files.map(f => (
            <p key={f.path} className={styles.ok}>Windows disk: {formatBytes(f.beforeBytes)} → {formatBytes(f.afterBytes)}</p>
          ))}
          <div className={styles.actions}>
            <ZestButton onClick={onClose} zest={{ visualOptions: { variant: 'standard' } }}>Done</ZestButton>
          </div>
        </div>
      )}

      {job.status === 'Failed' && (
        <div className={styles.summary}>
          <p className={styles.warn}>{job.error}</p>
          {job.shrink?.messages.map((m, i) => <p key={i} className={styles.note}>{m}</p>)}
          <div className={styles.actions}>
            <ZestButton onClick={onRetry} disabled={busy} zest={{ visualOptions: { variant: 'standard' } }}>Try again</ZestButton>
            <ZestButton onClick={onClose} zest={{ buttonStyle: 'outline' }}>Close</ZestButton>
          </div>
        </div>
      )}
    </div>
  );
}
