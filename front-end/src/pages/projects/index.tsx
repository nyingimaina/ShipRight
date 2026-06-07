import Head from 'next/head';
import Link from 'next/link';
import { useEffect, useState } from 'react';
import { useRouter } from 'next/router';
import toast from 'react-hot-toast';
import { ZestResponsiveLayout } from 'jattac.libs.web.zest-responsive-layout';
import AppShell from '@/modules/AppShell/AppShell';
import OverflowMenu from 'jattac.libs.web.overflow-menu';
import ZestButton from 'jattac.libs.web.zest-button';
import ProjectSetupWizard from '@/modules/ProjectConfig/ProjectSetupWizard';
import { api } from '@/shared/ApiService';
import { IProject } from '@/shared/types/IProject';
import styles from './Styles/ProjectList.module.css';

// undefined = pane closed | null = new project | IProject = editing existing
type WizardTarget = IProject | null | undefined;

export default function ProjectList() {
  const router = useRouter();
  const [projects, setProjects] = useState<IProject[]>([]);
  const [loading, setLoading] = useState(true);
  const [wizardTarget, setWizardTarget] = useState<WizardTarget>(undefined);

  const load = () => api.get<IProject[]>('/api/projects')
    .then(setProjects).catch(() => toast.error('Failed to load projects'))
    .finally(() => setLoading(false));

  useEffect(() => { load(); }, []);

  // Auto-open pane when arriving from dashboard with ?new=true
  useEffect(() => {
    if (router.query.new === 'true') {
      setWizardTarget(null);
      router.replace('/projects/', undefined, { shallow: true });
    }
  }, [router.query.new]);

  const closePane = () => setWizardTarget(undefined);

  const handleDelete = async (id: string, name: string) => {
    try {
      await api.delete(`/api/projects/${id}`);
      toast.success(`'${name}' deleted.`);
      setProjects(prev => prev.filter(p => p.id !== id));
    } catch {
      toast.error('Failed to delete project.');
    }
  };

  const paneOpen = wizardTarget !== undefined;

  return (
    <>
      <Head><title>ShipRight — Projects</title></Head>
      <AppShell>
        <ZestResponsiveLayout
          sidePaneWidth="540px"
          closeOnDesktopOverlayClick
          sidePane={{
            visible: paneOpen,
            title: wizardTarget ? `Edit: ${wizardTarget.name}` : 'New Project',
            onClose: closePane,
            content: paneOpen ? (
              <ProjectSetupWizard
                existing={wizardTarget ?? undefined}
                onSaved={project => {
                  closePane();
                  toast.success(wizardTarget ? `'${project.name}' updated.` : `'${project.name}' created.`);
                  // Optimistically update the list immediately so the project appears without waiting
                  setProjects(prev => {
                    const idx = prev.findIndex(p => p.id === project.id);
                    return idx >= 0 ? prev.map(p => p.id === project.id ? project : p) : [...prev, project];
                  });
                  load(); // Still reload from server to confirm persistence
                }}
                onCancel={closePane}
              />
            ) : undefined,
          }}
        >
          <div className={styles.header}>
            <h1 className={styles.heading}>Projects</h1>
            <ZestButton onClick={() => setWizardTarget(null)}
              zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
              New Project
            </ZestButton>
          </div>

          <div className={styles.grid}>
            {loading && [0, 1, 2].map(i => (
              <div key={i} className={`${styles.card} ${styles.skeletonCard}`}>
                <div className={`skeleton ${styles.skeletonTitle}`} />
                <div className={styles.skeletonChips}>
                  <div className={`skeleton ${styles.skeletonChip}`} />
                  <div className={`skeleton ${styles.skeletonChip}`} />
                </div>
                <div className={`skeleton ${styles.skeletonLink}`} />
              </div>
            ))}
            {!loading && projects.map(p => (
              <div key={p.id} className={styles.card}>
                <div className={styles.cardTop}>
                  <div>
                    <h3 className={styles.cardTitle}>{p.name}</h3>
                    <div className={styles.chips}>
                      {p.services.map(s => <span key={s.name} className={styles.chip}>{s.name}</span>)}
                    </div>
                  </div>
                  <OverflowMenu items={[
                    { content: 'Edit', onClick: () => setWizardTarget(p) },
                    { content: 'Delete', onClick: () => handleDelete(p.id, p.name) },
                  ]} />
                </div>
                <Link href={`/projects/${p.id}/`} className={styles.cardLink}>View detail →</Link>
              </div>
            ))}
          </div>

          {!loading && projects.length === 0 && (
            <p className={styles.empty}>
              No projects yet.{' '}
              <button onClick={() => setWizardTarget(null)}
                style={{ background: 'none', border: 'none', color: '#C9A84C', cursor: 'pointer' }}>
                Create one
              </button>.
            </p>
          )}
        </ZestResponsiveLayout>
      </AppShell>
    </>
  );
}
