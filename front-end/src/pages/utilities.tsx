import Head from 'next/head';
import AppShell from '@/modules/AppShell/AppShell';
import DiskCleanupPanel from '@/modules/Utilities/DiskCleanupPanel';
import SystemCheckPanel from '@/modules/Utilities/SystemCheckPanel';
import { useServerMode } from '@/shared/serverInfo';
import styles from './Styles/Utilities.module.css';

export default function UtilitiesPage() {
  const mode = useServerMode();

  return (
    <>
      <Head><title>ShipRight - Utilities</title></Head>
      <AppShell>
        {mode === null ? (
          <div className={styles.page}>
            <h1 className={styles.heading}>Utilities</h1>
            <p className={styles.sub}>Checking this machine…</p>
          </div>
        ) : mode === 'cloud' ? (
          <div className={styles.page}>
            <h1 className={styles.heading}>Utilities</h1>
            <p className={styles.sub}>
              Disk cleanup and the Windows build-machine check run in the ShipRight desktop app.
              In cloud mode the servers do the building, so this page is not needed.
            </p>
          </div>
        ) : (
          <div className={styles.page}>
            <div>
              <h1 className={styles.heading}>Utilities</h1>
              <p className={styles.sub}>Keep this computer ready to build, and keep its disk from filling up.</p>
            </div>
            <SystemCheckPanel />
            <div id="disk-cleanup" className={styles.anchor}>
              <DiskCleanupPanel />
            </div>
          </div>
        )}
      </AppShell>
    </>
  );
}
