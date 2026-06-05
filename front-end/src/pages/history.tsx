import Head from 'next/head';
import { useEffect, useState } from 'react';
import ReactSelect from 'react-select';
import AppShell from '@/modules/AppShell/AppShell';
import HistoryTable from '@/modules/History/HistoryTable';
import { api } from '@/shared/ApiService';
import { IBuildListResponse, BuildStatus } from '@/shared/types/IBuildRecord';
import styles from './Styles/History.module.css';

const PAGE_SIZE = 20;

const STATUS_OPTIONS = [
  { value: 'BuildSucceeded', label: 'Build Succeeded' },
  { value: 'Deployed',       label: 'Deployed' },
  { value: 'BuildFailed',    label: 'Build Failed' },
  { value: 'DeployFailed',   label: 'Deploy Failed' },
  { value: 'Aborted',        label: 'Aborted' },
  { value: 'Running',        label: 'Running' },
];

const selectStyles = {
  control:     (b: object) => ({ ...b, background: '#1A2640', border: '1px solid rgba(255,255,255,0.08)', minHeight: 36, minWidth: 200 }),
  menu:        (b: object) => ({ ...b, background: '#1A2640', zIndex: 20 }),
  option:      (b: object, s: { isFocused: boolean }) => ({ ...b, background: s.isFocused ? '#1F2E4A' : 'transparent', color: '#F0F2F5' }),
  multiValue:  (b: object) => ({ ...b, background: '#1F2E4A' }),
  multiValueLabel: (b: object) => ({ ...b, color: '#F0F2F5' }),
  placeholder: (b: object) => ({ ...b, color: '#637389' }),
  input:       (b: object) => ({ ...b, color: '#F0F2F5' }),
};

export default function HistoryPage() {
  const [data, setData] = useState<IBuildListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState<string[]>([]);

  useEffect(() => {
    setLoading(true);
    const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (statusFilter.length) params.set('status', statusFilter.join(','));
    api.get<IBuildListResponse>(`/api/builds?${params}`)
      .then(setData)
      .catch(() => {})
      .finally(() => setLoading(false));
  }, [page, statusFilter]);

  return (
    <>
      <Head><title>ShipRight — History</title></Head>
      <AppShell>
        <h1 className={styles.heading}>Build History</h1>

        <div className={styles.filters}>
          <div className={styles.filterGroup}>
            <span className={styles.filterLabel}>Status</span>
            <ReactSelect
              isMulti
              options={STATUS_OPTIONS}
              styles={selectStyles}
              placeholder="All statuses"
              onChange={opts => setStatusFilter((opts ?? []).map(o => o.value))}
            />
          </div>
        </div>

        <HistoryTable records={data?.items ?? []} loading={loading} />

        {data && data.totalPages > 1 && (
          <div className={styles.pagination}>
            <button className={styles.pageBtn} disabled={page <= 1} onClick={() => setPage(p => p - 1)}>← Prev</button>
            <span>Page {page} of {data.totalPages}</span>
            <button className={styles.pageBtn} disabled={page >= data.totalPages} onClick={() => setPage(p => p + 1)}>Next →</button>
          </div>
        )}
      </AppShell>
    </>
  );
}
