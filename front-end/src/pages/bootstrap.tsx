import Head from 'next/head';
import AppShell from '@/modules/AppShell/AppShell';
import BootstrapDeployment from '@/modules/BootstrapDeployment/BootstrapDeployment';
import { useServerMode } from '@/shared/serverInfo';

export default function BootstrapPage() {
  const mode = useServerMode();

  return (
    <>
      <Head><title>ShipRight - Bootstrap a deployment</title></Head>
      <AppShell>
        {mode === null ? (
          <p>Checking this machine…</p>
        ) : (
          <BootstrapDeployment cloud={mode === 'cloud'} />
        )}
      </AppShell>
    </>
  );
}
