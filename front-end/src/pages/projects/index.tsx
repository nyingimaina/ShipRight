import Head from 'next/head';
import { useEffect, useState } from 'react';
import { useRouter } from 'next/router';
import toast from 'react-hot-toast';
import { ZestResponsiveLayout } from 'jattac.libs.web.zest-responsive-layout';
import AppShell from '@/modules/AppShell/AppShell';
import OverflowMenu from 'jattac.libs.web.overflow-menu';
import ZestButton from 'jattac.libs.web.zest-button';
import ProjectPane from '@/modules/ProjectConfig/QuickSetup/ProjectPane';
import AdoptLauncher from '@/modules/AdoptSetup/AdoptLauncher';
import ProjectDetailPanel from '@/modules/ProjectDetail/ProjectDetailPanel';
import ImportBundleDialog from '@/modules/ProjectTransfer/ImportBundleDialog';
import ExportProjectDialog from '@/modules/ProjectTransfer/ExportProjectDialog';
import { exportProject } from '@/modules/ProjectTransfer/exportProject';
import { api } from '@/shared/ApiService';
import { IProject } from '@/shared/types/IProject';
import styles from './Styles/ProjectList.module.css';

// undefined = pane closed | null = new project | IProject = editing existing
type WizardTarget = IProject | null | undefined;

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;

/** "Pipeline · 3 services · Prod": what kind of project it is, how big it is, and where it deploys. */
const summary = (p: IProject) =>
  [p.type === 'Freeform' ? 'Freeform' : 'Pipeline', plural(p.services.length, 'service'), p.server?.name || p.server?.host]
    .filter(Boolean).join(' · ');

const MAX_CHIPS = 4;

export default function ProjectList() {
  const router = useRouter();
  const [projects, setProjects] = useState<IProject[]>([]);
  const [loading, setLoading] = useState(true);
  const [wizardTarget, setWizardTarget] = useState<WizardTarget>(undefined);
  const [importOpen, setImportOpen] = useState(false);
  const [secretExport, setSecretExport] = useState<IProject | null>(null);

  const detailId = router.query.detail as string | undefined;

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
  const openDetail = (id: string) => router.push(`/projects/?detail=${id}`, undefined, { shallow: true });
  const closeDetail = () => router.push('/projects/', undefined, { shallow: true });

  if (detailId) return <ProjectDetailPanel projectId={detailId} onBack={closeDetail} />;

  const handleDelete = async (id: string, name: string) => {
    try {
      await api.delete(`/api/projects/${id}`);
      toast.success(`'${name}' deleted.`);
      setProjects(prev => prev.filter(p => p.id !== id));
    } catch {
      toast.error('Failed to delete project.');
    }
  };

  const handleExport = async (id: string, name: string) => {
    try {
      await exportProject(id);
      toast.success(`'${name}' exported. Secrets are not included.`);
    } catch (e) {
      toast.error((e as Error).message || 'Export failed.');
    }
  };

  const paneOpen = wizardTarget !== undefined;

  return (
    <>
      <Head><title>ShipRight — Projects</title></Head>
      <AppShell>
        <ZestResponsiveLayout
          sidePaneWidth="540px"
          mobileBreakpointPx={720}
          enableBounceAnimation
          closeOnDesktopOverlayClick
          sidePane={{
            visible: paneOpen,
            title: wizardTarget ? `Edit: ${wizardTarget.name}` : 'New Project',
            onClose: closePane,
            content: paneOpen ? (
              <ProjectPane
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
          <header className={styles.header}>
            <div>
              <h1 className={styles.heading}>Projects</h1>
              <p className={styles.subtitle}>
                {loading ? 'Loading your projects…' : projects.length === 0 ? 'Nothing here yet' : plural(projects.length, 'project')}
              </p>
            </div>
            <div className={styles.actions}>
              <AdoptLauncher onSaved={load} />
              <ZestButton onClick={() => setImportOpen(true)} zest={{ buttonStyle: 'outline', semanticType: 'import' }}>
                Import
              </ZestButton>
              <ZestButton onClick={() => setWizardTarget(null)} zest={{ semanticType: 'new' }}>
                New Project
              </ZestButton>
            </div>
          </header>

          <div className={styles.grid}>
            {loading && [0, 1, 2].map(i => (
              <div key={i} className={`${styles.card} ${styles.skeletonCard}`}>
                <div className={`skeleton ${styles.skeletonTitle}`} />
                <div className={`skeleton ${styles.skeletonMeta}`} />
                <div className={styles.skeletonChips}>
                  <div className={`skeleton ${styles.skeletonChip}`} />
                  <div className={`skeleton ${styles.skeletonChip}`} />
                </div>
              </div>
            ))}
            {!loading && projects.map(p => (
              <article key={p.id} className={styles.card} onClick={() => openDetail(p.id)}>
                <div className={styles.cardTop}>
                  <h3 className={styles.cardTitle}>
                    <a
                      className={styles.titleLink}
                      href={`/projects/?detail=${p.id}`}
                      onClick={e => { e.preventDefault(); e.stopPropagation(); openDetail(p.id); }}
                    >{p.name}</a>
                  </h3>
                  <div className={styles.menu} onClick={e => e.stopPropagation()}>
                    <OverflowMenu items={[
                      { content: 'Edit', onClick: () => setWizardTarget(p) },
                      { content: 'Export', onClick: () => handleExport(p.id, p.name) },
                      { content: 'Export with secrets…', onClick: () => setSecretExport(p) },
                      { content: 'Delete', onClick: () => handleDelete(p.id, p.name) },
                    ]} />
                  </div>
                </div>
                <p className={styles.meta}>{summary(p)}</p>
                <div className={styles.chips}>
                  {p.services.slice(0, MAX_CHIPS).map(s => <span key={s.name} className={styles.chip}>{s.name}</span>)}
                  {p.services.length > MAX_CHIPS && <span className={styles.chipMore}>+{p.services.length - MAX_CHIPS}</span>}
                </div>
                <span className={styles.chevron} aria-hidden="true">›</span>
              </article>
            ))}
          </div>

          {!loading && projects.length === 0 && (
            <section className={styles.empty}>
              <h2 className={styles.emptyTitle}>No projects yet</h2>
              <p className={styles.emptyText}>
                A project tells ShipRight how to build and ship your app. Create one to get started, or bring in one you exported earlier.
              </p>
              <div className={styles.emptyActions}>
                <ZestButton onClick={() => setWizardTarget(null)} zest={{ semanticType: 'new' }}>New project</ZestButton>
                <ZestButton onClick={() => setImportOpen(true)} zest={{ buttonStyle: 'outline', semanticType: 'import' }}>Import</ZestButton>
              </div>
            </section>
          )}
        </ZestResponsiveLayout>
        {importOpen && (
          <ImportBundleDialog
            onClose={() => setImportOpen(false)}
            onImported={() => load()}
          />
        )}
        {secretExport && (
          <ExportProjectDialog
            projectId={secretExport.id!}
            projectName={secretExport.name}
            onClose={() => setSecretExport(null)}
            onExported={() => toast.success(`'${secretExport.name}' exported with secrets. Keep the file and passphrase safe.`)}
          />
        )}
      </AppShell>
    </>
  );
}
