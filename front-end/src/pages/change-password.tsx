import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useState, FormEvent } from 'react';
import toast from 'react-hot-toast';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import ZestButton from 'jattac.libs.web.zest-button';
import { authApi, useAuthStore, type ApiError } from '@/shared/auth';
import { useServerMode } from '@/shared/serverInfo';
import styles from './Styles/Auth.module.css';

export default function ChangePasswordPage() {
  const router = useRouter();
  const setUser = useAuthStore((s) => s.setUser);
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const mode = useServerMode();

  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  if (mode === null) {
    return null;
  }

  if (mode === 'desktop' || !isAuthenticated) {
    router.replace(mode === 'desktop' ? '/projects' : '/login');
    return null;
  }

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setFieldErrors({});
    setLoading(true);
    try {
      const session = await authApi.changePassword(currentPassword, newPassword);
      setUser(session.user);
      toast.success('Password updated.');
      router.replace('/projects');
    } catch (err) {
      const apiError = err as ApiError;
      if (apiError?.errors) setFieldErrors(apiError.errors);
      setError(apiError?.message ?? 'Changing the password failed');
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <Head><title>ShipRight — Change Password</title></Head>
      <div className={styles.container}>
        <div className={styles.card}>
          <div className={styles.logo}>
            <h1>ShipRight</h1>
            <p>Choose a new password</p>
          </div>

          <form className={styles.form} onSubmit={handleSubmit} noValidate>
            {error && <div className={styles.error} role="alert">{error}</div>}

            <div className={styles.field}>
              <label className={styles.label} htmlFor="currentPassword">Current Password</label>
              <ZestTextbox
                id="currentPassword"
                type="password"
                placeholder="Your current password"
                value={currentPassword}
                onChange={(e) => setCurrentPassword(e.target.value)}
                required
                autoFocus
                zest={{ stretch: true }}
              />
              {fieldErrors.currentPassword && <div className={styles.fieldError} role="alert">{fieldErrors.currentPassword[0]}</div>}
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
              {loading ? 'Saving…' : 'Update Password'}
            </ZestButton>
          </form>

          <div className={styles.links}>
            <Link href="/projects">Back to projects</Link>
          </div>
        </div>
      </div>
    </>
  );
}