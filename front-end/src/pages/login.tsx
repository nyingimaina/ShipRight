import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useState, FormEvent } from 'react';
import toast from 'react-hot-toast';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import ZestButton from 'jattac.libs.web.zest-button';
import { useAuthStore, authApi } from '@/shared/auth';
import { setAccessToken } from '@/shared/ApiService';
import { useServerMode } from '@/shared/serverInfo';
import styles from './Styles/Auth.module.css';

export default function LoginPage() {
  const router = useRouter();
  const setAuth = useAuthStore((s) => s.setAuth);
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const mode = useServerMode();

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  if (mode === null) {
    return null;
  }

  if (mode === 'desktop' || isAuthenticated) {
    router.replace('/projects');
    return null;
  }

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      const res = await authApi.login(email, password);
      setAuth(res.accessToken, res.refreshToken, res.user);
      setAccessToken(res.accessToken);
      toast.success(`Welcome back, ${res.user.name}!`);
      router.replace('/projects');
    } catch (err: any) {
      setError(err?.message ?? 'Login failed');
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

          <form className={styles.form} onSubmit={handleSubmit}>
            {error && <div className={styles.error}>{error}</div>}

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
