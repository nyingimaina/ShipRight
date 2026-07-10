import { useState, useEffect } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import type { IPipelineResource, IPipelineStep } from '@/shared/types/IProject';
import styles from './Styles/StepPicker.module.css';

interface Props {
  onSelectPipeline: (pipeline: IPipelineResource) => void;
  onUseCustom: () => void;
  onCancel: () => void;
}

const STEP_ICONS: Record<string, string> = {
  Script: '📜',
  Build: '🐳',
  Push: '📤',
  Deploy: '🚀',
};

export default function PipelineSelector({ onSelectPipeline, onUseCustom, onCancel }: Props) {
  const [pipelines, setPipelines] = useState<IPipelineResource[]>([]);
  const [loading, setLoading] = useState(true);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  useEffect(() => {
    api.get<IPipelineResource[]>('/api/resources/pipelines')
      .then(setPipelines)
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  const handleConfirm = () => {
    const pipeline = pipelines.find(p => p.id === selectedId);
    if (pipeline) {
      onSelectPipeline(pipeline);
    }
  };

  const getStepSummary = (steps: IPipelineStep[]) => {
    return steps.map(s => `${STEP_ICONS[s.type] || '?'} ${s.type}`).join(' → ');
  };

  return (
    <>
      <div className={styles.title}>Select Build Pipeline</div>

      {loading && (
        <div className={styles.list}>
          <div style={{ padding: '16px', color: '#637389', textAlign: 'center' }}>Loading pipelines...</div>
        </div>
      )}

      {!loading && pipelines.length === 0 && (
        <div className={styles.list}>
          <div style={{ padding: '16px', color: '#637389', textAlign: 'center' }}>
            No pipelines configured.{' '}
            <a href="/pipelines" style={{ color: '#C9A84C' }}>Create one</a> or use custom steps.
          </div>
        </div>
      )}

      {!loading && pipelines.length > 0 && (
        <div className={styles.list}>
          {pipelines.map(p => (
            <label key={p.id} className={styles.row} style={{ flexDirection: 'column', alignItems: 'flex-start', gap: 4 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, width: '100%' }}>
                <input
                  type="radio"
                  name="pipeline"
                  className={styles.checkbox}
                  checked={selectedId === p.id}
                  onChange={() => setSelectedId(p.id)}
                />
                <span className={styles.label}>{p.name}</span>
                <span style={{ fontSize: 11, color: '#637389', marginLeft: 'auto' }}>{p.scope}</span>
              </div>
              <div style={{ fontSize: 12, color: '#8B95A5', paddingLeft: 24 }}>
                {getStepSummary(p.steps)}
              </div>
            </label>
          ))}
        </div>
      )}

      <div className={styles.actions}>
        <ZestButton
          onClick={handleConfirm}
          disabled={!selectedId}
          zest={{ visualOptions: { variant: 'standard' }, semanticType: 'submit' }}
        >
          Use Pipeline
        </ZestButton>
        <ZestButton
          onClick={onUseCustom}
          zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}
        >
          Custom Steps
        </ZestButton>
        <ZestButton
          onClick={onCancel}
          zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}
        >
          Cancel
        </ZestButton>
      </div>
    </>
  );
}
