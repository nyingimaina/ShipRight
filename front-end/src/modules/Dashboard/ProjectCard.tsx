import Link from 'next/link';
import TimeAgo from 'timeago-react';
import ZestButton from 'jattac.libs.web.zest-button';
import styles from './Styles/ProjectCard.module.css';

interface CurrentVersion { serviceName: string; version: string | null; }
interface LastBuild { id: string; status: string; gitTag: string; startedAt: string; completedAt: string | null; }

export interface ProjectSummary {
  projectId: string;
  projectName: string;
  currentVersions: CurrentVersion[];
  lastBuild: LastBuild | null;
  lastDeployedAt: string | null;
  lastDeployedTag: string | null;
  buildSuccessRate: number;
  recentBuildCount: number;
}

interface Props {
  summary: ProjectSummary;
  onBuild: () => void;
}

function StatusBadge({ status }: { status: string }) {
  const cls = styles[`status${status}`] ?? '';
  return <span className={`${styles.statusBadge} ${cls}`}>{status}</span>;
}

export default function ProjectCard({ summary, onBuild }: Props) {
  return (
    <div className={styles.card}>
      <div className={styles.cardHeader}>
        <Link href={`/projects/${summary.projectId}/`} className={styles.projectName}>
          {summary.projectName}
        </Link>
        <div className={styles.buildBtn}>
          <ZestButton
            onClick={e => { e.preventDefault(); onBuild(); }}
            zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}>
            Build
          </ZestButton>
        </div>
      </div>

      {/* Version chips */}
      <div className={styles.chips}>
        {summary.currentVersions.map(v => (
          <span key={v.serviceName} className={`${styles.chip} ${v.version ? '' : styles.chipUnknown}`}>
            {v.serviceName} {v.version ? `v${v.version}` : '?'}
          </span>
        ))}
      </div>

      {/* Success rate bar */}
      {summary.recentBuildCount > 0 && (
        <div>
          <div className={styles.successBar}>
            <div className={styles.successFill} style={{ width: `${summary.buildSuccessRate}%` }} />
          </div>
        </div>
      )}

      {/* Meta */}
      <div className={styles.meta}>
        {summary.lastBuild && (
          <div className={styles.metaRow}>
            <span className={styles.metaLabel}>Last build</span>
            <StatusBadge status={summary.lastBuild.status} />
            {summary.lastBuild.gitTag && (
              <span className={styles.tagMono}>{summary.lastBuild.gitTag}</span>
            )}
            <TimeAgo datetime={summary.lastBuild.startedAt} />
          </div>
        )}
        <div className={styles.metaRow}>
          <span className={styles.metaLabel}>Last deploy</span>
          {summary.lastDeployedAt
            ? <><TimeAgo datetime={summary.lastDeployedAt} />
                {summary.lastDeployedTag && <span className={styles.tagMono}>{summary.lastDeployedTag}</span>}
              </>
            : <span style={{ color: '#637389' }}>Never deployed</span>
          }
        </div>
      </div>
    </div>
  );
}
