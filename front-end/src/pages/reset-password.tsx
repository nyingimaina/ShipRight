import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useState, FormEvent } from 'react';
import toast from 'react-hot-toast';
import { authApi } from '@/shared/auth';
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
  const [done, setDone] = useState(false);

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
    setLoading(true);
    try {
      await authApi.resetPassword(token, newPassword);
      setDone(true);
      toast.success('Password reset successfully');
    } catch (err: any) {
      setError(err?.message ?? 'Reset failed');
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
            <form className={styles.form} onSubmit={handleSubmit}>
              {error && <div className={styles.error}>{error}</div>}

              <div className={styles.field}>
                <label className={styles.label} htmlFor="token">Reset Token</label>
                <input
                  id="token"
                  className={styles.input}
                  type="text"
                  placeholder="Paste your reset token here"
                  value={token}
                  onChange={(e) => setToken(e.target.value)}
                  required
                  autoFocus
                />
              </div>

              <div className={styles.field}>
                <label className={styles.label} htmlFor="newPassword">New Password</label>
                <input
                  id="newPassword"
                  className={styles.input}
                  type="password"
                  placeholder="At least 8 characters"
                  value={newPassword}
                  onChange={(e) => setNewPassword(e.target.value)}
                  required
                  minLength={8}
                />
              </div>

              <button className={styles.submitBtn} type="submit" disabled={loading}>
                {loading && <span className={styles.spinner} />}
                {loading ? 'Resetting…' : 'Reset Password'}
              </button>
            </form>
          )}
        </div>
      </div>
    </>
  );
}
