import { useEffect, useRef, useState } from 'react';
import { motion, useReducedMotion } from 'framer-motion';
import { RiFileCopyLine, RiDownloadLine, RiLockLine, RiLockUnlockLine } from 'react-icons/ri';
import styles from './Styles/LogViewer.module.css';

interface LogEntry {
  id: number;
  source: string;
  line: string;
}

interface Props {
  lines: LogEntry[];
  isLive?: boolean;
}

export default function LogViewer({ lines, isLive }: Props) {
  const logRef = useRef<HTMLDivElement>(null);
  const [scrollLocked, setScrollLocked] = useState(false);
  const shouldReduce = useReducedMotion();

  useEffect(() => {
    if (!scrollLocked && logRef.current)
      logRef.current.scrollTop = logRef.current.scrollHeight;
  }, [lines, scrollLocked]);

  const copyAll = () => navigator.clipboard.writeText(lines.map(l => l.line).join('\n'));

  const download = () => {
    const blob = new Blob([lines.map(l => l.line).join('\n')], { type: 'text/plain' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'shipright-build.log';
    a.click();
  };

  const srcClass = (src: string) => {
    switch (src) {
      case 'git':       return styles.srcGit;
      case 'docker':    return styles.srcDocker;
      case 'ssh':       return styles.srcSsh;
      case 'shipright': return styles.srcShipright;
      default:          return styles.srcDefault;
    }
  };

  return (
    <div className={styles.wrap}>
      <div className={styles.toolbar}>
        <span className={styles.toolbarLeft}>
          {isLive ? '● streaming' : `${lines.length} lines`}
        </span>
        <div className={styles.toolbarRight}>
          <button className={`${styles.iconBtn} ${scrollLocked ? styles.scrollLocked : ''}`}
            onClick={() => setScrollLocked(v => !v)} title="Toggle scroll lock">
            {scrollLocked ? <RiLockLine /> : <RiLockUnlockLine />}
          </button>
          <button className={styles.iconBtn} onClick={copyAll} title="Copy to clipboard">
            <RiFileCopyLine />
          </button>
          <button className={styles.iconBtn} onClick={download} title="Download .log">
            <RiDownloadLine />
          </button>
        </div>
      </div>

      <div className={styles.log} ref={logRef}>
        {lines.length === 0
          ? <span className={styles.empty}>Waiting for output…</span>
          : lines.map(entry => (
              <motion.span
                key={entry.id}
                className={`${styles.line} ${srcClass(entry.source)}`}
                initial={shouldReduce ? false : { opacity: 0 }}
                animate={{ opacity: 1 }}
                transition={{ duration: 0.08 }}
              >
                {entry.line}
              </motion.span>
            ))
        }
      </div>
    </div>
  );
}

export type { LogEntry };
