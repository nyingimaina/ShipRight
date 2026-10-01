'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { api, sseUrl } from '@/shared/ApiService';
import styles from './ProjectDetailPanel.module.css';

export interface IComposeRepoStatus {
  wslPath: string;
  windowsPath: string | null;
  exists: boolean;
  isGitRepo: boolean;
  cloneUrl: string | null;
  health: { isHealthy: boolean; summary: string };
  appliesTo: boolean;
}

interface Props {
  projectId: string;
}

/** Streams a background op (clone/delete) to the toast + console, then refreshes. */
function streamOp(opId: string, onDone: () => void) {
  const es = new EventSource(sseUrl(`/api/repos/ops/${opId}/stream`));
  es.onmessage = e => {
    try {
      const { type, data } = JSON.parse(e.data);
      if (type === 'log' && data?.message) console.log(data.message);
      if (type === 'Completed') { es.close(); toast.success(data?.message ?? 'Done.'); onDone(); }
      if (type === 'Error') { es.close(); toast.error(data?.message ?? 'Operation failed.'); onDone(); }
    } catch { /* ignore malformed frames */ }
  };
  es.onerror = () => { es.close(); toast.error('Lost connection to the operation stream.'); onDone(); };
  return es;
}

export default function ComposeRepoCard({ projectId }: Props) {
  const [status, setStatus] = useState<IComposeRepoStatus | null>(null);
  const [cloneUrl, setCloneUrl] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const streamRef = useRef<EventSource | null>(null);

  const refresh = useCallback(async () => {
    try {
      const res = await api.get<IComposeRepoStatus>(`/api/projects/${projectId}/compose-repo`);
      setStatus(res);
      setCloneUrl(res.cloneUrl ?? '');
      setError(null);
    } catch (e: any) {
      setError(e?.message ?? 'Could not read the compose repo status.');
    }
  }, [projectId]);

  useEffect(() => {
    refresh();
    return () => { streamRef.current?.close(); };
  }, [refresh]);

  const saveUrl = async () => {
    if (!cloneUrl.trim()) { toast.error('A clone URL is required.'); return; }
    setBusy(true);
    try {
      await api.post(`/api/projects/${projectId}/compose-repo/clone-url`, { cloneUrl: cloneUrl.trim() });
      toast.success('Compose repo clone URL saved.');
      await refresh();
    } catch (e: any) {
      toast.error(e?.message ?? 'Could not save the clone URL.');
    } finally {
      setBusy(false);
    }
  };

  const startOp = async (action: 'clone' | 'delete') => {
    if (action === 'delete' && !window.confirm(
      `Remove the compose repo at ${status?.wslPath}?\n\nIt is moved aside as <dir>.corrupt-<timestamp> rather than deleted, and the clone URL is kept so it can be restored.`)) return;

    setBusy(true);
    try {
      const res = await api.post<{ opId: string }>(`/api/projects/${projectId}/compose-repo/${action}`, {});
      streamRef.current = streamOp(res.opId, () => { setBusy(false); refresh(); });
    } catch (e: any) {
      setBusy(false);
      toast.error(e?.message ?? `Could not start the ${action}.`);
    }
  };

  if (error) {
    return (
      <section className={styles.section}>
        <h2 className={styles.sectionTitle}>Compose Repo</h2>
        <p className={styles.versionError}>{error}</p>
      </section>
    );
  }

  if (!status) {
    return (
      <section className={styles.section}>
        <h2 className={styles.sectionTitle}>Compose Repo</h2>
        <p className={styles.loading}>Checking compose repo…</p>
      </section>
    );
  }

  if (!status.appliesTo) {
    return (
      <section className={styles.section}>
        <h2 className={styles.sectionTitle}>Compose Repo</h2>
        <p className={styles.imageLabel}>EnvCompose deploy — no compose repo involved.</p>
      </section>
    );
  }

  const corrupt = status.isGitRepo && !status.health.isHealthy;

  return (
    <section className={styles.section}>
      <h2 className={styles.sectionTitle}>Compose Repo</h2>

      <div className={styles.serviceList}>
        <div className={styles.serviceRow}>
          <span className={styles.serviceName}>Working dir</span>
          <span className={styles.imageLabel}>{status.wslPath || '(not set)'}</span>
        </div>

        {status.windowsPath && (
          <div className={styles.serviceRow}>
            <span className={styles.serviceName}>Windows path</span>
            <span className={styles.imageLabel}>{status.windowsPath}</span>
          </div>
        )}

        <div className={styles.serviceRow}>
          <span className={styles.serviceName}>Status</span>
          {corrupt
            ? <span className={styles.versionError}>corrupt</span>
            : <span className={styles.versionChip}>{status.isGitRepo ? 'healthy' : 'not cloned'}</span>}
        </div>

        <div className={styles.serviceRow}>
          <span className={styles.serviceName}>git fsck</span>
          <span className={styles.imageLabel}>{status.health.summary}</span>
        </div>
      </div>

      <div className={styles.createVersionRow} style={{ marginTop: 12 }}>
        <ZestTextbox
          value={cloneUrl}
          onChange={(e: any) => setCloneUrl(e?.target?.value ?? '')}
          placeholder="https://org/DefaultCollection/compose-repo/_git/compose-repo"
        />
        <ZestButton
          zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
          onClick={saveUrl}
          disabled={busy}>
          Save URL
        </ZestButton>
      </div>

      <div className={styles.buildActions} style={{ marginTop: 10 }}>
        <ZestButton
          zest={{ visualOptions: { variant: 'standard' } }}
          onClick={() => startOp('clone')}
          disabled={busy}>
          Clone
        </ZestButton>
        <ZestButton
          zest={{ buttonStyle: 'outline', visualOptions: { variant: 'danger' } }}
          onClick={() => startOp('delete')}
          disabled={busy}>
          Delete Compose Repo
        </ZestButton>
        <ZestButton
          zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
          onClick={refresh}
          disabled={busy}>
          Re-check
        </ZestButton>
      </div>

      {!status.isGitRepo && (
        <p className={styles.imageLabel} style={{ marginTop: 10 }}>
          No compose repo at this path. Set the correct clone URL above, then press Clone.
        </p>
      )}
    </section>
  );
}
