import { useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { api } from '@/shared/ApiService';
import { IPathChange, IPathRefreshPlan, IPathRefreshResponse } from '@/shared/types/IPathRefresh';
import styles from '../Styles/ProjectSetupWizard.module.css';

interface Props {
  projectId: string;
  defaultRootPath: string;
  onApplied: (changes: IPathChange[]) => void;
}

const messageOf = (e: unknown) => (e as { message?: string })?.message ?? 'Something went wrong.';

export default function RefreshProjectAtom({ projectId, defaultRootPath, onApplied }: Props) {
  const [rootPath, setRootPath] = useState(defaultRootPath);
  const [plan, setPlan] = useState<IPathRefreshPlan | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const body = { rootPath };
  const url = (action: 'preview' | 'apply') => `/api/projects/${projectId}/refresh-paths/${action}`;

  const changeFolder = (value: string) => {
    setRootPath(value);
    setPlan(null);
    setError(null);
    setDone(false);
  };

  const check = async () => {
    setBusy(true);
    setError(null);
    setDone(false);
    try {
      const result = await api.post<IPathRefreshResponse>(url('preview'), body);
      setPlan(result.plan);
    } catch (e) {
      setPlan(null);
      setError(messageOf(e));
    } finally {
      setBusy(false);
    }
  };

  const apply = async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await api.post<IPathRefreshResponse>(url('apply'), body);
      onApplied(result.plan.changes);
      setPlan(null);
      setDone(true);
    } catch (e) {
      setError(messageOf(e));
    } finally {
      setBusy(false);
    }
  };

  const count = plan?.changes.length ?? 0;

  return (
    <div className={styles.section}>
      <span className={styles.sectionTitle}>Refresh paths from folder</span>
      <p style={{ margin: 0, fontSize: 12, color: '#637389' }}>
        Looks at the folder again and updates each service&apos;s version file and build context. Nothing else in the
        project is changed, and credentials are never touched.
      </p>
      <div className={styles.fieldRow}>
        <span className={styles.fieldLabel}>Project root folder</span>
        <ZestTextbox
          value={rootPath}
          onChange={e => changeFolder(e.target.value)}
          placeholder="Project root folder"
          zest={{ stretch: true, zSize: 'sm' }}
        />
      </div>
      <div>
        <ZestButton onClick={check} disabled={busy || !rootPath.trim()} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
          Check folder
        </ZestButton>
      </div>

      {error && <p className={styles.errorText}>{error}</p>}
      {done && <p style={{ margin: 0, fontSize: 13 }}>Paths updated and saved.</p>}

      {plan && count === 0 && <p style={{ margin: 0, fontSize: 13 }}>Everything is already up to date.</p>}

      {plan && count > 0 && (
        <>
          {plan.changes.map(c => (
            <div key={c.serviceName} className={styles.serviceCard}>
              <span className={styles.serviceName}>{c.serviceName}</span>
              {c.oldBuildContextPath !== c.newBuildContextPath && (
                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Build context</span>
                  <div className={styles.fieldValue}>{c.oldBuildContextPath}</div>
                  <div className={styles.fieldValue}>{c.newBuildContextPath}</div>
                </div>
              )}
              {c.oldVersionFilePath !== c.newVersionFilePath && (
                <div className={styles.fieldRow}>
                  <span className={styles.fieldLabel}>Version file</span>
                  <div className={styles.fieldValue}>{c.oldVersionFilePath}</div>
                  <div className={styles.fieldValue}>{c.newVersionFilePath}</div>
                </div>
              )}
            </div>
          ))}
          <div>
            <ZestButton onClick={apply} disabled={busy} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>
              {`Apply ${count} ${count === 1 ? 'change' : 'changes'}`}
            </ZestButton>
          </div>
        </>
      )}

      {plan && plan.notFoundInFolder.length > 0 && (
        <p style={{ margin: 0, fontSize: 12, color: '#637389' }}>
          Not found in this folder (left as they are): {plan.notFoundInFolder.join(', ')}
        </p>
      )}
      {plan && plan.newInFolder.length > 0 && (
        <p style={{ margin: 0, fontSize: 12, color: '#637389' }}>
          Found in the folder but not in this project (not added): {plan.newInFolder.join(', ')}
        </p>
      )}
    </div>
  );
}
