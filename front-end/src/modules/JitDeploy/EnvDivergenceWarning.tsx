import { useCallback, useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import styles from './Styles/JitDeploy.module.css';
import { DivergenceCopy, EnvDivergenceResponse } from './envDivergence';

const NOBUSY = { busyOptions: { handleInternally: false } };

/**
 * Server-level settings have two copies: the authoritative one on the server record, and an older one embedded in this
 * project. Deploys now read the server record. When they drift, show both values and let the person reconcile them.
 */
export default function EnvDivergenceWarning({ base }: { base: string }) {
  const [value, setValue] = useState<EnvDivergenceResponse | null>(null);
  const [dismissed, setDismissed] = useState(false);

  const load = useCallback(async () => {
    try {
      const found = await api.get<EnvDivergenceResponse>(`${base}/env-divergence`);
      setValue(found.differs ? found : null);
    } catch {
      setValue(null); // an older server has no check: keep the old behaviour, no warning
    }
  }, [base]);

  useEffect(() => { void load(); }, [load]);

  const resolve = async (copy: DivergenceCopy) => {
    if (copy === 'none') {
      setDismissed(true);
      return;
    }
    try {
      await api.post(`${base}/env-divergence/resolve`, { copy });
      await load();
      toast.success(copy === 'to-project'
        ? 'Server settings copied into this project.'
        : 'Project settings copied onto the server record.');
    } catch (e) {
      toast.error((e as { message?: string })?.message ?? 'Could not copy those settings.');
    }
  };

  if (!value || dismissed) return null;

  return (
    <div className={styles.confirm} role="alert" aria-label="Server settings differ">
      <div>The server settings saved on the server are not the same as the copy inside this project.</div>
      <p className={styles.muted}>
        Deploys use the server record&apos;s values. This project holds a different copy. Refresh the project from the
        server to keep them in sync, or replace the server record with the project&apos;s values if those are the ones you
        want deployed. The value of each variable is shown below.
      </p>
      {value.variables.length > 0 && (
        <ul className={styles.list}>
          {value.variables.map(v => (
            <li key={v.name} className={styles.item}>
              <b>{v.name}</b>: server <code>{v.serverRecordValue || '(not set)'}</code>, project <code>{v.projectValue || '(not set)'}</code>
            </li>
          ))}
        </ul>
      )}
      {!value.serverFound && (
        <p className={styles.muted}>This project has no matching server record, so the server side cannot be changed here.</p>
      )}
      <div className={styles.confirmActions}>
        {value.serverFound && (
          <>
            <ZestButton onClick={() => resolve('to-project')} zest={{ ...NOBUSY, semanticType: 'approve' }}>Use server settings</ZestButton>
            <ZestButton onClick={() => resolve('to-server')} zest={{ ...NOBUSY, semanticType: 'edit' }}>Use project settings</ZestButton>
          </>
        )}
        <ZestButton onClick={() => resolve('none')} zest={{ ...NOBUSY, semanticType: 'cancel' }}>Keep both</ZestButton>
      </div>
    </div>
  );
}
