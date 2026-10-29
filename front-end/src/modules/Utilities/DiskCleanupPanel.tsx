import { useCallback, useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';
import {
  IActiveBuild, ICleanupImage, ICleanupPreview, IDiskJob, IDiskJobRequest, IWslDiskReport,
} from '@/shared/types/IDiskCleanup';
import DiskJobView, { formatBytes } from './DiskJobView';
import styles from './DiskCleanupPanel.module.css';

interface Props {
  /** How often a running job is re-read. */
  pollMs?: number;
}

const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

export default function DiskCleanupPanel({ pollMs = 1500 }: Props) {
  const [preview, setPreview] = useState<ICleanupPreview | null>(null);
  const [disk, setDisk] = useState<IWslDiskReport | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [pruneDangling, setPruneDangling] = useState(true);
  const [pruneBuildCache, setPruneBuildCache] = useState(true);
  const [confirmShrink, setConfirmShrink] = useState(false);
  const [job, setJob] = useState<IDiskJob | null>(null);
  const [busy, setBusy] = useState(false);
  const [stale, setStale] = useState<IActiveBuild[] | null>(null);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const lastRequest = useRef<IDiskJobRequest | null>(null);

  const scan = useCallback(async () => {
    try {
      const p = await api.get<ICleanupPreview>('/api/system/docker-cleanup');
      setPreview(p);
      setPicked(new Set(p.images.filter(i => i.selectedByDefault).map(i => i.reference)));
    } catch {
      setPreview({ images: [], inUseCount: 0, danglingCount: 0, selectedBytes: 0, diskUsage: '', error: 'Could not scan Docker. Is ShipRight running on this computer?' });
    }
    try {
      setDisk(await api.get<IWslDiskReport>('/api/system/wsl-disk'));
    } catch {
      setDisk(null);
    }
  }, []);

  useEffect(() => {
    scan();
    // A long job survives a page reload: pick it up again if one is still going (or just finished).
    api.get<IDiskJob | null>('/api/system/disk-job/latest').then(j => { if (j) setJob(j); }).catch(() => {});
  }, [scan]);

  const jobId = job?.id;
  const running = job?.status === 'Running';

  useEffect(() => {
    if (!running || !jobId) return;
    const poll = setInterval(async () => {
      try {
        const j = await api.get<IDiskJob | null>(`/api/system/disk-job/${jobId}`);
        if (j) setJob(j);
      } catch { /* keep the last known state; the next poll may succeed */ }
    }, pollMs);
    const tick = setInterval(() => setNowMs(Date.now()), 1000);
    return () => { clearInterval(poll); clearInterval(tick); };
  }, [running, jobId, pollMs]);

  const toggle = (ref: string) => setPicked(prev => {
    const next = new Set(prev);
    if (next.has(ref)) next.delete(ref); else next.add(ref);
    return next;
  });

  const start = async (request: IDiskJobRequest) => {
    lastRequest.current = request;
    setBusy(true);
    setConfirmShrink(false);
    try {
      setNowMs(Date.now());
      setJob(await api.post<IDiskJob>('/api/system/disk-job', request));
    } catch (e) {
      toast.error((e as { message?: string })?.message ?? 'Could not start.');
    } finally {
      setBusy(false);
    }
  };

  const requestFor = (shrink: boolean): IDiskJobRequest => ({
    // Keep the on-screen order so the request is predictable.
    images: (preview?.images ?? []).filter(i => picked.has(i.reference)).map(i => i.reference),
    pruneDangling,
    pruneBuildCache,
    shrink,
  });

  const retry = () => start(lastRequest.current ?? requestFor(false));

  /** Waits (briefly) until none of these builds is listed as active any more. */
  const waitUntilGone = async (ids: string[]) => {
    if (ids.length === 0) return;
    for (let i = 0; i < 40; i++) {
      try {
        const left = await api.get<IActiveBuild[]>('/api/system/wsl-disk/blockers');
        if (!left.some(b => ids.includes(b.id))) return;
      } catch { return; }
      await sleep(pollMs);
    }
  };

  /** Cancelling a build that has no pipeline behind it fails this way: it is stuck, not running. */
  const isStuck = (e: unknown) => /no active pipeline|not running/i.test((e as { message?: string })?.message ?? '');

  const abortBuilds = async (builds: IActiveBuild[]) => {
    if (builds.length === 0) return;
    setBusy(true);
    const stopped: string[] = [];
    const stuck: IActiveBuild[] = [];
    let failures = 0;
    for (const b of builds) {
      try {
        await api.post(`/api/builds/${b.id}/cancel`, {});
        stopped.push(b.id);
      } catch (e) {
        if (isStuck(e)) stuck.push(b); else failures++;
      }
    }
    if (failures > 0) {
      toast.error(failures === 1 ? 'A build could not be aborted. Open the build history to deal with it.' : `${failures} builds could not be aborted. Open the build history to deal with them.`);
    }
    await waitUntilGone(stopped);
    if (stuck.length > 0) {
      // Nothing is running behind these, so cancelling cannot work. Ask before clearing them by force.
      setStale(stuck);
      setBusy(false);
      return;
    }
    setBusy(false);
    if (failures === 0) await start(lastRequest.current ?? requestFor(true));
  };

  const forceClean = async () => {
    const targets = stale ?? [];
    setBusy(true);
    let failures = 0;
    for (const b of targets) {
      try { await api.post(`/api/builds/${b.id}/force-abort`, {}); } catch { failures++; }
    }
    setStale(null);
    if (failures > 0) toast.error('Some builds could not be cleared. Open the build history to look at them.');
    await waitUntilGone(targets.map(b => b.id));
    setBusy(false);
    if (failures === 0) await start(lastRequest.current ?? requestFor(true));
  };

  const close = () => { setJob(null); setStale(null); scan(); };

  const mine = (preview?.images ?? []).filter(i => i.owner === 'ShipRight');
  const others = (preview?.images ?? []).filter(i => i.owner !== 'ShipRight');
  const pickedBytes = (preview?.images ?? []).filter(i => picked.has(i.reference)).reduce((n, i) => n + i.sizeBytes, 0);
  const windowsDiskBytes = (disk?.vhdxFiles ?? []).reduce((n, f) => n + f.sizeBytes, 0);
  const cannotStart = busy || running || !preview || !!preview.error;

  const row = (img: ICleanupImage) => (
    <label key={img.reference} className={styles.row}>
      <input type="checkbox" checked={picked.has(img.reference)} onChange={() => toggle(img.reference)} aria-label={img.reference} disabled={!!running} />
      <span className={styles.ref}>{img.reference}</span>
      <span className={styles.meta}>{img.age}</span>
      <span className={styles.size}>{img.size}</span>
    </label>
  );

  return (
    <section className={styles.layout} aria-label="Free up disk space">
      <header className={styles.header}>
        <h2 className={styles.title}>Free up disk space</h2>
        <p className={styles.lead}>
          Docker images pile up while you build. This removes the ones nothing is using, and can then give the space back to Windows.
          ShipRight never touches your data — only images and build leftovers.
        </p>
        {windowsDiskBytes > 0 && (
          <p className={styles.fact}>
            Windows is holding <strong>{formatBytes(windowsDiskBytes)}</strong> for Docker and WSL.
            <HoverTip label="Why this can be bigger than what Docker uses">
              {'WSL keeps its files in one big file on your Windows drive. That file grows as you build, but it does not shrink by itself when Docker deletes things. Shrinking it is what gives the space back to Windows.'}
            </HoverTip>
          </p>
        )}
      </header>

      <div className={`${styles.card} ${styles.selection}`}>
        {preview?.error && <p className={styles.warn}>{preview.error}</p>}

        {preview && !preview.error && (
          <>
            <h3 className={styles.sub}>ShipRight images nothing is using</h3>
            {mine.length === 0
              ? <p className={styles.empty}>No unused images made by ShipRight.</p>
              : <div className={styles.list}>{mine.map(row)}</div>}

            {others.length > 0 && (
              <>
                <h3 className={styles.sub}>
                  Other images nothing is using
                  <HoverTip label="Why these are not ticked">
                    {'These images were not made by ShipRight (for example a database image you pulled once). They are listed in case you want them gone too, but are left unticked so nothing you may still want is removed by accident.'}
                  </HoverTip>
                </h3>
                <p className={styles.note}>Not made by ShipRight — tick any you want removed.</p>
                <div className={styles.list}>{others.map(row)}</div>
              </>
            )}

            <div className={styles.options}>
              <label className={styles.option}>
                <input type="checkbox" checked={pruneDangling} disabled={!!running} onChange={e => setPruneDangling(e.target.checked)} aria-label={`Leftover unnamed layers (${preview.danglingCount})`} />
                Leftover unnamed layers ({preview.danglingCount})
              </label>
              <label className={styles.option}>
                <input type="checkbox" checked={pruneBuildCache} disabled={!!running} onChange={e => setPruneBuildCache(e.target.checked)} aria-label="Build cache" />
                Build cache (makes the next build a little slower, once)
              </label>
            </div>
          </>
        )}
      </div>

      <div className={`${styles.card} ${styles.side} ${job ? styles.sideActive : ''}`}>
        {job ? (
          <DiskJobView job={job} nowMs={nowMs} busy={busy} onAbortBuilds={abortBuilds} stale={stale} onForceClean={forceClean} onLeaveStale={() => setStale(null)} onRetry={retry} onClose={close} />
        ) : (
          <>
            <h3 className={styles.sub}>Ready when you are</h3>
            <p className={styles.fact}>
              {picked.size} image{picked.size === 1 ? '' : 's'} selected · about {formatBytes(pickedBytes)}
              {preview && preview.inUseCount > 0 && <> · {preview.inUseCount} in use, left alone</>}
            </p>
            <div className={styles.actions}>
              <ZestButton onClick={() => start(requestFor(false))} disabled={cannotStart} zest={{ visualOptions: { variant: 'standard' } }}>
                Clean up now
              </ZestButton>
              <ZestButton onClick={() => setConfirmShrink(true)} disabled={cannotStart} zest={{ buttonStyle: 'outline' }}>
                Clean up and shrink my Windows disk
              </ZestButton>
            </div>

            {confirmShrink && (
              <div className={styles.confirm} role="alertdialog" aria-label="Confirm shrinking the disk">
                <p>
                  After cleaning, ShipRight stops WSL for a few minutes (anything running in it, including Docker containers, stops
                  and has to be started again) and shrinks its disk file. This can take {windowsDiskBytes > 0 ? 'several minutes' : 'a while'}.
                  Windows will ask permission — answer Yes. It will not start while a build is running.
                </p>
                <div className={styles.actions}>
                  <ZestButton onClick={() => start(requestFor(true))} zest={{ visualOptions: { variant: 'danger' } }}>Yes, clean up and shrink</ZestButton>
                  <ZestButton onClick={() => setConfirmShrink(false)} zest={{ buttonStyle: 'outline' }}>Cancel</ZestButton>
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </section>
  );
}
