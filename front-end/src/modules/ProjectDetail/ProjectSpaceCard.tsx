import { useCallback, useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';
import { IProjectSpaceReport, ISpaceCleanResult } from '@/shared/types/IProjectSpace';
import { formatBytes } from '@/modules/Utilities/DiskJobView';
import styles from './ProjectSpaceCard.module.css';

interface Props {
  projectId: string;
}

export default function ProjectSpaceCard({ projectId }: Props) {
  const [report, setReport] = useState<IProjectSpaceReport | null>(null);
  const [failed, setFailed] = useState(false);
  const [cleaning, setCleaning] = useState(false);
  const [freed, setFreed] = useState<number | null>(null);
  const [savingPolicy, setSavingPolicy] = useState(false);

  const load = useCallback(async () => {
    setFailed(false);
    try {
      setReport(await api.get<IProjectSpaceReport>(`/api/projects/${projectId}/space`));
    } catch {
      setFailed(true);
    }
  }, [projectId]);

  useEffect(() => { load(); }, [load]);

  const clean = async () => {
    setCleaning(true);
    setFreed(null);
    try {
      const res = await api.post<ISpaceCleanResult>(`/api/projects/${projectId}/space/clean`, {});
      if (res.report) setReport(res.report);
      setFreed(res.freedBytes);
      toast.success(res.freedBytes > 0 ? `Freed ${formatBytes(res.freedBytes)}.` : 'Nothing needed removing.');
    } catch (e) {
      toast.error((e as { message?: string })?.message ?? 'The clean-up did not work.');
    } finally {
      setCleaning(false);
    }
  };

  const changePolicy = async (on: boolean) => {
    setSavingPolicy(true);
    try {
      await api.post(`/api/projects/${projectId}/local-cleanup-policy`, { deleteAfterPush: on });
      setReport(prev => (prev ? { ...prev, deletesAfterPush: on } : prev));
      toast.success(on
        ? 'Images will be deleted from this computer as soon as they are pushed.'
        : 'Images will be kept as before.');
    } catch (e) {
      toast.error((e as { message?: string })?.message ?? 'Could not save this setting.');
    } finally {
      setSavingPolicy(false);
    }
  };

  return (
    <section className={styles.card} aria-label="Space used by earlier builds">
      <div className={styles.head}>
        <h3 className={styles.title}>Space held by earlier builds</h3>
        <ZestButton onClick={load} disabled={cleaning} zest={{ buttonStyle: 'outline' }}>Refresh</ZestButton>
      </div>

      {failed && <p className={styles.warn}>ShipRight could not measure the space this project is using.</p>}
      {!report && !failed && <p className={styles.note}>Measuring…</p>}

      {report?.error && <p className={styles.warn}>{report.error}</p>}

      {report && !report.error && (
        <>
          <div className={styles.numbers}>
            <div className={styles.number}>
              <span className={styles.big}>{formatBytes(report.totalBytes)}</span>
              <span className={styles.label}>earlier builds are holding</span>
            </div>
            <div className={styles.number}>
              <span className={`${styles.big} ${styles.accent}`}>{formatBytes(report.reclaimableBytes)}</span>
              <span className={styles.label}>can be freed right now</span>
            </div>
          </div>

          <ul className={styles.breakdown}>
            <li><span>Images ({report.images.length})</span><span>{formatBytes(report.imagesBytes)}</span></li>
            <li><span>Leftover build copies ({report.snapshots.length})</span><span>{formatBytes(report.snapshotsBytes)}</span></li>
            <li>
              <span>Build cache — kept, it makes the next build faster</span>
              <span>{formatBytes(report.cacheBytes)}</span>
            </li>
          </ul>

          {(report.images.length > 0 || report.snapshots.length > 0) && (
            <details className={styles.details}>
              <summary>See what is using the space</summary>
              <ul className={styles.items}>
                {report.images.map(i => (
                  <li key={i.reference} className={styles.item}>
                    <span className={styles.ref}>{i.reference}</span>
                    <span className={`${styles.chip} ${i.origin === 'Failed build' ? styles.chipFailed : ''}`}>{i.origin}</span>
                    {i.inUse && <span className={styles.chip}>In use</span>}
                    <span className={styles.size}>{i.size}</span>
                  </li>
                ))}
                {report.snapshots.map(s => (
                  <li key={s.buildId} className={styles.item}>
                    <span className={styles.ref}>Build copy {s.buildId.slice(0, 8)}</span>
                    <span className={`${styles.chip} ${s.origin === 'Failed build' ? styles.chipFailed : ''}`}>{s.origin}</span>
                    {s.active && <span className={styles.chip}>Building now</span>}
                    <span className={styles.size}>{formatBytes(s.sizeBytes)}</span>
                  </li>
                ))}
              </ul>
            </details>
          )}

          <div className={styles.actions}>
            <ZestButton onClick={clean} disabled={cleaning || report.reclaimableBytes <= 0} zest={{ visualOptions: { variant: 'standard' } }}>
              {cleaning ? 'Freeing…' : report.reclaimableBytes > 0 ? `Free up ${formatBytes(report.reclaimableBytes)} now` : 'Nothing to free'}
            </ZestButton>
          </div>
          {freed !== null && <p className={styles.ok}>Freed {formatBytes(freed)}.</p>}

          <label className={styles.policy}>
            <input
              type="checkbox"
              aria-label="Delete images from this computer as soon as they are pushed"
              checked={report.deletesAfterPush}
              disabled={savingPolicy}
              onChange={e => changePolicy(e.target.checked)}
            />
            <span>
              Delete images from this computer as soon as they are pushed
              <HoverTip label="Delete images after pushing">
                {'Once an image is safely in your registry, the copy on this computer only takes up space. With this on, every image is removed right after its push succeeds — whether the build then goes on to succeed or fail. Images that are being used and images that were never pushed are always kept.'}
              </HoverTip>
            </span>
          </label>
        </>
      )}
    </section>
  );
}
