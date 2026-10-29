import { useCallback, useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';
import {
  ICleanupImage, ICleanupPreview, ICleanupResult, IWslDiskReport, IWslShrinkResult,
} from '@/shared/types/IDiskCleanup';
import styles from './DiskCleanupPanel.module.css';

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];

export function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B';
  let value = bytes;
  let i = 0;
  while (value >= 1024 && i < UNITS.length - 1) { value /= 1024; i++; }
  return `${Math.round(value * 10) / 10} ${UNITS[i]}`;
}

const errorOf = (e: unknown, fallback: string): { message: string; details: string[] } => {
  const x = e as { error?: string; message?: string; messages?: string[] };
  return { message: x?.error ?? x?.message ?? fallback, details: x?.messages ?? [] };
};

export default function DiskCleanupPanel() {
  const [preview, setPreview] = useState<ICleanupPreview | null>(null);
  const [disk, setDisk] = useState<IWslDiskReport | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [pruneDangling, setPruneDangling] = useState(true);
  const [pruneBuildCache, setPruneBuildCache] = useState(true);
  const [confirmShrink, setConfirmShrink] = useState(false);
  const [busy, setBusy] = useState<'clean' | 'shrink' | null>(null);
  const [cleaned, setCleaned] = useState<ICleanupResult | null>(null);
  const [shrink, setShrink] = useState<{ ok: boolean; headline: string; lines: string[] } | null>(null);

  const scan = useCallback(async () => {
    try {
      const p = await api.get<ICleanupPreview>('/api/system/docker-cleanup');
      setPreview(p);
      setPicked(new Set(p.images.filter(i => i.selectedByDefault).map(i => i.reference)));
      setPruneDangling(true);
    } catch {
      setPreview({ images: [], inUseCount: 0, danglingCount: 0, selectedBytes: 0, diskUsage: '', error: 'Could not scan Docker. Is ShipRight running on this computer?' });
    }
    try {
      setDisk(await api.get<IWslDiskReport>('/api/system/wsl-disk'));
    } catch {
      setDisk(null);
    }
  }, []);

  useEffect(() => { scan(); }, [scan]);

  const toggle = (ref: string) => setPicked(prev => {
    const next = new Set(prev);
    if (next.has(ref)) next.delete(ref); else next.add(ref);
    return next;
  });

  const runCleanup = async (): Promise<boolean> => {
    // Keep the on-screen order so the request is predictable.
    const images = (preview?.images ?? []).filter(i => picked.has(i.reference)).map(i => i.reference);
    try {
      const res = await api.post<ICleanupResult>('/api/system/docker-cleanup/run', { images, pruneDangling, pruneBuildCache });
      setCleaned(res);
      const removed = res.items.filter(i => i.removed).length;
      toast.success(removed > 0 ? `Removed ${removed} image${removed === 1 ? '' : 's'}.` : 'Clean-up finished.');
      return true;
    } catch (e) {
      toast.error(errorOf(e, 'Clean-up failed.').message);
      return false;
    }
  };

  const cleanNow = async () => {
    setBusy('clean');
    setShrink(null);
    await runCleanup();
    setBusy(null);
    scan();
  };

  const cleanAndShrink = async () => {
    setConfirmShrink(false);
    setBusy('shrink');
    setShrink(null);
    const ok = await runCleanup();
    if (ok) {
      try {
        const res = await api.post<IWslShrinkResult>('/api/system/wsl-disk/shrink', {});
        setShrink({
          ok: true,
          headline: 'Your Windows disk is smaller now',
          lines: res.files.map(f => `${f.path}: ${formatBytes(f.beforeBytes)} → ${formatBytes(f.afterBytes)}`),
        });
      } catch (e) {
        const { message, details } = errorOf(e, 'Shrinking the disk did not work.');
        setShrink({ ok: false, headline: message, lines: details });
      }
    }
    setBusy(null);
    scan();
  };

  const mine = (preview?.images ?? []).filter(i => i.owner === 'ShipRight');
  const others = (preview?.images ?? []).filter(i => i.owner !== 'ShipRight');
  const pickedBytes = (preview?.images ?? []).filter(i => picked.has(i.reference)).reduce((n, i) => n + i.sizeBytes, 0);
  const windowsDiskBytes = (disk?.vhdxFiles ?? []).reduce((n, f) => n + f.sizeBytes, 0);
  const cannotRun = busy !== null || !preview || !!preview.error;

  const row = (img: ICleanupImage) => (
    <label key={img.reference} className={styles.row}>
      <input type="checkbox" checked={picked.has(img.reference)} onChange={() => toggle(img.reference)} aria-label={img.reference} />
      <span className={styles.ref}>{img.reference}</span>
      <span className={styles.meta}>{img.age}</span>
      <span className={styles.size}>{img.size}</span>
    </label>
  );

  return (
    <section className={styles.panel}>
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
              <input type="checkbox" checked={pruneDangling} onChange={e => setPruneDangling(e.target.checked)} aria-label={`Leftover unnamed layers (${preview.danglingCount})`} />
              Leftover unnamed layers ({preview.danglingCount})
            </label>
            <label className={styles.option}>
              <input type="checkbox" checked={pruneBuildCache} onChange={e => setPruneBuildCache(e.target.checked)} aria-label="Build cache" />
              Build cache (makes the next build a little slower, once)
            </label>
          </div>

          <p className={styles.fact}>
            {picked.size} image{picked.size === 1 ? '' : 's'} selected · about {formatBytes(pickedBytes)}
            {preview.inUseCount > 0 && <> · {preview.inUseCount} in use, left alone</>}
          </p>
        </>
      )}

      <div className={styles.actions}>
        <ZestButton onClick={cleanNow} disabled={cannotRun} zest={{ visualOptions: { variant: 'standard' } }}>
          {busy === 'clean' ? 'Cleaning…' : 'Clean up now'}
        </ZestButton>
        <ZestButton onClick={() => setConfirmShrink(true)} disabled={cannotRun} zest={{ buttonStyle: 'outline' }}>
          {busy === 'shrink' ? 'Working…' : 'Clean up and shrink my Windows disk'}
        </ZestButton>
      </div>

      {confirmShrink && (
        <div className={styles.confirm} role="alertdialog" aria-label="Confirm shrinking the disk">
          <p>
            After cleaning, ShipRight stops WSL for a few minutes (anything running in it, including Docker containers, stops
            and has to be started again) and shrinks its disk file. Windows will ask permission — answer Yes.
            It will not start while a build is running.
          </p>
          <div className={styles.actions}>
            <ZestButton onClick={cleanAndShrink} zest={{ visualOptions: { variant: 'danger' } }}>Yes, clean up and shrink</ZestButton>
            <ZestButton onClick={() => setConfirmShrink(false)} zest={{ buttonStyle: 'outline' }}>Cancel</ZestButton>
          </div>
        </div>
      )}

      {cleaned && (
        <div className={styles.result}>
          <h3 className={styles.sub}>What happened</h3>
          {cleaned.items.map(i => (
            <p key={i.reference} className={i.removed ? styles.ok : styles.warn}>
              {i.removed ? 'Removed' : 'Kept'} {i.reference}{i.note ? ` — ${i.note}` : ''}
            </p>
          ))}
          {cleaned.danglingOutput && <p className={styles.ok}>Leftover layers: {cleaned.danglingOutput}</p>}
          {cleaned.buildCacheOutput && <p className={styles.ok}>Build cache: {cleaned.buildCacheOutput}</p>}
        </div>
      )}

      {shrink && (
        <div className={styles.result}>
          <p className={shrink.ok ? styles.ok : styles.warn}>{shrink.headline}</p>
          {shrink.lines.map((l, i) => <p key={i} className={styles.note}>{l}</p>)}
        </div>
      )}
    </section>
  );
}
