import { useEffect, useState, type ReactNode } from 'react';
import { useRouter } from 'next/router';
import { useAuthStore } from './AuthStore';
import { useServerMode } from '../serverInfo';

export const PUBLIC_PATHS = ['/login', '/signup', '/forgot-password', '/reset-password'];

interface Props {
  children: ReactNode;
}

export default function AuthGuard({ children }: Props) {
  const router = useRouter();
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const mode = useServerMode();
  const [hydrated, setHydrated] = useState(() => useAuthStore.persist?.hasHydrated() ?? false);

  useEffect(() => {
    if (hydrated) return;
    const persist = useAuthStore.persist;
    if (!persist) {
      setHydrated(true);
      return;
    }
    const unsub = persist.onFinishHydration(() => setHydrated(true));
    return unsub;
  }, [hydrated]);

  const isPublic = PUBLIC_PATHS.includes(router.pathname);
  const isDesktop = mode === 'desktop';

  useEffect(() => {
    if (hydrated && mode && !isDesktop && !isPublic && !isAuthenticated) {
      router.replace('/login');
    }
  }, [hydrated, mode, isDesktop, isPublic, isAuthenticated, router]);

  if (mode === null || !hydrated) return null;

  if (isDesktop || isPublic || isAuthenticated) return <>{children}</>;
  return null;
}
