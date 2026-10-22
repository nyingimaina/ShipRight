import styles from './Spinner.module.css';

interface Props {
  label?: string;
  /** Extra reassurance for waits that can be long (SSH over a slow link). */
  hint?: string;
}

export default function Spinner({ label = 'Loading…', hint }: Props) {
  return (
    <div className={styles.wrap} role="status" aria-live="polite">
      <span className={styles.ring} aria-hidden="true" />
      <span className={styles.text}>
        <span>{label}</span>
        {hint && <span className={styles.hint}>{hint}</span>}
      </span>
    </div>
  );
}
