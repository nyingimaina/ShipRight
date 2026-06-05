import Head from 'next/head';
import Link from 'next/link';
import { useEffect, useState } from 'react';
import { useRouter } from 'next/router';
import AppShell from '@/modules/AppShell/AppShell';
import BuildWizard from '@/modules/BuildWizard/BuildWizard';
import ProjectCard, { ProjectSummary } from '@/modules/Dashboard/ProjectCard';
import { api } from '@/shared/ApiService';
import { IProject } from '@/shared/types/IProject';
import { IServiceVersion } from '@/shared/types/IBuildRecord';
import styles from './Styles/Dashboard.module.css';

export default function Dashboard() {
  const router = useRouter();
  const [projects, setProjects] = useState<IProject[]>([]);
  const [summaries, setSummaries] = useState<Record<string, ProjectSummary>>({});
  const [versions, setVersions] = useState<Record<string, IServiceVersion[]>>({});
  const [loading, setLoading] = useState(true);
  const [buildTarget, setBuildTarget] = useState<IProject | null>(null);

  const load = async () => {
    setLoading(true);
    try {
      const ps = await api.get<IProject[]>('/api/projects');
      setProjects(ps);
      const results = await Promise.allSettled(ps.map(p => Promise.all([
        api.get<ProjectSummary>(`/api/projects/${p.id}/summary`),
        api.get<IServiceVersion[]>(`/api/projects/${p.id}/current-versions`).catch(() => [] as IServiceVersion[]),
      ])));
      const summaryMap: Record<string, ProjectSummary> = {};
      const versionsMap: Record<string, IServiceVersion[]> = {};
      results.forEach((r, i) => {
        if (r.status === 'fulfilled') {
          summaryMap[ps[i].id] = r.value[0];
          versionsMap[ps[i].id] = r.value[1];
        }
      });
      setSummaries(summaryMap);
      setVersions(versionsMap);
    } catch {}
    finally { setLoading(false); }
  };

  useEffect(() => { load(); }, []);

  return (
    <>
      <Head><title>ShipRight</title></Head>
      <AppShell>
        <h1 className={styles.heading}>ShipRight</h1>
        <p className={styles.tagline}>Build. Ship. Done.</p>

        {loading && <p className={styles.loading}>Loading…</p>}

        {!loading && projects.length === 0 && (
          <div className={styles.emptyState}>
            <p className={styles.emptyMessage}>No projects configured yet.</p>
            <Link href="/projects/?new=true" className={styles.emptyAction}>Add your first project</Link>
          </div>
        )}

        <div className={styles.grid}>
          {projects.map(p => {
            const summary = summaries[p.id];
            if (!summary) return null;
            return (
              <ProjectCard
                key={p.id}
                summary={summary}
                onBuild={() => setBuildTarget(p)}
              />
            );
          })}
        </div>
      </AppShell>

      {buildTarget && (
        <BuildWizard
          projectId={buildTarget.id}
          projectName={buildTarget.name}
          currentVersions={versions[buildTarget.id] ?? []}
          isOpen
          onClose={() => setBuildTarget(null)}
        />
      )}
    </>
  );
}
