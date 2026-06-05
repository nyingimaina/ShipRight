import { useState } from 'react';
import { Drawer } from 'vaul';
import ReactSelect from 'react-select';
import { IBuildRecord, BuildStatus } from '@/shared/types/IBuildRecord';
import ResponsiveTable from 'jattac.libs.web.responsive-table';
import LogViewer, { LogEntry } from '@/modules/BuildWizard/LogViewer';
import styles from './Styles/HistoryTable.module.css';

interface Props {
  records: IBuildRecord[];
  loading: boolean;
  onPageChange?: (page: number) => void;
}

const STATUS_OPTIONS: { value: BuildStatus; label: string }[] = [
  { value: 'BuildSucceeded', label: 'Build Succeeded' },
  { value: 'Deployed',       label: 'Deployed' },
  { value: 'BuildFailed',    label: 'Build Failed' },
  { value: 'DeployFailed',   label: 'Deploy Failed' },
  { value: 'Aborted',        label: 'Aborted' },
  { value: 'Running',        label: 'Running' },
];

const selectStyles = {
  control: (b: object) => ({ ...b, background: '#1A2640', border: '1px solid rgba(255,255,255,0.08)', minHeight: 32 }),
  menu:    (b: object) => ({ ...b, background: '#1A2640' }),
  option:  (b: object, s: { isFocused: boolean }) => ({ ...b, background: s.isFocused ? '#1F2E4A' : 'transparent', color: '#F0F2F5' }),
  singleValue: (b: object) => ({ ...b, color: '#F0F2F5' }),
  multiValue:  (b: object) => ({ ...b, background: '#1F2E4A' }),
  multiValueLabel: (b: object) => ({ ...b, color: '#F0F2F5' }),
  input:   (b: object) => ({ ...b, color: '#F0F2F5' }),
};

function StatusBadge({ status }: { status: BuildStatus }) {
  const cls = styles[`status${status}`] ?? '';
  return <span className={`${styles.statusBadge} ${cls}`}>{status}</span>;
}

function duration(record: IBuildRecord): string {
  if (!record.completedAt) return '—';
  const ms = new Date(record.completedAt).getTime() - new Date(record.startedAt).getTime();
  const s = Math.round(ms / 1000);
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`;
}

export default function HistoryTable({ records, loading }: Props) {
  const [logBuild, setLogBuild] = useState<IBuildRecord | null>(null);

  const logLines: LogEntry[] = (logBuild?.logOutput ?? '')
    .split('\n')
    .filter(Boolean)
    .map((line, i) => {
      const src = line.startsWith('[docker]') ? 'docker'
        : line.startsWith('[git]') ? 'git'
        : line.startsWith('[ssh]') ? 'ssh'
        : 'shipright';
      return { id: i, source: src, line };
    });

  const cols = [
    {
      columnId: 'tag',
      displayLabel: 'Tag',
      cellRenderer: (r: IBuildRecord) => <span className={styles.mono}>{r.gitTag || '—'}</span>,
    },
    {
      columnId: 'project',
      displayLabel: 'Project',
      cellRenderer: (r: IBuildRecord) => r.projectName,
    },
    {
      columnId: 'status',
      displayLabel: 'Status',
      cellRenderer: (r: IBuildRecord) => <StatusBadge status={r.status} />,
    },
    {
      columnId: 'started',
      displayLabel: 'Started',
      cellRenderer: (r: IBuildRecord) => new Date(r.startedAt).toLocaleString(),
      getSortableValue: (r: IBuildRecord) => new Date(r.startedAt).getTime(),
    },
    {
      columnId: 'duration',
      displayLabel: 'Duration',
      cellRenderer: (r: IBuildRecord) => duration(r),
    },
    {
      columnId: 'actions',
      displayLabel: '',
      cellRenderer: (r: IBuildRecord) => (
        <button
          data-rt-ignore-row-click
          onClick={() => setLogBuild(r)}
          style={{ background: 'none', border: '1px solid rgba(255,255,255,0.08)', color: '#A8B8CC',
            padding: '3px 10px', borderRadius: 4, cursor: 'pointer', fontSize: 12 }}
        >
          View Log
        </button>
      ),
    },
  ];

  return (
    <>
      <ResponsiveTable
        columnDefinitions={cols}
        data={records}
        animationProps={{ isLoading: loading, animateOnLoad: true }}
        sortProps={{ initialSortColumn: 'started', initialSortDirection: 'desc' }}
        noDataComponent={<p style={{ color: '#637389', padding: 24 }}>No builds found.</p>}
      />

      {/* Log drawer */}
      <Drawer.Root open={!!logBuild} onOpenChange={open => { if (!open) setLogBuild(null); }}>
        <Drawer.Portal>
          <Drawer.Overlay className={styles.drawerOverlay} />
          <Drawer.Content className={styles.drawerContent}>
            <div className={styles.drawerHandle} />
            <Drawer.Title className={styles.drawerHeader}>
              Log — {logBuild?.gitTag || logBuild?.id?.slice(0, 8)}
              {' '}<StatusBadge status={logBuild?.status ?? 'Pending'} />
            </Drawer.Title>
            <div className={styles.drawerBody}>
              <LogViewer lines={logLines} />
            </div>
          </Drawer.Content>
        </Drawer.Portal>
      </Drawer.Root>
    </>
  );
}
