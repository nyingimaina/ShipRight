import styles from './Switch.module.css';

interface Props {
  checked: boolean;
  onChange: (on: boolean) => void;
  label: string;
}

/** An Apple-style on/off switch. A checkbox with role="switch", so it is announced as "on" or "off". */
export default function Switch({ checked, onChange, label }: Props) {
  return (
    <label className={styles.row}>
      <input
        type="checkbox"
        role="switch"
        aria-checked={checked}
        className={styles.input}
        checked={checked}
        onChange={e => onChange(e.target.checked)}
      />
      <span className={styles.text}>{label}</span>
    </label>
  );
}
