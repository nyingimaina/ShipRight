import ZestTextbox from 'jattac.libs.web.zest-textbox';
import styles from '../Styles/ProjectSetupWizard.module.css';

export interface WatchDraft {
  watchBranch: string;
  watchPollSeconds: number;
  watchSteps: string;
}

interface Props {
  draft: WatchDraft;
  onDraftChange: (patch: Partial<WatchDraft>) => void;
}

const STEPS = ['Build', 'BuildAndPush', 'BuildPushAndDeploy'];

export default function WatchAtom({ draft, onDraftChange }: Props) {
  return (
    <div className={styles.section}>
      <span className={styles.sectionTitle}>Watch branch (auto-build)</span>
      <div className={styles.fieldRow} style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
        <label className={styles.fieldLabel} style={{ marginBottom: 0 }}>Watch branch for auto-build</label>
        <input type="checkbox" checked={!!draft.watchBranch}
          onChange={e => onDraftChange({ watchBranch: e.target.checked ? 'master' : '' })}
          style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
      </div>
      {draft.watchBranch && (
        <>
          <div className={styles.fieldRow}>
            <label className={styles.fieldLabel}>Branch name</label>
            <ZestTextbox value={draft.watchBranch} onChange={e => onDraftChange({ watchBranch: e.target.value })}
              placeholder="master" zest={{ stretch: true }} />
          </div>
          <div className={styles.fieldRow}>
            <label className={styles.fieldLabel}>Poll interval</label>
            <select value={draft.watchPollSeconds}
              onChange={e => onDraftChange({ watchPollSeconds: Number(e.target.value) })}
              style={{ background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', width: '100%' }}>
              <option value={300}>Every 5 minutes</option>
              <option value={900}>Every 15 minutes</option>
              <option value={1800}>Every 30 minutes</option>
            </select>
          </div>
          <div className={styles.fieldRow}>
            <label className={styles.fieldLabel}>Steps</label>
            <select value={draft.watchSteps}
              onChange={e => onDraftChange({ watchSteps: e.target.value })}
              style={{ background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', width: '100%' }}>
              {STEPS.map(s => <option key={s} value={s}>{s}</option>)}
            </select>
          </div>
        </>
      )}
    </div>
  );
}
