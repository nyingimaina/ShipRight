import { IDiskJob } from '@/shared/types/IDiskCleanup';

export function formatDuration(seconds: number): string {
  if (seconds < 10) return 'a few seconds';
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m === 0 ? `${h} h` : `${h} h ${m} min`;
}

export interface JobProgress {
  percent: number;
  doneCount: number;
  total: number;
  /** Null when nothing is left to wait for. */
  remainingSeconds: number | null;
  /** The running step is past its estimate, so no honest number can be given for it. */
  overrun: boolean;
}

/**
 * Progress is time-weighted by each step's estimate. The estimates are guesses (a shrink in particular cannot report
 * its own progress), so a running step never reaches 100% on its own and a step that overruns is reported as such.
 */
export function progressOf(job: IDiskJob, nowMs: number): JobProgress {
  const total = job.steps.length;
  const settled = (s: string) => s === 'Done' || s === 'Failed' || s === 'Skipped';
  const doneCount = job.steps.filter(s => settled(s.status)).length;

  if (job.status !== 'Running') {
    return { percent: job.status === 'Blocked' ? 0 : 100, doneCount, total, remainingSeconds: null, overrun: false };
  }

  const weight = job.steps.reduce((n, s) => n + s.estimatedSeconds, 0) || 1;
  let finished = 0;
  let remaining = 0;
  let overrun = false;
  let anyRunning = false;

  for (const s of job.steps) {
    if (settled(s.status)) { finished += s.estimatedSeconds; continue; }
    if (s.status === 'Running') {
      anyRunning = true;
      const elapsed = s.startedAt ? Math.max(0, (nowMs - Date.parse(s.startedAt)) / 1000) : 0;
      finished += Math.min(elapsed, s.estimatedSeconds * 0.95);
      if (elapsed > s.estimatedSeconds) overrun = true; else remaining += s.estimatedSeconds - elapsed;
      continue;
    }
    remaining += s.estimatedSeconds;
  }

  return {
    percent: Math.min(100, Math.round((100 * finished) / weight)),
    doneCount,
    total,
    remainingSeconds: anyRunning || remaining > 0 ? Math.round(remaining) : null,
    overrun,
  };
}
