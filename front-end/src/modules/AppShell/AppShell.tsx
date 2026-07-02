import { useRouter } from 'next/router';
import SidekickMenu from 'jattac.libs.web.zest-sidekick-menu';
import { RiStackLine, RiTimeLine, RiTerminalBoxLine, RiDatabase2Line, RiServerLine, RiCalendarCheckLine, RiGitBranchLine, RiHeartPulseLine } from 'react-icons/ri';
import ThemeToggle from '@/shared/theme/ThemeToggle';
import styles from './Styles/AppShell.module.css';
import packageJson from '../../../package.json';

interface Props {
  children: React.ReactNode;
}

export default function AppShell({ children }: Props) {
  const router = useRouter();

  const menuItems = [
    { id: 'projects',  label: 'Projects',  icon: <RiStackLine size={18} />,       searchTerms: 'projects',            onClick: () => router.push('/projects') },
    { id: 'servers',   label: 'Servers',   icon: <RiServerLine size={18} />,      searchTerms: 'servers hosts ssh',   onClick: () => router.push('/servers') },
    { id: 'terminal',  label: 'Terminal',  icon: <RiTerminalBoxLine size={18} />, searchTerms: 'terminal ssh',        onClick: () => router.push('/terminal') },
    { id: 'databases', label: 'Databases', icon: <RiDatabase2Line size={18} />,   searchTerms: 'databases db sql',    onClick: () => router.push('/databases') },
    { id: 'history',   label: 'History',   icon: <RiTimeLine size={18} />,        searchTerms: 'history builds',      onClick: () => router.push('/history') },
    { id: 'backups',      label: 'Backups',      icon: <RiCalendarCheckLine size={18} />, searchTerms: 'backups scheduler',           onClick: () => router.push('/backups') },
    { id: 'watch-branch', label: 'Watch Branch', icon: <RiGitBranchLine size={18} />,     searchTerms: 'watch branch auto build ci', onClick: () => router.push('/watch-branch') },
    { id: 'monitoring',   label: 'Monitoring',   icon: <RiHeartPulseLine size={18} />,    searchTerms: 'monitoring health metrics cpu ram disk', onClick: () => router.push('/monitoring') },
  ];

  return (
    <>
      <SidekickMenu
        items={menuItems}
        searchEnabled={false}
        side="left"
        headerContent={
          <div className={styles.header}>
            <div className={styles.headerTitle}>ShipRight</div>
            <div className={styles.headerSub}>Build. Ship. Done.</div>
          </div>
        }
        footerContent={
          <div className={styles.footer}>
            <ThemeToggle />
            <div className={styles.footerVersion}>v{packageJson.version}</div>
          </div>
        }
      />
      <main className={styles.content}>{children}</main>
    </>
  );
}
