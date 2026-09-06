import ZestTextbox from 'jattac.libs.web.zest-textbox';
import styles from '../Styles/ProjectSetupWizard.module.css';
import { ProjectType } from '@/shared/types/IProject';

export interface IdentityState {
  name: string;
  timeZone: string;
  type: ProjectType;
  features: { docker: boolean; git: boolean; deploy: boolean; database: boolean };
}

interface Props {
  state: IdentityState;
  onChange: (patch: Partial<IdentityState>) => void;
  errors: Record<string, string>;
  showModeChoice: boolean;
}

const FEATURES: { key: keyof IdentityState['features']; label: string }[] = [
  { key: 'docker', label: 'Docker build & push — services, version files, build context' },
  { key: 'git', label: 'Git repositories — version control integration' },
  { key: 'deploy', label: 'Deploy server — SSH host, credentials, remote directory' },
  { key: 'database', label: 'Database — backup, restore, SQL queries' },
];

export default function ProjectIdentityAtom({ state, onChange, errors, showModeChoice }: Props) {
  const { type, features } = state;
  return (
    <>
      {showModeChoice && (
        <div style={{ display: 'flex', gap: 14, margin: '16px 0' }}>
          <button
            type="button"
            onClick={() => onChange({ type: 'Pipeline' })}
            style={{
              flex: 1, cursor: 'pointer', background: type === 'Pipeline'
                ? 'rgba(74,127,168,0.2)' : '#0D1625',
              border: type === 'Pipeline'
                ? '2px solid #4A7FA8' : '1px solid rgba(255,255,255,0.1)',
              borderRadius: 10, padding: '18px 16px', textAlign: 'left',
              transition: 'border-color .15s, background .15s',
            }}>
            <div style={{ fontSize: 15, fontWeight: 600, color: '#F0F2F5', marginBottom: 6 }}>Pipeline</div>
            <div style={{ fontSize: 12, color: '#637389', lineHeight: 1.5 }}>
              Full guided wizard — detect source code, configure services, set up your server and database.
            </div>
          </button>
          <button
            type="button"
            onClick={() => onChange({ type: 'Freeform' })}
            style={{
              flex: 1, cursor: 'pointer', background: type === 'Freeform'
                ? 'rgba(74,127,168,0.2)' : '#0D1625',
              border: type === 'Freeform'
                ? '2px solid #4A7FA8' : '1px solid rgba(255,255,255,0.1)',
              borderRadius: 10, padding: '18px 16px', textAlign: 'left',
              transition: 'border-color .15s, background .15s',
            }}>
            <div style={{ fontSize: 15, fontWeight: 600, color: '#F0F2F5', marginBottom: 6 }}>Freeform</div>
            <div style={{ fontSize: 12, color: '#637389', lineHeight: 1.5 }}>
              Pick only what you need — Docker build, Git repos, deployment server, database.
            </div>
          </button>
        </div>
      )}

      <div className={styles.section}>
        <span className={styles.sectionTitle}>Project</span>
        <div className={styles.fieldRow}>
          <label className={styles.fieldLabel}>Name <span style={{ color: '#C9A84C' }}>*</span></label>
          <ZestTextbox value={state.name} onChange={e => onChange({ name: e.target.value })}
            placeholder="e.g. SMS Gateway" zest={{ stretch: true }} />
          {errors['name'] && <p className={styles.errorText}>{errors['name']}</p>}
        </div>
        <div className={styles.fieldRow}>
          <label className={styles.fieldLabel}>Time Zone</label>
          <ZestTextbox value={state.timeZone} onChange={e => onChange({ timeZone: e.target.value })}
            placeholder="America/New_York" zest={{ stretch: true }} />
          <p style={{ margin: '4px 0 0', fontSize: 11, color: '#637389' }}>
            Defaults to this browser&apos;s timezone. Use an IANA timezone name.
          </p>
        </div>
      </div>

      {type === 'Freeform' && (
        <div style={{ background: '#0D1625', borderRadius: 8, padding: '14px 16px', marginBottom: 12 }}>
          <div style={{ fontSize: 13, fontWeight: 600, color: '#A8B8CC', marginBottom: 10 }}>Features</div>
          {FEATURES.map(f => (
            <label key={f.key} style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8, cursor: 'pointer', fontSize: 13, color: '#C9D6E3' }}>
              <input type="checkbox" checked={features[f.key]}
                onChange={e => onChange({ features: { ...features, [f.key]: e.target.checked } })}
                style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
              {f.label}
            </label>
          ))}
        </div>
      )}
    </>
  );
}
