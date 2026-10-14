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

export default function SignupPage() {
  const router = useRouter();
  const setUser = useAuthStore((s) => s.setUser);
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const mode = useServerMode();

  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [companyName, setCompanyName] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [googleAvailable, setGoogleAvailable] = useState(false);
  const [providersChecked, setProvidersChecked] = useState(false);

  useEffect(() => {
    if (mode !== 'cloud' || providersChecked) return;
    setProvidersChecked(true);
    authApi.providers().then((p) => setGoogleAvailable(p.google)).catch(() => setGoogleAvailable(false));
  }, [mode, providersChecked]);

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
    setFieldErrors({});
    setLoading(true);
    try {
      const session = await authApi.register(email, password, name, companyName);
      setUser(session.user);
      toast.success(`Welcome, ${session.user.name}!`);
      router.replace(session.user.mustChangePassword ? '/change-password' : '/projects');
    } catch (err) {
      const apiError = err as ApiError;
      if (apiError?.errors) setFieldErrors(apiError.errors);
      setError(apiError?.message ?? 'Signup failed');
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <Head><title>ShipRight — Sign Up</title></Head>
      <div className={styles.container}>
        <div className={styles.card}>
          <div className={styles.logo}>
            <h1>ShipRight</h1>
            <p>Create your account</p>
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
              <label className={styles.label} htmlFor="name">Name</label>
              <ZestTextbox
                id="name"
                type="text"
                placeholder="Your name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                required
                autoFocus
                zest={{ stretch: true }}
              />
              {fieldErrors.name && <div className={styles.fieldError} role="alert">{fieldErrors.name[0]}</div>}
            </div>

            <div className={styles.field}>
              <label className={styles.label} htmlFor="email">Email</label>
              <ZestTextbox
                id="email"
                type="email"
                placeholder="you@example.com"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                required
                zest={{ stretch: true }}
              />
              {fieldErrors.email && <div className={styles.fieldError} role="alert">{fieldErrors.email[0]}</div>}
            </div>

            <div className={styles.field}>
              <label className={styles.label} htmlFor="company">Company</label>
              <ZestTextbox
                id="company"
                type="text"
                placeholder="Your company name"
                value={companyName}
                onChange={(e) => setCompanyName(e.target.value)}
                required
                zest={{ stretch: true }}
              />
              {fieldErrors.companyName && <div className={styles.fieldError} role="alert">{fieldErrors.companyName[0]}</div>}
            </div>

            <div className={styles.field}>
              <label className={styles.label} htmlFor="password">Password</label>
              <ZestTextbox
                id="password"
                type="password"
                placeholder="At least 8 characters"
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
              {loading ? 'Creating account…' : 'Create Account'}
            </ZestButton>
          </form>

          <div className={styles.links}>
            <Link href="/login">Already have an account? Sign in</Link>
          </div>
        </div>
      </div>
    </>
  );
}