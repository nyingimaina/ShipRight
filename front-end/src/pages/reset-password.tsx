import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useEffect, useState, FormEvent } from 'react';
import toast from 'react-hot-toast';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import ZestButton from 'jattac.libs.web.zest-button';
import { authApi, type ApiError } from '@/shared/auth';
import { useServerMode } from '@/shared/serverInfo';
import styles from './Styles/Auth.module.css';

export default function ResetPasswordPage() {
  const router = useRouter();
  const mode = useServerMode();
  const { token: tokenParam } = router.query;

  const [token, setToken] = useState((tokenParam as string) ?? '');
  const [newPassword, setNewPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [done, setDone] = useState(false);

  useEffect(() => {
    if (typeof tokenParam === 'string') setToken(tokenParam);
  }, [tokenParam]);

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
      await authApi.resetPassword(token, newPassword);
      setDone(true);
      toast.success('Password reset successfully');
    } catch (err) {
      const apiError = err as ApiError;
      if (apiError?.errors) setFieldErrors(apiError.errors);
      setError(apiError?.message ?? 'Reset failed');
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <Head><title>ShipRight — Reset Password</title></Head>
      <div className={styles.container}>
        <div className={styles.card}>
          <div className={styles.logo}>
            <h1>ShipRight</h1>
            <p>Choose a new password</p>
          </div>

          {done ? (
            <>
              <div className={styles.success}>
                Your password has been reset successfully.
              </div>
              <div className={styles.links}>
                <Link href="/login">Sign in with your new password</Link>
              </div>
            </>
          ) : (
            <form className={styles.form} onSubmit={handleSubmit} noValidate>
              {error && <div className={styles.error} role="alert">{error}</div>}

              <div className={styles.field}>
                <label className={styles.label} htmlFor="token">Reset Token</label>
                <ZestTextbox
                  id="token"
                  type="text"
                  placeholder="Paste your reset token here"
                  value={token}
                  onChange={(e) => setToken(e.target.value)}
                  required
                  autoFocus
                  zest={{ stretch: true }}
                />
                {fieldErrors.token && <div className={styles.fieldError} role="alert">{fieldErrors.token[0]}</div>}
              </div>

              <div className={styles.field}>
                <label className={styles.label} htmlFor="newPassword">New Password</label>
                <ZestTextbox
                  id="newPassword"
                  type="password"
                  placeholder="At least 8 characters"
                  value={newPassword}
                  onChange={(e) => setNewPassword(e.target.value)}
                  required
                  zest={{ stretch: true }}
                />
                {fieldErrors.newPassword && <div className={styles.fieldError} role="alert">{fieldErrors.newPassword[0]}</div>}
              </div>

              <ZestButton
                type="submit"
                disabled={loading}
                zest={{ semanticType: 'submit', visualOptions: { variant: 'standard', size: 'md', stretch: true } }}
              >
                {loading && <span className={styles.spinner} />}
                {loading ? 'Resetting…' : 'Reset Password'}
              </ZestButton>
            </form>
          )}
        </div>
      </div>
    </>
  );
}