import SidekickMenu from 'jattac.libs.web.zest-sidekick-menu';
import { RiDashboardLine, RiStackLine, RiTimeLine } from 'react-icons/ri';
import styles from './Styles/AppShell.module.css';

interface Props {
  children: React.ReactNode;
}

const menuItems = [
  { id: 'dashboard', label: 'Dashboard', icon: <RiDashboardLine size={18} />, searchTerms: 'dashboard home',  path: '/' },
  { id: 'projects',  label: 'Projects',  icon: <RiStackLine size={18} />,     searchTerms: 'projects',        path: '/projects/' },
  { id: 'history',   label: 'History',   icon: <RiTimeLine size={18} />,      searchTerms: 'history builds',  path: '/history/' },
];

export default function AppShell({ children }: Props) {
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
      />
      <main className={styles.content}>{children}</main>
    </>
  );
}
