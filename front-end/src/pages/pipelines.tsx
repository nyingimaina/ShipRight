import Head from 'next/head';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import { ZestResponsiveLayout } from 'jattac.libs.web.zest-responsive-layout';
import ZestButton from 'jattac.libs.web.zest-button';
import OverflowMenu from 'jattac.libs.web.overflow-menu';
import AppShell from '@/modules/AppShell/AppShell';
import PipelineBuilder from '@/modules/BuildWizard/PipelineBuilder';
import { api } from '@/shared/ApiService';
import type { IPipelineResource, IPipelineStep } from '@/shared/types/IProject';
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

  const load = async () => {
    setLoading(true);
    try {
      const data = await api.get<IPipelineResource[]>('/api/resources/pipelines');
      setPipelines(data);
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
            <ZestButton onClick={openNew}
              zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
              New Pipeline
            </ZestButton>
          </div>

          <div className={styles.grid}>
            {loading && [0, 1, 2].map(i => (
              <div key={i} className={`${styles.card} ${styles.skeletonCard}`}>
                <div className={`skeleton ${styles.skeletonTitle}`} />
              </div>
            ))}
            {!loading && pipelines.map(p => (
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
                  <OverflowMenu items={[
                    { content: 'Edit', onClick: () => openEdit(p) },
                    { content: 'Delete', onClick: () => handleDelete(p) },
                  ]} />
                </div>
              </div>
            ))}
            {!loading && pipelines.length === 0 && (
              <p className={styles.empty}>
                No pipelines configured.{' '}
                <button onClick={openNew}
                  style={{ background: 'none', border: 'none', color: '#C9A84C', cursor: 'pointer' }}>
                  Create one
                </button>.
              </p>
            )}
          </div>
        </ZestResponsiveLayout>
      </AppShell>
    </>
  );
}
