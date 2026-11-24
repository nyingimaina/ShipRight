import type { ReactNode } from 'react';
import { useRouter } from 'next/router';
import AppShell from '@/modules/AppShell/AppShell';
import AppHeader from './AppHeader';
import AppFooter from './AppFooter';
import { PUBLIC_PATHS } from '@/shared/auth/AuthGuard';
import { ShellContext } from './ShellContext';

/** The single shell around signed-in pages. Public sign-in screens get the bare page. */
export default function RootLayout({ children }: { children: ReactNode }) {
  const router = useRouter();
  if (PUBLIC_PATHS.includes(router.pathname)) return <>{children}</>;

  return (
    <ShellContext.Provider value>
      <AppShell>
        <AppHeader />
        {children}
        <AppFooter />
      </AppShell>
    </ShellContext.Provider>
  );
}
