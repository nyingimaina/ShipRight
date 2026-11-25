import { useCallback, useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api, apiUrl } from '@/shared/ApiService';
import { BackupFile, BackupListing, fmtBytes, fmtDate, fmtExact, fmtWhen, retentionLabel } from './backupTypes';
import styles from './BackupFiles.module.css';

interface Props {
  projectId: string;
  /** Defaults to the project's database API base. */
  apiBase?: string;
  /** Standalone server panels pass their own retention so the list shows the same numbers. */
  retainCount?: number;
  retainDays?: number;
  /** Extra query parameters the standalone server routes need (container, database, provider…). */
  queryParams?: Record<string, string | number | undefined>;
  /** Extra fields the standalone restore route needs (the database config). */
  extraRestoreBody?: Record<string, unknown>;
  /** Hide restore when the caller streams operations itself. */
  allowRestore?: boolean;
  onChanged?: () => void;
  onRestoreStarted?: (opId: string) => void;
}

function badge(text: string, tone: 'plain' | 'good' | 'accent' = 'plain') {
  return <span className={[styles.badge, styles[tone]].join(' ')}>{text}</span>;
}

export default function BackupFiles({
  projectId,
  apiBase,
  retainCount,
  retainDays,
  queryParams,
  extraRestoreBody,
  allowRestore = true,
  onChanged,
  onRestoreStarted,
}: Props) {
  const base = apiBase ?? `/api/projects/${projectId}/db`;
  const [listing, setListing] = useState<BackupListing | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const paramsKey = JSON.stringify(queryParams ?? {});

  const query = useCallback((extra: Record<string, string | number | undefined> = {}) => {
    const params = new URLSearchParams();
    const all: Record<string, string | number | undefined> = {
      ...(JSON.parse(paramsKey) as Record<string, string | number>),
      retainCount,
      retainDays,
      ...extra,
    };
    for (const [key, value] of Object.entries(all)) {
      if (value !== undefined && value !== '') params.set(key, String(value));
    }
    return params.toString();
  }, [paramsKey, retainCount, retainDays]);

  const url = useCallback((path: string, extra?: Record<string, string | number | undefined>) => {
    const search = query(extra);
    return `${base}/${path}${search ? `?${search}` : ''}`;
  }, [base, query]);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setListing(await api.get<BackupListing>(url('backups')));
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not load backups.');
    } finally {
      setLoading(false);
    }
  }, [url]);

  useEffect(() => { load(); }, [load]);

  const download = (file: BackupFile, format: 'plain' | 'zstd') => {
    const href = apiUrl(url('backups/download', { file: file.fileName, format }));
    const anchor = document.createElement('a');
    anchor.href = href;
    anchor.download = '';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  };

  const restore = async (file: BackupFile) => {
    const confirmed = window.confirm(
      `Restore ${file.databaseName} from ${file.fileName}?\n\n` +
      `This replaces the database with the contents of this backup (${fmtWhen(file.createdAt)}).`,
    );
    if (!confirmed) return;

    setBusy(file.fileName);
    try {
      const result = await api.post<{ opId: string }>(`${base}/restore`, {
        fileName: file.fileName,
        ...(extraRestoreBody ?? {}),
      });
      toast.success('Restore started.');
      onRestoreStarted?.(result.opId);
      onChanged?.();
      await load();
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not start the restore.');
    } finally {
      setBusy(null);
    }
  };

  const remove = async (file: BackupFile) => {
    if (!window.confirm(`Delete ${file.fileName}? This cannot be undone.`)) return;

    setBusy(file.fileName);
    try {
      await api.delete(url('backups', { file: file.fileName }));
      toast.success('Backup deleted.');
      onChanged?.();
      await load();
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not delete the backup.');
    } finally {
      setBusy(null);
    }
  };

  if (loading && !listing) return <p className={styles.muted}>Loading backups…</p>;
  if (!listing) return <p className={styles.muted}>Backups are unavailable right now.</p>;

  const totalStored = listing.files.reduce((sum, file) => sum + file.storedBytes, 0);
  const totalOriginal = listing.files.reduce((sum, file) => sum + file.originalBytes, 0);

  return (
    <div className={styles.wrap}>
      <div className={styles.summary}>
        <span>
          <strong>{listing.files.length}</strong> {listing.files.length === 1 ? 'backup' : 'backups'}
          {' · '}
          {fmtBytes(totalStored)} stored
          {totalOriginal > 0 && <> ({fmtBytes(totalOriginal)} raw)</>}
        </span>
        <span className={styles.summaryPolicy}>
          Keep last {listing.keepCount}
          {listing.keepDays > 0 ? `, max ${listing.keepDays}d` : ''}
        </span>
        <ZestButton onClick={load} zest={{ buttonStyle: 'text', semanticType: 'refresh', visualOptions: { size: 'sm' } }}>
          Refresh
        </ZestButton>
      </div>

      {listing.files.length === 0 ? (
        <p className={styles.empty}>No backups yet. Run one from the project or schedule it on the Backups page.</p>
      ) : (
        <ul className={styles.list}>
          {listing.files.map(file => (
            <li key={file.fileName} className={styles.row}>
              <div className={styles.nameLine}>
                <span className={styles.name} title={file.fileName}>{file.fileName}</span>
                <span className={styles.badges}>
                  {badge(file.source === 'schedule' ? 'scheduled' : 'manual')}
                  {file.compression === 'zstd' && badge(`zstd ${file.compressionLevel}`, 'good')}
                  {file.encryption !== 'none' && badge('encrypted', 'accent')}
                </span>
              </div>

              <div className={styles.metaLine}>
                <span title={fmtExact(file.createdAt)}>{fmtWhen(file.createdAt)}</span>
                <span className={styles.dot}>·</span>
                <span>{fmtBytes(file.storedBytes)} stored</span>
                <span className={styles.dot}>·</span>
                <span>{fmtBytes(file.originalBytes)} raw</span>
                {file.ratio > 1 && (
                  <>
                    <span className={styles.dot}>·</span>
                    <span>{file.ratio.toFixed(1)}×</span>
                  </>
                )}
                <span className={styles.dot}>·</span>
                <span title={`Projected rotation: ${fmtDate(file.projectedDeleteAt)}`}>
                  {retentionLabel(file, listing.keepCount, listing.keepDays)}
                </span>
                {file.durationMs > 0 && (
                  <>
                    <span className={styles.dot}>·</span>
                    <span>{(file.durationMs / 1000).toFixed(1)}s</span>
                  </>
                )}
              </div>

              <div className={styles.actions}>
                <button type="button" className={styles.link} onClick={() => download(file, 'plain')}>
                  Download
                </button>
                <button type="button" className={styles.link} onClick={() => download(file, 'zstd')}>
                  Download .zst
                </button>
                {allowRestore && (
                  <ZestButton
                    onClick={() => restore(file)}
                    disabled={busy === file.fileName}
                    zest={{ buttonStyle: 'outline', semanticType: 'submit', visualOptions: { size: 'sm' } }}
                  >
                    Restore
                  </ZestButton>
                )}
                <ZestButton
                  onClick={() => remove(file)}
                  disabled={busy === file.fileName}
                  zest={{ buttonStyle: 'text', semanticType: 'delete', visualOptions: { size: 'sm', variant: 'danger' } }}
                >
                  Delete
                </ZestButton>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
