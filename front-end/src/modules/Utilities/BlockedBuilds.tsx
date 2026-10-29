import { useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import ZestButton from 'jattac.libs.web.zest-button';
import { IActiveBuild, IDiskJob } from '@/shared/types/IDiskCleanup';
import styles from './DiskCleanupPanel.module.css';

interface Props {
  job: IDiskJob;
  busy: boolean;
  stale: IActiveBuild[] | null;
  onAbortBuilds: (builds: IActiveBuild[]) => void;
  onForceClean: () => void;
  onLeaveStale: () => void;
  onRetry: () => void;
  onClose: () => void;
}

/** Builds that are in the way of stopping WSL, with one-click ways to get them out of the way. */
export default function BlockedBuilds({ job, busy, stale, onAbortBuilds, onForceClean, onLeaveStale, onRetry, onClose }: Props) {
  const abortable = job.blockers.filter(b => b.canAbort);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const allBox = useRef<HTMLInputElement>(null);

  // Forget a selection for builds that are gone (for example after a re-check).
  useEffect(() => {
    setSelected(prev => new Set([...prev].filter(id => abortable.some(b => b.id === id))));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [job.blockers]);

  const allSelected = abortable.length > 0 && abortable.every(b => selected.has(b.id));
  const someSelected = selected.size > 0 && !allSelected;

  useEffect(() => {
    if (allBox.current) allBox.current.indeterminate = someSelected;
  }, [someSelected]);

  const toggle = (id: string) => setSelected(prev => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });

  const toggleAll = () => setSelected(allSelected ? new Set() : new Set(abortable.map(b => b.id)));

  return (
    <>
      <p className={styles.note}>{job.error ?? 'A build is active.'} Nothing was deleted.</p>

      {stale && stale.length > 0 ? (
        <div className={styles.confirm} role="alertdialog" aria-label="Force clean stuck builds">
          <p>
            {stale.length === 1 ? 'This build cannot be cancelled because' : `These ${stale.length} builds cannot be cancelled because`} nothing
            is running behind {stale.length === 1 ? 'it' : 'them'} — ShipRight lost track of {stale.length === 1 ? 'it' : 'them'}, but
            {stale.length === 1 ? ' it is' : ' they are'} still marked as active, which stops the disk from being shrunk.
            Force cleaning marks {stale.length === 1 ? 'it' : 'them'} as aborted and removes {stale.length === 1 ? 'its' : 'their'} leftover build files.
          </p>
          <ul className={styles.staleList}>
            {stale.map(b => <li key={b.id}>{b.projectName || 'A build'} <span className={styles.meta}>({b.status})</span></li>)}
          </ul>
          <div className={styles.actions}>
            <ZestButton onClick={onForceClean} disabled={busy} zest={{ visualOptions: { variant: 'danger' } }}>Force clean and continue</ZestButton>
            <ZestButton onClick={onLeaveStale} disabled={busy} zest={{ buttonStyle: 'outline' }}>Leave them</ZestButton>
          </div>
        </div>
      ) : (
        <>
          {abortable.length > 0 && (
            <div className={styles.bulkBar}>
              <label className={styles.option}>
                <input
                  ref={allBox}
                  type="checkbox"
                  checked={allSelected}
                  onChange={toggleAll}
                  disabled={busy}
                  aria-label={`Select all (${abortable.length})`}
                />
                Select all ({abortable.length})
              </label>
              <ZestButton
                onClick={() => onAbortBuilds(abortable.filter(b => selected.has(b.id)))}
                disabled={busy || selected.size === 0}
                zest={{ visualOptions: { variant: 'danger' } }}
              >
                {selected.size > 0 ? `Abort selected builds and continue (${selected.size})` : 'Abort selected builds and continue'}
              </ZestButton>
            </div>
          )}

          <ul className={styles.blockers}>
            {job.blockers.map(b => (
              <li key={b.id} className={styles.blocker}>
                <div className={styles.blockerInfo}>
                  {b.canAbort && (
                    <input
                      type="checkbox"
                      className={styles.pick}
                      checked={selected.has(b.id)}
                      onChange={() => toggle(b.id)}
                      disabled={busy}
                      aria-label={`Select ${b.projectName || 'build'} (${b.status})`}
                    />
                  )}
                  <strong>{b.projectName || 'A build'}</strong>
                  <span className={styles.badge}>{b.status}</span>
                  {b.step && <span className={styles.meta}>at {b.step}</span>}
                  {!b.canAbort && <span className={styles.meta}>waiting to start — it will begin by itself</span>}
                </div>
                <div className={styles.blockerActions}>
                  {b.canAbort && (
                    <ZestButton onClick={() => onAbortBuilds([b])} disabled={busy} zest={{ visualOptions: { variant: 'danger' } }}>
                      Abort this build and continue
                    </ZestButton>
                  )}
                  <Link className={styles.linkButton} href="/history">Open build history</Link>
                </div>
              </li>
            ))}
          </ul>

          <div className={styles.actions}>
            <ZestButton onClick={onRetry} disabled={busy} zest={{ buttonStyle: 'outline' }}>Check again</ZestButton>
            <ZestButton onClick={onClose} zest={{ buttonStyle: 'outline' }}>Close</ZestButton>
          </div>
        </>
      )}
    </>
  );
}
