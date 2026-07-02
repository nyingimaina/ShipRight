import ZestTextbox from 'jattac.libs.web.zest-textbox';
import styles from './Styles/ProjectConfigForm.module.css';

export interface WatchBranchFields {
  watchBranch?: string;
  watchPollSeconds: number;
  watchSteps: string;
}

interface Props {
  fields: WatchBranchFields;
  onChange: (fields: WatchBranchFields) => void;
}

const POLL_OPTIONS = [
  { label: '5 minutes', value: 300 },
  { label: '15 minutes', value: 900 },
  { label: '30 minutes', value: 1800 },
];

const STEP_OPTIONS = [
  { label: 'Build only', value: 'Build' },
  { label: 'Build + Push to registry', value: 'BuildAndPush' },
  { label: 'Build + Push + Deploy', value: 'BuildPushAndDeploy' },
];

const selectStyle: React.CSSProperties = {
  background: '#131D30', color: '#F0F2F5',
  border: '1px solid rgba(255,255,255,0.12)',
  borderRadius: 6, padding: '6px 10px', width: '100%',
};

export default function WatchBranchSection({ fields, onChange }: Props) {
  const enabled = !!fields.watchBranch;

  const set = (patch: Partial<WatchBranchFields>) =>
    onChange({ ...fields, ...patch });

  return (
    <div style={{ marginTop: 24, borderTop: '1px solid rgba(255,255,255,0.08)', paddingTop: 20 }}>
      <div className={styles.formRow} style={{ alignItems: 'center', flexDirection: 'row', gap: 12 }}>
        <label className={styles.label} style={{ marginBottom: 0 }}>Watch branch for auto-build</label>
        <input
          type="checkbox"
          checked={enabled}
          onChange={e => set({ watchBranch: e.target.checked ? 'master' : undefined })}
          style={{ accentColor: '#C9A84C', width: 16, height: 16 }}
        />
      </div>

      {enabled && (
        <>
          <p style={{ margin: '4px 0 12px', fontSize: 12, color: 'var(--text-secondary)' }}>
            ShipRight polls the remote every N minutes. When the branch HEAD advances, a build is triggered automatically.
          </p>
          <div className={styles.formRow}>
            <label className={styles.label}>Branch name</label>
            <ZestTextbox
              value={fields.watchBranch ?? ''}
              onChange={e => set({ watchBranch: e.target.value })}
              placeholder="master"
              maxLength={100}
              zest={{ stretch: true }}
            />
          </div>
          <div className={styles.formRow}>
            <label className={styles.label}>Poll interval</label>
            <select
              value={fields.watchPollSeconds}
              onChange={e => set({ watchPollSeconds: Number(e.target.value) })}
              style={selectStyle}
            >
              {POLL_OPTIONS.map(o => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </select>
          </div>
          <div className={styles.formRow}>
            <label className={styles.label}>Steps to run</label>
            <select
              value={fields.watchSteps}
              onChange={e => set({ watchSteps: e.target.value })}
              style={selectStyle}
            >
              {STEP_OPTIONS.map(o => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </select>
          </div>
        </>
      )}
    </div>
  );
}
