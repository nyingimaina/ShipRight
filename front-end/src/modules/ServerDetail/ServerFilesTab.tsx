import { useCallback, useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import {
  RiFolderLine, RiFileLine, RiArrowUpLine, RiRefreshLine, RiUploadLine, RiDownloadLine,
  RiDeleteBinLine,
} from 'react-icons/ri';
import { api, sseUrl } from '@/shared/ApiService';
import { IServerConfig } from '@/shared/types/IProject';
import Spinner from '@/shared/components/Busy/Spinner';
import { useSingleFlight } from '@/shared/hooks/useSingleFlight';
import styles from './Styles/ServerFiles.module.css';

interface RemoteEntry { name: string; path: string; isDirectory: boolean; size: number; modified: string | null; }
interface RemoteListing { path: string; parent: string | null; entries: RemoteEntry[]; }

const enc = encodeURIComponent;
const SLOW_HINT = 'Slow connections can take a few seconds.';
/** How long a download link ignores repeat clicks (folders are compressed on the server first). */
const FILE_COOLDOWN_MS = 3000;
const FOLDER_COOLDOWN_MS = 20000;

function fmtSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`;
}

function crumbs(path: string): { label: string; path: string }[] {
  const parts = path.split('/').filter(Boolean);
  return [{ label: '/', path: '/' }, ...parts.map((p, i) => ({ label: p, path: '/' + parts.slice(0, i + 1).join('/') }))];
}

/**
 * Browse a server's files: open text files, download (folders as .tar.gz), upload, delete.
 * Every server round trip goes through one single-flight guard: a spinner says what is happening,
 * every control is disabled meanwhile, and repeat clicks are dropped instead of queued.
 */
export default function ServerFilesTab({ server }: { server: IServerConfig }) {
  const id = server.id!;
  const { run, busy } = useSingleFlight();
  const [activity, setActivity] = useState('Loading…');
  const [listing, setListing] = useState<RemoteListing | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showHidden, setShowHidden] = useState(false);
  const [viewing, setViewing] = useState<{ name: string; text?: string; error?: string } | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);
  const [preparing, setPreparing] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const cooldown = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => { if (cooldown.current) clearTimeout(cooldown.current); }, []);

  const listUrl = (path: string | undefined, hidden: boolean) =>
    `/api/servers/${id}/files?${path ? `path=${enc(path)}&` : ''}showHidden=${hidden}`;

  const load = useCallback((path?: string, hidden = showHidden) => run(async () => {
    setActivity(path ? `Loading ${path}…` : 'Loading…');
    setError(null);
    setConfirming(null);
    try { setListing(await api.get<RemoteListing>(listUrl(path, hidden))); }
    catch (e: unknown) { setError((e as { message?: string })?.message ?? 'Failed to list the folder.'); }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [id, showHidden, run]);

  useEffect(() => { load(undefined); }, [id]); // eslint-disable-line react-hooks/exhaustive-deps

  const open = (e: RemoteEntry) => run(async () => {
    setActivity(`Opening ${e.name}…`);
    setViewing({ name: e.name });
    try {
      const text = await api.getRaw(`/api/servers/${id}/files/read?path=${enc(e.path)}`);
      setViewing({ name: e.name, text });
    } catch (err: unknown) {
      setViewing({ name: e.name, error: (err as { message?: string })?.message ?? 'Could not open the file.' });
    }
  });

  const upload = (file: File | undefined) => {
    if (!file || !listing) return;
    const dir = listing.path;
    return run(async () => {
      setActivity(`Uploading ${file.name}…`);
      try {
        await api.upload(`/api/servers/${id}/files/upload?path=${enc(dir)}&name=${enc(file.name)}`, file);
        toast.success(`Uploaded ${file.name}.`);
        setListing(await api.get<RemoteListing>(listUrl(dir, showHidden)));
      } catch (err: unknown) {
        toast.error((err as { message?: string })?.message ?? 'Upload failed.');
      } finally {
        if (fileInput.current) fileInput.current.value = '';
      }
    });
  };

  const remove = (e: RemoteEntry) => run(async () => {
    setActivity(`Deleting ${e.name}…`);
    try {
      await api.delete(`/api/servers/${id}/files?path=${enc(e.path)}`);
      toast.success(`Deleted ${e.name}.`);
      setConfirming(null);
      setListing(await api.get<RemoteListing>(listUrl(listing?.path, showHidden)));
    } catch (err: unknown) {
      toast.error((err as { message?: string })?.message ?? 'Delete failed.');
      setConfirming(null);
    }
  });

  const toggleHidden = (v: boolean) => { setShowHidden(v); load(listing?.path, v); };

  /** Downloads are handled by the browser, so guard repeat clicks (each one would start another archive job). */
  const onDownloadClick = (e: RemoteEntry) => (ev: React.MouseEvent) => {
    if (preparing !== null) { ev.preventDefault(); return; }
    setPreparing(e.path);
    cooldown.current = setTimeout(() => setPreparing(null), e.isDirectory ? FOLDER_COOLDOWN_MS : FILE_COOLDOWN_MS);
  };

  return (
    <div className={styles.wrap}>
      <div className={styles.toolbar}>
        <button className={styles.btn} disabled={busy} onClick={() => load(server.remoteWorkingDir || undefined)}>Working dir</button>
        <button className={styles.btn} disabled={busy} onClick={() => load(`/home/${server.username}/.shipright`)}>Server logs</button>
        <button className={styles.btn} aria-label="Up" disabled={busy || !listing?.parent} onClick={() => load(listing!.parent!)}>
          <RiArrowUpLine /> Up
        </button>
        <button className={styles.btn} aria-label="Refresh" disabled={busy} onClick={() => load(listing?.path)}><RiRefreshLine /></button>
        <label className={styles.check}>
          <input type="checkbox" aria-label="Show hidden files" checked={showHidden} disabled={busy}
            onChange={e => toggleHidden(e.target.checked)} /> Hidden
        </label>
        <label className={`${styles.btn} ${styles.uploadBtn} ${busy || !listing ? styles.btnOff : ''}`}>
          <RiUploadLine /> Upload
          <input ref={fileInput} type="file" aria-label="Upload file" hidden disabled={busy || !listing}
            onChange={e => upload(e.target.files?.[0])} />
        </label>
      </div>

      {listing && (
        <nav className={styles.crumbs} aria-label="Path">
          {crumbs(listing.path).map((c, i, all) => (
            <span key={c.path}>
              <button className={styles.crumb} disabled={busy} onClick={() => load(c.path)}>{c.label}</button>
              {i < all.length - 1 && c.label !== '/' && <span className={styles.sep}>/</span>}
            </span>
          ))}
        </nav>
      )}

      {busy && <Spinner label={activity} hint={SLOW_HINT} />}
      {error && <p className={styles.error}>{error}</p>}

      <div className={styles.body}>
        <div className={`${styles.list} ${busy ? styles.dim : ''}`} aria-busy={busy}>
          {listing && listing.entries.length === 0 && !error && !busy && <p className={styles.empty}>This folder is empty.</p>}
          {listing?.entries.map(e => (
            <div key={e.path} className={styles.row}>
              <button className={styles.name} disabled={busy} onClick={() => (e.isDirectory ? load(e.path) : open(e))}
                aria-label={e.isDirectory ? undefined : `Open ${e.name}`}>
                {e.isDirectory ? <RiFolderLine /> : <RiFileLine />}
                <span>{e.name}</span>
              </button>
              <span className={styles.size}>{e.isDirectory ? '' : fmtSize(e.size)}</span>
              <span className={styles.actions}>
                {preparing === e.path && (
                  <span className={styles.preparing}>
                    {e.isDirectory ? 'Preparing download… (compressing on the server)' : 'Preparing download…'}
                  </span>
                )}
                <a className={`${styles.icon} ${preparing !== null && preparing !== e.path ? styles.iconOff : ''}`}
                  href={sseUrl(`/api/servers/${id}/files/download?path=${enc(e.path)}`)} download
                  onClick={onDownloadClick(e)}
                  aria-label={e.isDirectory ? `Download ${e.name} as .tar.gz` : `Download ${e.name}`}
                  title={e.isDirectory ? 'Download as .tar.gz' : 'Download'}>
                  <RiDownloadLine />
                </a>
                {confirming === e.path ? (
                  <>
                    <button className={styles.danger} aria-label="Confirm delete" disabled={busy} onClick={() => remove(e)}>Delete?</button>
                    <button className={styles.btn} aria-label="Cancel delete" disabled={busy} onClick={() => setConfirming(null)}>Cancel</button>
                  </>
                ) : (
                  <button className={styles.icon} aria-label={`Delete ${e.name}`} title="Delete" disabled={busy}
                    onClick={() => setConfirming(e.path)}>
                    <RiDeleteBinLine />
                  </button>
                )}
              </span>
            </div>
          ))}
        </div>

        {viewing && (
          <div className={styles.viewer}>
            <div className={styles.viewerHead}>
              <strong>{viewing.name}</strong>
              <button className={styles.btn} disabled={busy} onClick={() => setViewing(null)}>Close</button>
            </div>
            {viewing.error ? <p className={styles.error}>{viewing.error}</p>
              : viewing.text === undefined ? null
              : <pre className={styles.pre}>{viewing.text}</pre>}
          </div>
        )}
      </div>
    </div>
  );
}
