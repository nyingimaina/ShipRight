import ZestButton from 'jattac.libs.web.zest-button';
import styles from './Styles/StepPicker.module.css';

export type BuildStep = 'build' | 'push' | 'deploy';

const STEP_LABELS: Record<BuildStep, string> = {
  build:  'Build Image',
  push:   'Push to Registry',
  deploy: 'Deploy to Production',
};

const ALL_STEPS: BuildStep[] = ['build', 'push', 'deploy'];

interface Props {
  steps: Set<BuildStep>;
  onChange: (steps: Set<BuildStep>) => void;
  onConfirm: () => void;
  onCancel: () => void;
}

export default function StepPicker({ steps, onChange, onConfirm, onCancel }: Props) {
  const allSelected = ALL_STEPS.every(s => steps.has(s));

  const toggle = (step: BuildStep) => {
    if (step === 'build') return; // build is always required
    const next = new Set(steps);
    next.has(step) ? next.delete(step) : next.add(step);
    onChange(next);
  };

  const toggleAll = () =>
    onChange(allSelected ? new Set<BuildStep>(['build']) : new Set<BuildStep>(ALL_STEPS));

  return (
    <>
      <div className={styles.title}>Choose steps to run</div>
      <div className={styles.list}>
        {ALL_STEPS.map(step => (
          <label key={step} className={styles.row}>
            <input
              type="checkbox"
              className={styles.checkbox}
              checked={steps.has(step)}
              disabled={step === 'build'}
              onChange={() => toggle(step)}
            />
            <span className={styles.label}>{STEP_LABELS[step]}</span>
          </label>
        ))}
      </div>
      <button className={styles.selectAll} onClick={toggleAll}>
        {allSelected ? 'Deselect All' : 'Select All'}
      </button>
      <div className={styles.actions}>
        <ZestButton
          onClick={onConfirm}
          zest={{ visualOptions: { variant: 'standard' }, semanticType: 'submit' }}
        >
          Confirm
        </ZestButton>
        <ZestButton
          onClick={onCancel}
          zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}
        >
          Cancel Picker
        </ZestButton>
      </div>
    </>
  );
}
