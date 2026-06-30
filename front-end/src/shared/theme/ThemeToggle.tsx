import { RiMoonLine, RiSunLine, RiComputerLine } from 'react-icons/ri';
import { useTheme, type ThemeChoice } from './ThemeContext';
import styles from './ThemeToggle.module.css';

const OPTIONS: { value: ThemeChoice; icon: React.ReactNode; label: string }[] = [
  { value: 'dark',   icon: <RiMoonLine size={14} />,     label: 'Dark'   },
  { value: 'system', icon: <RiComputerLine size={14} />, label: 'System' },
  { value: 'light',  icon: <RiSunLine size={14} />,      label: 'Light'  },
];

export default function ThemeToggle() {
  const { theme, setTheme } = useTheme();

  return (
    <div className={styles.toggle} role="group" aria-label="Color theme">
      {OPTIONS.map(opt => (
        <button
          key={opt.value}
          className={`${styles.option} ${theme === opt.value ? styles.active : ''}`}
          onClick={() => setTheme(opt.value)}
          aria-pressed={theme === opt.value}
          title={opt.label}
        >
          {opt.icon}
          <span className={styles.label}>{opt.label}</span>
        </button>
      ))}
    </div>
  );
}
