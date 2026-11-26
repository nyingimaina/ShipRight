import { useEffect, useState } from 'react';
import { RiCloseLine, RiDownloadCloud2Line } from 'react-icons/ri';
import { useInstallPrompt } from './useInstallPrompt';
import styles from './InstallBanner.module.css';

const DISMISS_KEY = 'shipright.install.dismissed.v1';

/** A calm, one-time invitation to install the app. It never covers content for long, and "Not now" is remembered. */
export default function InstallBanner() {
  const { canPrompt, installed, prompt } = useInstallPrompt();
  const [dismissed, setDismissed] = useState(true);

  useEffect(() => {
    try {
      setDismissed(window.localStorage.getItem(DISMISS_KEY) === '1');
    } catch {
      setDismissed(false);
    }
  }, []);

  if (!canPrompt || installed || dismissed) return null;

  const close = () => {
    setDismissed(true);
    try { window.localStorage.setItem(DISMISS_KEY, '1'); } catch { /* preference only */ }
  };

  return (
    <aside className={styles.banner} role="dialog" aria-label="Install ShipRight">
      <div className={styles.icon} aria-hidden="true"><RiDownloadCloud2Line size={18} /></div>
      <div className={styles.copy}>
        <span className={styles.title}>Install ShipRight</span>
        <span className={styles.text}>Full screen, one click away, and it keeps working when the connection blinks.</span>
      </div>
      <div className={styles.actions}>
        <button
          type="button"
          className={styles.install}
          onClick={() => { void prompt().then((accepted) => { if (accepted) close(); }); }}
        >
          Install
        </button>
        <button type="button" className={styles.later} onClick={close}>Not now</button>
      </div>
      <button type="button" className={styles.close} onClick={close} aria-label="Dismiss install invitation">
        <RiCloseLine size={16} />
      </button>
    </aside>
  );
}
