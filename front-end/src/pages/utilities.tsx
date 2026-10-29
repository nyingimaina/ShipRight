import Head from 'next/head';
import AppShell from '@/modules/AppShell/AppShell';
import DiskCleanupPanel from '@/modules/Utilities/DiskCleanupPanel';
import SystemCheckPanel from '@/modules/Utilities/SystemCheckPanel';
import styles from './Styles/Utilities.module.css';

export default function UtilitiesPage() {
  return (
    <>
      <Head><title>ShipRight — Utilities</title></Head>
      <AppShell>
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
      </AppShell>
    </>
  );
}
