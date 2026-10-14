import Head from 'next/head';
import AppShell from '@/modules/AppShell/AppShell';
import TeamPage from '@/modules/Team/TeamPage';
import styles from './Styles/Team.module.css';

export default function TeamScreen() {
  return (
    <>
      <Head><title>ShipRight — Team</title></Head>
      <AppShell>
        <div className={styles.wrap}>
          <TeamPage />
        </div>
      </AppShell>
    </>
  );
}