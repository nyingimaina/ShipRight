import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import AppShell from '@/modules/AppShell/AppShell';
import BuildWizard from '@/modules/BuildWizard/BuildWizard';
import { ProjectSummary } from '@/modules/Dashboard/ProjectCard';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { IProject } from '@/shared/types/IProject';
import { IServiceVersion } from '@/shared/types/IBuildRecord';
import styles from './Styles/ProjectDetail.module.css';

export default function ProjectDetail() {
  const router = useRouter();
  const { id } = router.query as { id: string };
  const [project, setProject] = useState<IProject | null>(null);
  const [versions, setVersions] = useState<IServiceVersion[]>([]);
  const [summary, setSummary] = useState<ProjectSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [wizardBuildId, setWizardBuildId] = useState<string | undefined>(undefined);
  const [wizardOpen, setWizardOpen] = useState(false);

  useEffect(() => {
    if (!id) return;
    Promise.all([
      api.get<IProject>(`/api/projects/${id}`),
      api.get<IServiceVersion[]>(`/api/projects/${id}/current-versions`).catch(() => [] as IServiceVersion[]),
      api.get<ProjectSummary>(`/api/projects/${id}/summary`).catch(() => null),
    ])
      .then(([p, v, s]) => { setProject(p); setVersions(v); setSummary(s); })
      .catch(() => toast.error('Project not found.'))
      .finally(() => setLoading(false));
  }, [id]);

  if (loading) return <AppShell><p className={styles.loading}>Loading…</p></AppShell>;
  if (!project) return <AppShell><p className={styles.notFound}>Project not found.</p></AppShell>;

  const versionMap = Object.fromEntries(versions.map(v => [v.serviceName, v]));

  return (
    <>
      <Head><title>ShipRight — {project.name}</title></Head>
      <AppShell>
        <div className={styles.titleRow}>
          <h1 className={styles.heading}>{project.name}</h1>
          <ZestButton onClick={() => router.push(`/projects/${id}/edit`)}
            zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline', semanticType: 'edit' }}>
            Edit
          </ZestButton>
        </div>
        <p className={styles.breadcrumb}>
          <Link href="/projects/" className={styles.breadcrumbLink}>Projects</Link>
          {' / '}{project.name}
        </p>

        <section className={styles.section}>
          <h2 className={styles.sectionTitle}>Services</h2>
          <div className={styles.serviceList}>
            {project.services.map(s => {
              const v = versionMap[s.name];
              return (
                <div key={s.name} className={styles.serviceRow}>
                  <span className={styles.serviceName}>{s.name}</span>
                  {v?.version && <span className={styles.versionChip}>v{v.version}</span>}
                  {v?.error && <span className={styles.versionError} title={v.error}>version unreadable</span>}
                  <span className={styles.imageLabel}>{s.dockerImageName}</span>
                </div>
              );
            })}
          </div>
        </section>

        <section className={styles.section}>
          <h2 className={styles.sectionTitle}>Build & Deploy</h2>
          <div className={styles.buildActions}>
            <ZestButton zest={{ visualOptions: { variant: 'standard' } }}
              onClick={() => { setWizardBuildId(undefined); setWizardOpen(true); }}>
              Build
            </ZestButton>
            {summary?.lastBuild?.status === 'ImageBuilt' && (
              <ZestButton zest={{ visualOptions: { variant: 'standard' } }}
                onClick={() => { setWizardBuildId(summary.lastBuild!.id); setWizardOpen(true); }}>
                Push to Registry
              </ZestButton>
            )}
            {summary?.lastBuild?.status === 'PushFailed' && (
              <ZestButton zest={{ visualOptions: { variant: 'standard' } }}
                onClick={() => { setWizardBuildId(summary.lastBuild!.id); setWizardOpen(true); }}>
                Retry Push
              </ZestButton>
            )}
            {(summary?.lastBuild?.status === 'PushSucceeded' || summary?.lastBuild?.status === 'BuildSucceeded') && (
              <ZestButton zest={{ visualOptions: { variant: 'standard' } }}
                onClick={() => { setWizardBuildId(summary.lastBuild!.id); setWizardOpen(true); }}>
                Deploy to Production
              </ZestButton>
            )}
          </div>
        </section>
      </AppShell>

      <BuildWizard
        projectId={project.id}
        projectName={project.name}
        currentVersions={versions}
        initialBuildId={wizardBuildId}
        isOpen={wizardOpen}
        onClose={() => { setWizardOpen(false); setWizardBuildId(undefined); }}
      />
    </>
  );
}

export async function getStaticPaths() { return { paths: [], fallback: false }; }
export async function getStaticProps() { return { props: {} }; }
