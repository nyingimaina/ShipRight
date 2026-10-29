import Head from 'next/head';
import AppShell from '@/modules/AppShell/AppShell';
import DiskCleanupPanel from '@/modules/Utilities/DiskCleanupPanel';
import styles from './Styles/History.module.css';

export default function UtilitiesPage() {
  return (
    <>
      <Head><title>ShipRight — Utilities</title></Head>
      <AppShell>
        <h1 className={styles.heading}>Utilities</h1>
        <DiskCleanupPanel />
      </AppShell>
    </>
  );
}
