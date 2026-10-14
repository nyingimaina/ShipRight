import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useEffect, useState, FormEvent } from 'react';
import toast from 'react-hot-toast';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import ZestButton from 'jattac.libs.web.zest-button';
import { authApi, googleStartUrl, useAuthStore, type ApiError } from '@/shared/auth';
import { useServerMode } from '@/shared/serverInfo';
import styles from './Styles/Auth.module.css';

export default function LoginPage() {
  const router = useRouter();
  const setUser = useAuthStore((s) => s.setUser);
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const mustChangePassword = useAuthStore((s) => s.user?.mustChangePassword === true);
  const mode = useServerMode();

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [googleAvailable, setGoogleAvailable] = useState(false);
  const [providersChecked, setProvidersChecked] = useState(false);

  useEffect(() => {
    if (mode !== 'cloud' || providersChecked) return;
    setProvidersChecked(true);
    authApi.providers().then((p) => {
      if (typeof window !== 'undefined' && !p.google) {
        setError('Google sign-in is not set up on this server. Sign in with email instead.');
      }
      setGoogleAvailable(p.google);
    }).catch(() => setGoogleAvailable(false));
  }, [mode, providersChecked]);

  if (mode === null) {
    return null;
  }

  // Desktop mode never signs in; a signed-in person no longer belongs on this page. AuthGuard
  // bounces them to the same destination, so updating the store here is enough to move along.
  if (mode === 'desktop' || isAuthenticated) {
    router.replace(isAuthenticated ? (mustChangePassword ? '/change-password' : '/projects') : '/projects');
    return null;
  }

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setFieldErrors({});
    setLoading(true);
    try {
      const session = await authApi.login(email, password);
      setUser(session.user);
      toast.success(`Welcome back, ${session.user.name}!`);
    } catch (err) {
      const apiError = err as ApiError;
      if (apiError?.errors) setFieldErrors(apiError.errors);
      setError(apiError?.message ?? 'Login failed');
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <Head><title>ShipRight — Login</title></Head>
      <div className={styles.container}>
        <div className={styles.card}>
          <div className={styles.logo}>
            <h1>ShipRight</h1>
            <p>Sign in to your account</p>
          </div>

          {googleAvailable && (
            <>
              <a className={styles.googleButton} href={googleStartUrl()} data-testid="google-button">
                Continue with Google
              </a>
              <div className={styles.divider}><span>or</span></div>
            </>
          )}

          <form className={styles.form} onSubmit={handleSubmit} noValidate>
            {error && <div className={styles.error} role="alert">{error}</div>}

            <div className={styles.field}>
              <label className={styles.label} htmlFor="email">Email</label>
              <ZestTextbox
                id="email"
                type="email"
                placeholder="you@example.com"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                required
                autoFocus
                zest={{ stretch: true }}
              />
              {fieldErrors.email && <div className={styles.fieldError} role="alert">{fieldErrors.email[0]}</div>}
            </div>

            <div className={styles.field}>
              <label className={styles.label} htmlFor="password">Password</label>
              <ZestTextbox
                id="password"
                type="password"
                placeholder="••••••••"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
                zest={{ stretch: true }}
              />
              {fieldErrors.password && <div className={styles.fieldError} role="alert">{fieldErrors.password[0]}</div>}
            </div>

            <ZestButton
              type="submit"
              disabled={loading}
              zest={{ semanticType: 'submit', visualOptions: { variant: 'standard', size: 'md', stretch: true } }}
            >
              {loading && <span className={styles.spinner} />}
              {loading ? 'Signing in…' : 'Sign In'}
            </ZestButton>
          </form>

          <div className={styles.links}>
            <Link href="/signup">Create account</Link>
            <Link href="/forgot-password">Forgot password?</Link>
          </div>
        </div>
      </div>
    </>
  );
}