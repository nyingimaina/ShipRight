import { useState, useEffect } from 'react';
import {
  DndContext,
  closestCenter,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
} from '@dnd-kit/core';
import type { DragEndEvent } from '@dnd-kit/core';
import {
  arrayMove,
  SortableContext,
  sortableKeyboardCoordinates,
  verticalListSortingStrategy,
  useSortable,
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import ZestButton from 'jattac.libs.web.zest-button';
import toast from 'react-hot-toast';
import { api } from '@/shared/ApiService';
import type {
  IPipelineResource,
  IPipelineStep,
  IScriptResource,
  PipelineStepType,
  ScriptPlatform,
  ExecutionTarget,
  DeployMode,
} from '@/shared/types/IProject';
import styles from './Styles/PipelineBuilder.module.css';

interface Props {
  pipeline?: IPipelineResource | null;
  onSave: (pipeline: IPipelineResource) => void;
  onCancel: () => void;
}

const STEP_ICONS: Record<PipelineStepType, string> = {
  Script: '📜',
  Build: '🐳',
  Push: '📤',
  Deploy: '🚀',
};

const STEP_LABELS: Record<PipelineStepType, string> = {
  Script: 'Script',
  Build: 'Build Image',
  Push: 'Push to Registry',
  Deploy: 'Deploy to Production',
};

const PLATFORM_OPTIONS: ScriptPlatform[] = ['Bash', 'PowerShell', 'Cmd', 'Python', 'Sh'];
const TARGET_OPTIONS: ExecutionTarget[] = ['Local', 'Remote'];
const DEPLOY_MODE_OPTIONS: DeployMode[] = ['GitScript', 'GitCompose', 'EnvCompose'];

export default function PipelineBuilder({ pipeline, onSave, onCancel }: Props) {
  const [name, setName] = useState(pipeline?.name || '');
  const [scope, setScope] = useState<'Global' | 'Project'>(pipeline?.scope || 'Global');
  const [steps, setSteps] = useState<IPipelineStep[]>(pipeline?.steps || []);
  const [selectedStepId, setSelectedStepId] = useState<string | null>(null);
  const [scripts, setScripts] = useState<IScriptResource[]>([]);
  const [saving, setSaving] = useState(false);

  const sensors = useSensors(
    useSensor(PointerSensor),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates })
  );

  useEffect(() => {
    api.get<IScriptResource[]>('/api/resources/scripts')
      .then(setScripts)
      .catch(() => toast.error('Failed to load scripts'));
  }, []);

  const selectedStep = steps.find(s => s.id === selectedStepId);

  const addStep = (type: PipelineStepType) => {
    if (type !== 'Script') {
      const exists = steps.some(s => s.type === type);
      if (exists) {
        toast.error(`${STEP_LABELS[type]} step already exists.`);
        return;
      }
    }

    const newStep: IPipelineStep = {
      id: crypto.randomUUID(),
      type,
      label: type === 'Script' ? '' : STEP_LABELS[type],
      scriptResourceId: type === 'Script' ? undefined : undefined,
      deployMode: type === 'Deploy' ? 'GitScript' : undefined,
      continueOnError: false,
    };

    const insertIdx = getInsertionIndex(steps, type);
    const next = [...steps];
    next.splice(insertIdx, 0, newStep);
    setSteps(next);
    setSelectedStepId(newStep.id);
  };

  const removeStep = (id: string) => {
    setSteps(prev => prev.filter(s => s.id !== id));
    if (selectedStepId === id) setSelectedStepId(null);
  };

  const updateStep = (id: string, updates: Partial<IPipelineStep>) => {
    setSteps(prev => prev.map(s => s.id === id ? { ...s, ...updates } : s));
  };

  const moveStep = (id: string, direction: 'up' | 'down') => {
    const idx = steps.findIndex(s => s.id === id);
    if (idx < 0) return;
    const newIdx = direction === 'up' ? idx - 1 : idx + 1;
    if (newIdx < 0 || newIdx >= steps.length) return;
    const next = [...steps];
    [next[idx], next[newIdx]] = [next[newIdx], next[idx]];
    setSteps(next);
  };

  const handleDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;

    const oldIndex = steps.findIndex(s => s.id === active.id);
    const newIndex = steps.findIndex(s => s.id === over.id);
    setSteps(arrayMove(steps, oldIndex, newIndex));
  };

  const handleSave = async () => {
    if (!name.trim()) {
      toast.error('Pipeline name is required.');
      return;
    }
    if (steps.length === 0) {
      toast.error('Pipeline must have at least one step.');
      return;
    }

    // Validate step order
    const errors = validateStepOrder(steps);
    if (errors.length > 0) {
      toast.error(errors[0]);
      return;
    }

    // Validate script steps have resources
    const scriptStepsMissingResource = steps.filter(s => s.type === 'Script' && !s.scriptResourceId);
    if (scriptStepsMissingResource.length > 0) {
      toast.error('All script steps must have a script resource selected.');
      return;
    }

    setSaving(true);
    try {
      const payload: IPipelineResource = {
        id: pipeline?.id || crypto.randomUUID(),
        name: name.trim(),
        steps,
        scope,
        projectId: pipeline?.projectId,
        createdAt: pipeline?.createdAt || new Date().toISOString(),
        modifiedAt: new Date().toISOString(),
      };

      if (pipeline?.id) {
        await api.put(`/api/resources/pipelines/${pipeline.id}`, payload);
      } else {
        await api.post('/api/resources/pipelines', payload);
      }
      toast.success(`Pipeline "${payload.name}" saved.`);
      onSave(payload);
    } catch (e: any) {
      toast.error(e?.message || 'Failed to save pipeline.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className={styles.container}>
      <div className={styles.header}>
        <input
          className={styles.nameInput}
          value={name}
          onChange={e => setName(e.target.value)}
          placeholder="Pipeline name..."
        />
        <select
          className={styles.scopeSelect}
          value={scope}
          onChange={e => setScope(e.target.value as 'Global' | 'Project')}
        >
          <option value="Global">Global</option>
          <option value="Project">Project</option>
        </select>
      </div>

      <div className={styles.body}>
        <div className={styles.stepsPanel}>
          <div className={styles.stepsHeader}>
            <span>Steps</span>
            <span className={styles.stepCount}>{steps.length}</span>
          </div>

          <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
            <SortableContext items={steps.map(s => s.id)} strategy={verticalListSortingStrategy}>
              <div className={styles.stepsList}>
                {steps.map(step => (
                  <SortableStep
                    key={step.id}
                    step={step}
                    isSelected={step.id === selectedStepId}
                    onSelect={() => setSelectedStepId(step.id)}
                    onRemove={() => removeStep(step.id)}
                    onMoveUp={() => moveStep(step.id, 'up')}
                    onMoveDown={() => moveStep(step.id, 'down')}
                    scripts={scripts}
                  />
                ))}
              </div>
            </SortableContext>
          </DndContext>

          {steps.length === 0 && (
            <div className={styles.emptySteps}>
              No steps added yet. Use the buttons below to add steps.
            </div>
          )}

          <div className={styles.addButtons}>
            <ZestButton onClick={() => addStep('Script')} zest={{ buttonStyle: 'outline' }}>
              + Script
            </ZestButton>
            <ZestButton onClick={() => addStep('Build')} zest={{ buttonStyle: 'outline' }}>
              + Build
            </ZestButton>
            <ZestButton onClick={() => addStep('Push')} zest={{ buttonStyle: 'outline' }}>
              + Push
            </ZestButton>
            <ZestButton onClick={() => addStep('Deploy')} zest={{ buttonStyle: 'outline' }}>
              + Deploy
            </ZestButton>
          </div>
        </div>

        <div className={styles.configPanel}>
          {selectedStep ? (
            <StepConfig
              step={selectedStep}
              scripts={scripts}
              onChange={(updates) => updateStep(selectedStep.id, updates)}
            />
          ) : (
            <div className={styles.noSelection}>
              Select a step to configure it
            </div>
          )}
        </div>
      </div>

      <div className={styles.footer}>
        <ZestButton onClick={handleSave} disabled={saving}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid', semanticType: 'save' }}>
          {saving ? 'Saving…' : pipeline?.id ? 'Update Pipeline' : 'Create Pipeline'}
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
          Cancel
        </ZestButton>
      </div>
    </div>
  );
}

function SortableStep({ step, isSelected, onSelect, onRemove, onMoveUp, onMoveDown, scripts }: {
  step: IPipelineStep;
  isSelected: boolean;
  onSelect: () => void;
  onRemove: () => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  scripts: IScriptResource[];
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: step.id });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
  };

  const scriptName = step.type === 'Script' && step.scriptResourceId
    ? scripts.find(s => s.id === step.scriptResourceId)?.name || 'Unknown script'
    : null;

  const platform = step.type === 'Script' && step.scriptResourceId
    ? scripts.find(s => s.id === step.scriptResourceId)?.platform || 'Bash'
    : null;

  return (
    <div
      ref={setNodeRef}
      style={style}
      className={`${styles.stepRow} ${isSelected ? styles.stepRowSelected : ''}`}
      onClick={onSelect}
    >
      <div className={styles.dragHandle} {...attributes} {...listeners}>
        ≡
      </div>
      <span className={styles.stepIcon}>{STEP_ICONS[step.type]}</span>
      <div className={styles.stepInfo}>
        <span className={styles.stepLabel}>
          {step.label || STEP_LABELS[step.type]}
          {scriptName && <span className={styles.scriptName}> — {scriptName}</span>}
        </span>
        {platform && (
          <span className={styles.stepMeta}>{platform} | {step.continueOnError ? 'continue on error' : 'abort on error'}</span>
        )}
      </div>
      <div className={styles.stepActions}>
        <button className={styles.arrowBtn} onClick={(e) => { e.stopPropagation(); onMoveUp(); }}>↑</button>
        <button className={styles.arrowBtn} onClick={(e) => { e.stopPropagation(); onMoveDown(); }}>↓</button>
        <button className={styles.removeBtn} onClick={(e) => { e.stopPropagation(); onRemove(); }}>×</button>
      </div>
    </div>
  );
}

function StepConfig({ step, scripts, onChange }: {
  step: IPipelineStep;
  scripts: IScriptResource[];
  onChange: (updates: Partial<IPipelineStep>) => void;
}) {
  if (step.type === 'Script') {
    return (
      <div className={styles.configContent}>
        <h3 className={styles.configTitle}>Script Step Configuration</h3>
        <div className={styles.configField}>
          <label>Label</label>
          <input
            value={step.label || ''}
            onChange={e => onChange({ label: e.target.value })}
            placeholder="e.g. Pre-build checks"
          />
        </div>
        <div className={styles.configField}>
          <label>Script Resource</label>
          <select
            value={step.scriptResourceId || ''}
            onChange={e => onChange({ scriptResourceId: e.target.value || undefined })}
          >
            <option value="">Select a script...</option>
            {scripts.map(s => (
              <option key={s.id} value={s.id}>{s.name} ({s.platform})</option>
            ))}
          </select>
        </div>
        <div className={styles.configField}>
          <label>
            <input
              type="checkbox"
              checked={step.continueOnError || false}
              onChange={e => onChange({ continueOnError: e.target.checked })}
            />
            {' '}Continue on error
          </label>
        </div>
      </div>
    );
  }

  if (step.type === 'Deploy') {
    return (
      <div className={styles.configContent}>
        <h3 className={styles.configTitle}>Deploy Step Configuration</h3>
        <div className={styles.configField}>
          <label>Deploy Mode</label>
          <select
            value={step.deployMode || 'GitScript'}
            onChange={e => onChange({ deployMode: e.target.value as DeployMode })}
          >
            {DEPLOY_MODE_OPTIONS.map(m => (
              <option key={m} value={m}>{m}</option>
            ))}
          </select>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.configContent}>
      <h3 className={styles.configTitle}>{STEP_LABELS[step.type]} Step</h3>
      <p className={styles.configNote}>This step runs the {STEP_LABELS[step.type].toLowerCase()} pipeline.</p>
    </div>
  );
}

function getInsertionIndex(steps: IPipelineStep[], type: PipelineStepType): number {
  if (type === 'Script') {
    // Find the position based on type ordering
    const buildIdx = steps.findIndex(s => s.type === 'Build');
    const pushIdx = steps.findIndex(s => s.type === 'Push');
    const deployIdx = steps.findIndex(s => s.type === 'Deploy');

    // Insert before the first fixed step
    const fixedIndices = [buildIdx, pushIdx, deployIdx].filter(i => i >= 0);
    return fixedIndices.length > 0 ? Math.min(...fixedIndices) : steps.length;
  }

  // Fixed steps go at the end in order
  const order = ['Build', 'Push', 'Deploy'];
  const typeOrder = order.indexOf(type);
  let insertIdx = steps.length;

  for (let i = 0; i < steps.length; i++) {
    const stepOrder = order.indexOf(steps[i].type);
    if (stepOrder > typeOrder) {
      insertIdx = i;
      break;
    }
  }

  return insertIdx;
}

function validateStepOrder(steps: IPipelineStep[]): string[] {
  const errors: string[] = [];
  let lastFixedOrder = -1;
  const order = ['Build', 'Push', 'Deploy'];

  for (const step of steps) {
    if (step.type === 'Script') continue;
    const stepOrder = order.indexOf(step.type);
    if (stepOrder < lastFixedOrder) {
      errors.push(`${step.type} step must come after ${order[lastFixedOrder]} step.`);
    }
    lastFixedOrder = stepOrder;
  }

  return errors;
}
