import Head from 'next/head';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import { ZestResponsiveLayout } from 'jattac.libs.web.zest-responsive-layout';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import OverflowMenu from 'jattac.libs.web.overflow-menu';
import AppShell from '@/modules/AppShell/AppShell';
import PipelineBuilder from '@/modules/BuildWizard/PipelineBuilder';
import BuildWizard from '@/modules/BuildWizard/BuildWizard';
import { api } from '@/shared/ApiService';
import type { IPipelineResource, IPipelineStep, IProject, IServiceVersion } from '@/shared/types/IProject';
import styles from './Styles/Pipelines.module.css';

const STEP_ICONS: Record<string, string> = {
  Script: '📜',
  Build: '🐳',
  Push: '📤',
  Deploy: '🚀',
};

export default function PipelinesPage() {
  const [pipelines, setPipelines] = useState<IPipelineResource[]>([]);
  const [loading, setLoading] = useState(true);
  const [paneTarget, setPaneTarget] = useState<'new' | 'edit' | undefined>(undefined);
  const [editPipeline, setEditPipeline] = useState<IPipelineResource | null>(null);
  // Build state
  const [buildWizardOpen, setBuildWizardOpen] = useState(false);
  const [buildProject, setBuildProject] = useState<IProject | null>(null);
  const [buildPipeline, setBuildPipeline] = useState<IPipelineResource | null>(null);
  const [buildVersions, setBuildVersions] = useState<IServiceVersion[]>([]);
  // Project picker for global pipelines
  const [showProjectPicker, setShowProjectPicker] = useState(false);
  const [pickerPipeline, setPickerPipeline] = useState<IPipelineResource | null>(null);
  const [projects, setProjects] = useState<IProject[]>([]);
  const [projectSearch, setProjectSearch] = useState('');
  const [loadingProjects, setLoadingProjects] = useState(false);
  // Project filter for pipeline list
  const [filterProjectId, setFilterProjectId] = useState<string>('');
  const [allProjects, setAllProjects] = useState<IProject[]>([]);

  const load = async () => {
    setLoading(true);
    try {
      const [data, projs] = await Promise.all([
        api.get<IPipelineResource[]>('/api/resources/pipelines'),
        api.get<IProject[]>('/api/projects').catch(() => []),
      ]);
      setPipelines(data);
      setAllProjects(projs);
    } catch {
      toast.error('Failed to load pipelines.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []);

  const openNew = () => { setPaneTarget('new'); setEditPipeline(null); };
  const openEdit = (p: IPipelineResource) => { setPaneTarget('edit'); setEditPipeline(p); };
  const closePane = () => { setPaneTarget(undefined); setEditPipeline(null); };

  const handleSave = (pipeline: IPipelineResource) => {
    closePane();
    load();
  };

  const handleDelete = async (p: IPipelineResource) => {
    try {
      await api.delete(`/api/resources/pipelines/${p.id}`);
      toast.success(`'${p.name}' deleted.`);
      setPipelines(prev => prev.filter(x => x.id !== p.id));
    } catch (e: any) {
      if (e?.status === 409) {
        toast.error(e.message || 'Cannot delete — pipeline is in use by projects.');
      } else {
        toast.error('Failed to delete.');
      }
    }
  };

  const openBuild = async (p: IPipelineResource) => {
    if (p.scope === 'Project' && p.projectId) {
      try {
        const [project, versions] = await Promise.all([
          api.get<IProject>(`/api/projects/${p.projectId}`),
          api.get<IServiceVersion[]>(`/api/projects/${p.projectId}/current-versions`).catch(() => []),
        ]);
        setBuildProject(project);
        setBuildPipeline(p);
        setBuildVersions(versions);
        setBuildWizardOpen(true);
      } catch {
        toast.error('Failed to load project for this pipeline.');
      }
    } else {
      // Global pipeline — show project picker
      setPickerPipeline(p);
      setShowProjectPicker(true);
      setLoadingProjects(true);
      try {
        const list = await api.get<IProject[]>('/api/projects');
        setProjects(list);
      } catch {
        toast.error('Failed to load projects.');
      } finally {
        setLoadingProjects(false);
      }
    }
  };

  const handleProjectPick = async (project: IProject) => {
    setShowProjectPicker(false);
    try {
      const versions = await api.get<IServiceVersion[]>(`/api/projects/${project.id}/current-versions`).catch(() => []);
      setBuildProject(project);
      setBuildPipeline(pickerPipeline);
      setBuildVersions(versions);
      setBuildWizardOpen(true);
    } catch {
      toast.error('Failed to load project versions.');
    }
    setPickerPipeline(null);
  };

  const filteredProjects = projects.filter(p =>
    p.name.toLowerCase().includes(projectSearch.toLowerCase())
  );

  const getStepSummary = (steps: IPipelineStep[]) => {
    const counts = steps.reduce((acc, s) => {
      acc[s.type] = (acc[s.type] || 0) + 1;
      return acc;
    }, {} as Record<string, number>);

    return Object.entries(counts)
      .map(([type, count]) => `${count} ${type}`)
      .join(', ') || 'No steps';
  };

  const paneOpen = paneTarget !== undefined;
  const paneTitle = paneTarget === 'new' ? 'New Pipeline'
    : paneTarget === 'edit' ? `Edit: ${editPipeline?.name || ''}`
    : '';

  const filteredPipelines = filterProjectId
    ? pipelines.filter(p => p.projectId === filterProjectId)
    : pipelines;

  return (
    <>
      <Head><title>ShipRight — Pipelines</title></Head>
      <AppShell>
        <ZestResponsiveLayout
          sidePaneWidth="800px"
          closeOnDesktopOverlayClick={false}
          sidePane={{
            visible: paneOpen,
            title: paneTitle,
            onClose: closePane,
            content: paneOpen ? (
              <PipelineBuilder
                pipeline={paneTarget === 'edit' ? editPipeline : undefined}
                onSave={handleSave}
                onCancel={closePane}
              />
            ) : undefined,
          }}
        >
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 24 }}>
            <h1 className={styles.heading}>Pipelines</h1>
            <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
              {allProjects.length > 0 && (
                <select
                  value={filterProjectId}
                  onChange={e => setFilterProjectId(e.target.value)}
                  style={{
                    background: '#131D30', color: '#C9D6E3',
                    border: '1px solid rgba(255,255,255,0.12)',
                    borderRadius: 6, padding: '7px 10px', fontSize: 13,
                  }}
                >
                  <option value="">All projects</option>
                  {allProjects.map(p => (
                    <option key={p.id} value={p.id}>{p.name}</option>
                  ))}
                </select>
              )}
              <ZestButton onClick={openNew}
                zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
                New Pipeline
              </ZestButton>
            </div>
          </div>

          <div className={styles.grid}>
            {loading && [0, 1, 2].map(i => (
              <div key={i} className={`${styles.card} ${styles.skeletonCard}`}>
                <div className={`skeleton ${styles.skeletonTitle}`} />
              </div>
            ))}
            {!loading && filteredPipelines.map(p => (
              <div key={p.id} className={styles.card}>
                <div className={styles.cardTop}>
                  <div className={styles.cardContent}>
                    <h3 className={styles.cardTitle}>{p.name}</h3>
                    <p className={styles.cardScope}>{p.scope}</p>
                    <div className={styles.stepIcons}>
                      {p.steps.map((step, i) => (
                        <span key={i} className={styles.stepIcon} title={step.type}>
                          {STEP_ICONS[step.type] || '?'}
                        </span>
                      ))}
                    </div>
                    <p className={styles.cardDetail}>{getStepSummary(p.steps)}</p>
                  </div>
                  <div className={styles.cardActions}>
                    <ZestButton
                      onClick={() => openBuild(p)}
                      zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                    >
                      Build
                    </ZestButton>
                    <OverflowMenu items={[
                      { content: 'Edit', onClick: () => openEdit(p) },
                      { content: 'Delete', onClick: () => handleDelete(p) },
                    ]} />
                  </div>
                </div>
              </div>
            ))}
            {!loading && filteredPipelines.length === 0 && (
              <p className={styles.empty}>
                {filterProjectId
                  ? 'No pipelines for the selected project.'
                  : <>No pipelines configured.{' '}
                    <button onClick={openNew}
                      style={{ background: 'none', border: 'none', color: '#C9A84C', cursor: 'pointer' }}>
                      Create one
                    </button>.</>
                }
              </p>
            )}
          </div>
        </ZestResponsiveLayout>
      </AppShell>

      {/* Project picker for global pipeline build */}
      {showProjectPicker && (
        <div className={styles.pickerOverlay}>
          <div className={styles.pickerCard}>
            <h3 className={styles.pickerTitle}>Select Project</h3>
            <ZestTextbox
              value={projectSearch}
              onChange={e => setProjectSearch(e.target.value)}
              placeholder="Search projects..."
              zest={{ stretch: true, zSize: 'sm' }}
            />
            <div className={styles.pickerList}>
              {loadingProjects && <div className={styles.pickerEmpty}>Loading projects...</div>}
              {!loadingProjects && filteredProjects.length === 0 && (
                <div className={styles.pickerEmpty}>No projects found.</div>
              )}
              {!loadingProjects && filteredProjects.map(p => (
                <button key={p.id} className={styles.pickerItem} onClick={() => handleProjectPick(p)}>
                  {p.name}
                </button>
              ))}
            </div>
            <ZestButton onClick={() => { setShowProjectPicker(false); setPickerPipeline(null); }}
              zest={{ buttonStyle: 'outline' }}>
              Cancel
            </ZestButton>
          </div>
        </div>
      )}

      {/* Build wizard */}
      {buildProject && (
        <BuildWizard
          projectId={buildProject.id}
          projectName={buildProject.name}
          currentVersions={buildVersions}
          defaultDeployMode={buildProject.server.deployMode}
          isOpen={buildWizardOpen}
          initialPipeline={buildPipeline ?? undefined}
          onClose={() => {
            setBuildWizardOpen(false);
            setBuildProject(null);
            setBuildPipeline(null);
            setBuildVersions([]);
          }}
        />
      )}
    </>
  );
}
