import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useRouter } from 'next/router';
import { authApi } from './AuthApi';
import { useAuthStore } from './AuthStore';
import { useServerMode } from '../serverInfo';

export const PUBLIC_PATHS = ['/login', '/signup', '/forgot-password', '/reset-password'];
const CHANGE_PASSWORD_PATH = '/change-password';

interface Props {
  children: ReactNode;
}

export default function AuthGuard({ children }: Props) {
  const router = useRouter();
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const mustChangePassword = useAuthStore((s) => s.user?.mustChangePassword === true);
  const mode = useServerMode();
  const started = useRef(false);
  const [ready, setReady] = useState(false);

  // The server's identity cookie is the source of truth: ask once who is signed in, then let the
  // store and this guard react. Desktop mode needs no sign-in and no check.
  useEffect(() => {
    if (started.current) return;
    started.current = true;

    if (mode !== 'cloud') {
      setReady(true);
      return;
    }

    authApi.me()
      .then((session) => useAuthStore.getState().setUser(session.user))
      .catch(() => useAuthStore.getState().setUser(null))
      .finally(() => setReady(true));
  }, [mode]);

  const isPublic = PUBLIC_PATHS.includes(router.pathname);
  const isDesktop = mode === 'desktop';

  useEffect(() => {
    if (mode === null || mode === 'desktop' || !ready) return;
    if (isAuthenticated && mustChangePassword && router.pathname !== CHANGE_PASSWORD_PATH) {
      router.replace(CHANGE_PASSWORD_PATH);
      return;
    }
    if (isAuthenticated && isPublic) {
      router.replace(mustChangePassword ? CHANGE_PASSWORD_PATH : '/projects');
      return;
    }
    if (!isAuthenticated && !isPublic) {
      router.replace('/login');
    }
  }, [mode, ready, isAuthenticated, mustChangePassword, isPublic, router]);

  if (mode === null || (mode === 'cloud' && !ready)) return null;

  if (isDesktop) return <>{children}</>;
  if (isAuthenticated || isPublic) return <>{children}</>;
  return null;
}