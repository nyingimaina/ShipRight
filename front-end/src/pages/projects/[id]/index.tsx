import Head from 'next/head';
import Link from 'next/link';
import { useRouter } from 'next/router';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import AppShell from '@/modules/AppShell/AppShell';
import BuildWizard from '@/modules/BuildWizard/BuildWizard';
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
  const [loading, setLoading] = useState(true);
  const [wizardOpen, setWizardOpen] = useState(false);

  useEffect(() => {
    if (!id) return;
    Promise.all([
      api.get<IProject>(`/api/projects/${id}`),
      api.get<IServiceVersion[]>(`/api/projects/${id}/current-versions`).catch(() => [] as IServiceVersion[]),
    ])
      .then(([p, v]) => { setProject(p); setVersions(v); })
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
          <ZestButton zest={{ visualOptions: { variant: 'standard' } }}
            onClick={() => setWizardOpen(true)}>
            Build
          </ZestButton>
        </section>
      </AppShell>

      <BuildWizard
        projectId={project.id}
        projectName={project.name}
        currentVersions={versions}
        isOpen={wizardOpen}
        onClose={() => setWizardOpen(false)}
      />
    </>
  );
}

export async function getStaticPaths() { return { paths: [], fallback: false }; }
export async function getStaticProps() { return { props: {} }; }
