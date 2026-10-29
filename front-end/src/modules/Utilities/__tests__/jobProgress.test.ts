import { formatDuration, progressOf } from '../jobProgress';
import { IDiskJob, IDiskJobStep } from '@/shared/types/IDiskCleanup';

const T0 = Date.parse('2026-10-29T10:00:00Z');
const at = (s: number) => new Date(T0 + s * 1000).toISOString();

const step = (over: Partial<IDiskJobStep>): IDiskJobStep => ({ id: 's', label: 'Step', status: 'Pending', estimatedSeconds: 60, ...over });
const job = (steps: IDiskJobStep[], over: Partial<IDiskJob> = {}): IDiskJob =>
  ({ id: 'j', status: 'Running', startedAt: at(0), steps, blockers: [], ...over });

describe('formatDuration', () => {
  it.each([
    [0, 'a few seconds'],
    [4, 'a few seconds'],
    [45, '45 s'],
    [60, '1 min'],
    [100, '2 min'],
    [3600, '1 h'],
    [3900, '1 h 5 min'],
  ])('%s seconds reads as "%s"', (s, text) => expect(formatDuration(s)).toBe(text));
});

describe('progressOf', () => {
  it('is 0% with nothing started, and counts every step', () => {
    const p = progressOf(job([step({ id: 'a' }), step({ id: 'b' })]), T0);
    expect(p.percent).toBe(0);
    expect(p.total).toBe(2);
    expect(p.doneCount).toBe(0);
  });

  it('weights steps by how long they are expected to take', () => {
    const p = progressOf(job([
      step({ id: 'a', status: 'Done', estimatedSeconds: 30, startedAt: at(0), finishedAt: at(30) }),
      step({ id: 'b', status: 'Pending', estimatedSeconds: 90 }),
    ]), T0 + 30_000);
    expect(p.percent).toBe(25);
    expect(p.doneCount).toBe(1);
  });

  it('moves a running step forward with time, but never claims it is finished', () => {
    const steps = [step({ id: 'a', status: 'Running', estimatedSeconds: 100, startedAt: at(0) })];
    expect(progressOf(job(steps), T0 + 50_000).percent).toBe(50);
    expect(progressOf(job(steps), T0 + 500_000).percent).toBe(95);
  });

  it('estimates the time left from the unfinished steps and the running one', () => {
    const p = progressOf(job([
      step({ id: 'a', status: 'Done', estimatedSeconds: 30, startedAt: at(0), finishedAt: at(30) }),
      step({ id: 'b', status: 'Running', estimatedSeconds: 100, startedAt: at(30) }),
      step({ id: 'c', status: 'Pending', estimatedSeconds: 60 }),
    ]), T0 + 70_000);
    expect(p.remainingSeconds).toBe(60 + 60);
    expect(p.overrun).toBe(false);
  });

  it('says so, instead of inventing a number, when the running step is taking longer than guessed', () => {
    const p = progressOf(job([
      step({ id: 'a', status: 'Running', estimatedSeconds: 60, startedAt: at(0) }),
      step({ id: 'b', status: 'Pending', estimatedSeconds: 30 }),
    ]), T0 + 200_000);
    expect(p.overrun).toBe(true);
    expect(p.remainingSeconds).toBe(30);
  });

  it('is 100% when the job succeeded, and has no time left', () => {
    const p = progressOf(job([step({ status: 'Done', startedAt: at(0), finishedAt: at(5) })], { status: 'Succeeded', finishedAt: at(5) }), T0 + 9_000);
    expect(p.percent).toBe(100);
    expect(p.remainingSeconds).toBeNull();
  });

  it('counts skipped steps as settled, so a failed job does not look stuck', () => {
    const p = progressOf(job([
      step({ id: 'a', status: 'Failed', estimatedSeconds: 10, startedAt: at(0), finishedAt: at(2) }),
      step({ id: 'b', status: 'Skipped', estimatedSeconds: 90 }),
    ], { status: 'Failed' }), T0 + 5_000);
    expect(p.percent).toBe(100);
    expect(p.remainingSeconds).toBeNull();
  });
});
