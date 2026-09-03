import { useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import FilePicker from '@/modules/FilePicker/FilePicker';
import styles from '../Styles/ProjectSetupWizard.module.css';

interface Props {
  workingDir: string;
  errors: Record<string, string>;
  onWorkingDirChange: (value: string) => void;
}

export default function WslAtom({ workingDir, errors, onWorkingDirChange }: Props) {
  const [showPicker, setShowPicker] = useState(false);

  return (
    <div className={styles.section}>
      <span className={styles.sectionTitle}>Docker Compose directory <span style={{ color: '#C9A84C' }}>*</span></span>
      <p className={styles.stepSub}>Where is the docker-compose.yml for this project? (WSL path)</p>
      {showPicker
        ? <FilePicker dirsOnly storageKey="wsl-dir" onSelect={p => { onWorkingDirChange(uncToLinuxPath(p)); setShowPicker(false); }} />
        : <div style={{ display: 'flex', gap: 8 }}>
            <ZestTextbox value={workingDir} onChange={e => onWorkingDirChange(e.target.value)}
              placeholder="/home/nyingi/work/jattac/docker/..." zest={{ stretch: true }} />
            <ZestButton onClick={() => setShowPicker(true)} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Browse</ZestButton>
          </div>
      }
      {errors['wsl.workingDir'] && <p className={styles.errorText}>{errors['wsl.workingDir']}</p>}
    </div>
  );
}

function uncToLinuxPath(p: string): string {
  if (!p.startsWith('\\\\')) return p;
  const parts = p.split('\\').filter(Boolean);
  return parts.length >= 3 ? '/' + parts.slice(2).join('/') : p;
}
