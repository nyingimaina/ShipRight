import { useState } from 'react';
import { IProject } from '@/shared/types/IProject';
import ProjectSetupWizard from '../ProjectSetupWizard';
import QuickSetup from './QuickSetup';
import styles from './QuickSetup.module.css';

interface Props {
  existing?: IProject;
  onSaved: (project: IProject) => void;
  onCancel: () => void;
}

type Mode = 'smart' | 'legacy';

const STORAGE_KEY = 'shipright.newProjectMode';

function lastMode(): Mode | null {
  try {
    const v = window.localStorage.getItem(STORAGE_KEY);
    return v === 'smart' || v === 'legacy' ? v : null;
  } catch {
    return null;
  }
}

function remember(mode: Mode) {
  try { window.localStorage.setItem(STORAGE_KEY, mode); } catch { /* storage blocked: the choice just is not remembered */ }
}

/**
 * A new project starts by choosing how to create it: Smart Mode (the guided flow) or Legacy Mode (the original
 * wizard, every field by hand). Editing a project always uses the original wizard, so nothing changes for projects
 * that already exist - a project made in Smart Mode is an ordinary project underneath.
 */
export default function ProjectPane({ existing, onSaved, onCancel }: Props) {
  const [mode, setMode] = useState<Mode | null>(null);
  const last = existing ? null : lastMode();

  if (existing || mode === 'legacy') {
    return <ProjectSetupWizard existing={existing} onSaved={onSaved} onCancel={onCancel} />;
  }
  if (mode === 'smart') {
    return <QuickSetup onSaved={onSaved} onCancel={onCancel} onManual={() => { remember('legacy'); setMode('legacy'); }} />;
  }

  const choose = (m: Mode) => { remember(m); setMode(m); };

  return (
    <div className={styles.chooser}>
      <h2 className={styles.chooserTitle}>How would you like to set up this project?</h2>
      <div className={styles.chooserCards}>
        <button type="button" className={`${styles.chooserCard} ${styles.chooserRecommended}`} onClick={() => choose('smart')}>
          <span className={styles.chooserName}>Smart Mode</span>
          <span className={styles.chooserBadge}>Recommended</span>
          {last === 'smart' && <span className={styles.chooserBadge}>Last used</span>}
          <span className={styles.chooserText}>
            ShipRight sets it up for you. Pick your project folder and it works out what is in it, creates anything
            missing, and builds from a clean copy of your saved code. Works with .NET and Node projects.
          </span>
        </button>
        <button type="button" className={styles.chooserCard} onClick={() => choose('legacy')}>
          <span className={styles.chooserName}>Legacy Mode</span>
          {last === 'legacy' && <span className={styles.chooserBadge}>Last used</span>}
          <span className={styles.chooserText}>
            You fill in every setting yourself, step by step. Use this for other kinds of projects, an existing
            compose repository, or deploy options Smart Mode does not set up yet.
          </span>
        </button>
      </div>
      <button type="button" className={styles.chooserCancel} onClick={onCancel}>Cancel</button>
    </div>
  );
}
