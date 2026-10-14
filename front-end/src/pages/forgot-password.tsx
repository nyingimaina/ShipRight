import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useState, FormEvent } from 'react';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import ZestButton from 'jattac.libs.web.zest-button';
import { authApi, type ApiError } from '@/shared/auth';
import { useServerMode } from '@/shared/serverInfo';
import styles from './Styles/Auth.module.css';

export default function ForgotPasswordPage() {
  const router = useRouter();
  const mode = useServerMode();
  const [email, setEmail] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [sent, setSent] = useState(false);

  if (mode === null) {
    return null;
  }

  if (mode === 'desktop') {
    router.replace('/projects');
    return null;
  }

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setFieldErrors({});
    setLoading(true);
    try {
      await authApi.forgotPassword(email);
      setSent(true);
    } catch (err) {
      const apiError = err as ApiError;
      if (apiError?.errors) setFieldErrors(apiError.errors);
      setError(apiError?.message ?? 'Request failed');
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <Head><title>ShipRight — Forgot Password</title></Head>
      <div className={styles.container}>
        <div className={styles.card}>
          <div className={styles.logo}>
            <h1>ShipRight</h1>
            <p>Reset your password</p>
          </div>

          {sent ? (
            <>
              <div className={styles.success}>
                If an account with that email exists, a reset link has been sent.
              </div>
              <div className={styles.links}>
                <Link href="/login">Back to sign in</Link>
              </div>
            </>
          ) : (
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

              <ZestButton
                type="submit"
                disabled={loading}
                zest={{ semanticType: 'submit', visualOptions: { variant: 'standard', size: 'md', stretch: true } }}
              >
                {loading && <span className={styles.spinner} />}
                {loading ? 'Sending…' : 'Send Reset Link'}
              </ZestButton>
            </form>
          )}

          {!sent && (
            <div className={styles.links}>
              <Link href="/login">Back to sign in</Link>
            </div>
          )}
        </div>
      </div>
    </>
  );
}