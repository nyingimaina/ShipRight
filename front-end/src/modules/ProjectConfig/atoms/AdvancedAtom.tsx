import ZestTextbox from 'jattac.libs.web.zest-textbox';
import styles from '../Styles/ProjectSetupWizard.module.css';

interface Props {
  gitPushTimeoutSeconds: number;
  onGitPushTimeoutChange: (value: number) => void;
}

export default function AdvancedAtom({ gitPushTimeoutSeconds, onGitPushTimeoutChange }: Props) {
  return (
    <div className={styles.section}>
      <span className={styles.sectionTitle}>Advanced</span>
      <div className={styles.fieldRow}>
        <label className={styles.fieldLabel}>Git push timeout (seconds)</label>
        <ZestTextbox
          value={String(gitPushTimeoutSeconds)}
          onChange={e => onGitPushTimeoutChange(Number(e.target.value) || 0)}
          placeholder="300"
          zest={{ stretch: true }} />
        <p style={{ margin: '3px 0 0', fontSize: 11, color: '#637389' }}>
          How long to wait for a git push before failing the build.
        </p>
      </div>
    </div>
  );
}
