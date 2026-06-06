import { useEffect, useState } from 'react';
import {
  RiFolderLine, RiFileLine, RiArrowLeftLine,
  RiHomeLine, RiComputerLine, RiFileTextLine, RiDownloadLine,
  RiHardDriveLine, RiTerminalLine,
} from 'react-icons/ri';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { IFsListing, IFsShortcuts, ISshConfig } from '@/shared/types/IFs';
import styles from './Styles/FilePicker.module.css';

interface Props {
  initialPath?: string;
  onSelect: (path: string) => void;
  dirsOnly?: boolean;
  label?: string;
  sshConfig?: ISshConfig;
}

const LOCAL_KEY = 'filePicker.lastLocalPath';
const remoteKey = (host: string) => `filePicker.lastRemotePath:${host}`;

const COMMON_ICONS: Record<string, React.ReactNode> = {
  Home:      <RiHomeLine />,
  Desktop:   <RiComputerLine />,
  Documents: <RiFileTextLine />,
  Downloads: <RiDownloadLine />,
};

function parseBreadcrumbs(path: string): { label: string; path: string }[] {
  if (!path) return [];

  if (path.startsWith('\\\\')) {
    // UNC: \\wsl.localhost\Ubuntu\home\...
    const parts = path.split('\\').filter(Boolean);
    return parts.map((part, i) => ({
      label: part,
      path: '\\\\' + parts.slice(0, i + 1).join('\\'),
    }));
  }

  if (/^[A-Za-z]:/.test(path)) {
    // Windows absolute: C:\Users\...
    const parts = path.split('\\').filter(Boolean);
    return parts.map((part, i) => ({
      label: part,
      path: i === 0 ? part + '\\' : parts.slice(0, i + 1).join('\\'),
    }));
  }

  // Unix (local or remote)
  const parts = path.split('/').filter(Boolean);
  return [
    { label: '/', path: '/' },
    ...parts.map((part, i) => ({
      label: part,
      path: '/' + parts.slice(0, i + 1).join('/'),
    })),
  ];
}

export default function FilePicker({ initialPath, onSelect, dirsOnly = false, label, sshConfig }: Props) {
  const [listing, setListing] = useState<IFsListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string>(initialPath ?? '');
  const [shortcuts, setShortcuts] = useState<IFsShortcuts | null>(null);

  const navigate = (path: string) => {
    setLoading(true);
    setError(null);

    const fetchListing = sshConfig
      ? api.get<IFsListing>(`/api/fs/remote/list?${new URLSearchParams({
          host: sshConfig.host, user: sshConfig.user, keyPath: sshConfig.keyPath, path,
        })}`)
      : api.get<IFsListing>(`/api/fs/list?${new URLSearchParams({ path })}`);

    fetchListing
      .then(l => {
        setListing(l);
        if (!selected) setSelected(l.path);
        if (sshConfig) localStorage.setItem(remoteKey(sshConfig.host), l.path);
        else           localStorage.setItem(LOCAL_KEY, l.path);
      })
      .catch(e => setError(e?.message ?? 'Failed to list directory'))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    const defaultPath = sshConfig
      ? (initialPath || localStorage.getItem(remoteKey(sshConfig.host)) || `/home/${sshConfig.user}`)
      : (initialPath ?? localStorage.getItem(LOCAL_KEY) ?? '');
    navigate(defaultPath);

    if (!sshConfig) {
      api.get<IFsShortcuts>('/api/fs/shortcuts').then(setShortcuts).catch(() => {});
    }
  }, []);

  const breadcrumbs = listing?.path ? parseBreadcrumbs(listing.path) : [];
  const entries = (listing?.entries ?? []).filter(e => dirsOnly ? e.isDirectory : true);
  const cur = listing?.path ?? '';

  const driveActive = (drivePath: string) => {
    const letter = drivePath.replace(/\\+$/, '').toUpperCase();
    return cur.toUpperCase().startsWith(letter);
  };

  const showSidebar = !sshConfig;

  return (
    <div>
      {label && <p style={{ fontSize: 12, color: '#A8B8CC', marginBottom: 6 }}>{label}</p>}
      <div className={styles.wrap}>
        <div className={styles.layout}>

          {/* Sidebar — local browsing only */}
          {showSidebar && (
            <div className={styles.sidebar}>
              {!!shortcuts?.commonFolders?.length && (
                <div className={styles.sidebarGroup}>
                  <div className={styles.sidebarLabel}>Quick Access</div>
                  {shortcuts.commonFolders.map(s => (
                    <button
                      key={s.path}
                      className={`${styles.sidebarItem} ${cur === s.path ? styles.sidebarItemActive : ''}`}
                      onClick={() => navigate(s.path)}
                      title={s.path}
                    >
                      <span className={styles.sidebarIcon}>{COMMON_ICONS[s.label] ?? <RiFolderLine />}</span>
                      <span className={styles.sidebarItemName}>{s.label}</span>
                    </button>
                  ))}
                </div>
              )}

              {!!shortcuts?.drives?.length && (
                <div className={styles.sidebarGroup}>
                  <div className={styles.sidebarLabel}>Drives</div>
                  {shortcuts.drives.map(s => (
                    <button
                      key={s.path}
                      className={`${styles.sidebarItem} ${driveActive(s.path) ? styles.sidebarItemActive : ''}`}
                      onClick={() => navigate(s.path)}
                      title={s.path}
                    >
                      <span className={styles.sidebarIcon}><RiHardDriveLine /></span>
                      <span className={styles.sidebarItemName}>{s.label}</span>
                    </button>
                  ))}
                </div>
              )}

              {!!shortcuts?.wsl?.length && (
                <div className={styles.sidebarGroup}>
                  <div className={styles.sidebarLabel}>WSL</div>
                  {shortcuts.wsl.map(s => (
                    <button
                      key={s.path}
                      className={`${styles.sidebarItem} ${cur.toLowerCase().startsWith(s.path.toLowerCase()) ? styles.sidebarItemActive : ''}`}
                      onClick={() => navigate(s.path)}
                      title={s.path}
                    >
                      <span className={styles.sidebarIcon}><RiTerminalLine /></span>
                      <span className={styles.sidebarItemName}>{s.label}</span>
                    </button>
                  ))}
                </div>
              )}
            </div>
          )}

          {/* Main panel */}
          <div className={styles.main}>
            {/* Breadcrumb */}
            <div className={styles.breadcrumb}>
              {breadcrumbs.map((crumb, i) => (
                <span key={crumb.path}>
                  {i > 0 && <span className={styles.crumbSep}>/</span>}
                  {i < breadcrumbs.length - 1
                    ? <button className={styles.crumbPart} onClick={() => navigate(crumb.path)}>{crumb.label}</button>
                    : <span className={styles.crumbCurrent}>{crumb.label}</span>
                  }
                </span>
              ))}
            </div>

            {/* Entries */}
            <div className={styles.list}>
              {loading && <div className={styles.loading}>Loading…</div>}
              {error && <div className={styles.error}>{error}</div>}

              {!loading && !error && listing?.parent && (
                <div className={`${styles.entry} ${styles.entryDir}`} onClick={() => navigate(listing.parent!)}>
                  <RiArrowLeftLine className={styles.entryIcon} />
                  <span className={styles.entryName}>..</span>
                </div>
              )}

              {!loading && !error && entries.length === 0 && (
                <div className={styles.empty}>Empty directory</div>
              )}

              {!loading && !error && entries.map(e => (
                <div
                  key={e.path}
                  className={`${styles.entry} ${e.isDirectory ? styles.entryDir : ''} ${!e.isDirectory && selected === e.path ? styles.entrySelected : ''}`}
                  onClick={() => {
                    if (e.isDirectory) navigate(e.path);
                    else setSelected(e.path);
                  }}
                >
                  {e.isDirectory
                    ? <RiFolderLine className={styles.entryIcon} />
                    : <RiFileLine className={styles.entryIcon} />
                  }
                  <span className={styles.entryName}>{e.name}</span>
                </div>
              ))}
            </div>

            {/* Footer */}
            <div className={styles.footer}>
              <span className={styles.selectedPath} title={dirsOnly ? listing?.path : (selected || listing?.path)}>
                {dirsOnly ? (listing?.path ?? '—') : (selected || listing?.path || '—')}
              </span>
              <ZestButton
                onClick={() => onSelect(dirsOnly ? (listing?.path ?? selected) : (selected || (listing?.path ?? '')))}
                zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}>
                Select
              </ZestButton>
            </div>
          </div>

        </div>
      </div>
    </div>
  );
}
