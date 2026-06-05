import { useCallback, useEffect, useRef, useState } from 'react';
import { Drawer } from 'vaul';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { RiCheckLine, RiCloseLine, RiLoader4Line } from 'react-icons/ri';
import { api } from '@/shared/ApiService';
import { buildSse } from '@/shared/SseService';
import { IBuildRecord } from '@/shared/types/IBuildRecord';
import { IServiceVersion } from '@/shared/types/IBuildRecord';
import LogViewer, { LogEntry } from './LogViewer';
import styles from './Styles/BuildWizard.module.css';

interface Props {
  projectId: string;
  projectName: string;
  currentVersions: IServiceVersion[];
  isOpen: boolean;
  onClose: () => void;
}

type Phase = 'versions' | 'pipeline' | 'done';
type StepStatus = 'pending' | 'running' | 'done' | 'failed';

const STEP_NAMES = [
  'PreconditionCheck', 'GitStatusCheck', 'BranchCheck',
  'WriteVersionsAndTag', 'ComposeRepoSync', 'DockerLoginCheck',
  'DockerBuildAndPush', 'BuildComplete',
];

interface PauseState {
  reason: string;
  prompt: string;
  options: string[];
  commitMessage?: string;
}

let lineCounter = 0;

export default function BuildWizard({ projectId, projectName, currentVersions, isOpen, onClose }: Props) {
  const [phase, setPhase] = useState<Phase>('versions');
  const [newVersions, setNewVersions] = useState<Record<string, string>>({});
  const [buildId, setBuildId] = useState<string | null>(null);
  const [buildRecord, setBuildRecord] = useState<IBuildRecord | null>(null);
  const [lines, setLines] = useState<LogEntry[]>([]);
  const [stepStatuses, setStepStatuses] = useState<Record<string, StepStatus>>({});
  const [pause, setPause] = useState<PauseState | null>(null);
  const [connState, setConnState] = useState<'connected' | 'reconnecting' | 'disconnected'>('connected');
  const [deployPending, setDeployPending] = useState(false);
  const sseConnected = useRef(false);

  // Pre-fill versions with suggested next
  useEffect(() => {
    const map: Record<string, string> = {};
    currentVersions.forEach(v => { map[v.serviceName] = v.suggestedNext ?? v.version ?? ''; });
    setNewVersions(map);
  }, [currentVersions]);

  const appendLine = useCallback((source: string, line: string) => {
    setLines(prev => [...prev, { id: lineCounter++, source, line }]);
  }, []);

  const connectSse = useCallback((id: string) => {
    if (sseConnected.current) return;
    sseConnected.current = true;

    buildSse.connect(id, {
      onLogLine:       e => appendLine(e.source, e.line),
      onStepStarted:   e => setStepStatuses(p => ({ ...p, [e.stepName]: 'running' })),
      onStepCompleted: e => setStepStatuses(p => ({ ...p, [e.stepName]: e.success ? 'done' : 'failed' })),
      onPauseRequested: e => setPause({ reason: e.reason, prompt: e.prompt, options: e.options }),
      onBuildCompleted: e => {
        setBuildRecord(prev => prev ? { ...prev, status: e.status as IBuildRecord['status'], gitTag: e.gitTag ?? '' } : null);
        if (e.status === 'BuildSucceeded' || e.status === 'BuildFailed' ||
            e.status === 'Aborted' || e.status === 'Interrupted')
          setPhase('done');
      },
      onDeployCompleted: e => {
        setBuildRecord(prev => prev ? { ...prev, status: e.status as IBuildRecord['status'] } : null);
      },
      onConnectionChange: s => {
        setConnState(s);
        if (s === 'connected' && buildId)
          buildSse.catchUp(buildId).then(r => { if (r) setBuildRecord(r); });
      },
    });
  }, [buildId, appendLine]);

  const handleStartBuild = async () => {
    const serviceVersions = currentVersions.map(v => ({
      serviceName: v.serviceName,
      newVersion: newVersions[v.serviceName] ?? '',
    }));

    const result = await api.post<{ buildId: string }>('/api/builds/start', {
      projectId,
      serviceVersions,
    });

    setBuildId(result.buildId);
    const record = await api.get<IBuildRecord>(`/api/builds/${result.buildId}`);
    setBuildRecord(record);
    setPhase('pipeline');
    connectSse(result.buildId);
  };

  const handlePauseRespond = async (choice: string) => {
    if (!buildId || !pause) return;
    const data: Record<string, string> = {};
    if (pause.commitMessage) data.commitMessage = pause.commitMessage;
    await api.post(`/api/builds/${buildId}/respond`, { reason: pause.reason, choice, data });
    setPause(null);
  };

  const handleDeploy = async () => {
    if (!buildId) return;
    setDeployPending(true);
    try {
      await api.post(`/api/builds/${buildId}/deploy`, {});
      sseConnected.current = false;
      connectSse(buildId);
    } finally {
      setDeployPending(false);
    }
  };

  const handleClose = () => {
    buildSse.disconnect();
    sseConnected.current = false;
    setPhase('versions');
    setLines([]);
    setStepStatuses({});
    setPause(null);
    setBuildId(null);
    setBuildRecord(null);
    onClose();
  };

  const connClass = connState === 'reconnecting' ? styles.connStatusReconnecting
    : connState === 'disconnected' ? styles.connStatusDisconnected
    : styles.connStatus;

  const isBuildSucceeded = buildRecord?.status === 'BuildSucceeded';
  const isDeployed = buildRecord?.status === 'Deployed' || buildRecord?.status === 'DeployFailed';

  return (
    <Drawer.Root open={isOpen} onOpenChange={open => { if (!open) handleClose(); }}>
      <Drawer.Portal>
        <Drawer.Overlay className={styles.overlay} />
        <Drawer.Content className={styles.content}>
          <div className={styles.handle} />
          <Drawer.Title className={styles.header}>
            <span className={styles.title}>
              {phase === 'versions' ? `Build — ${projectName}` : `Build ${buildRecord?.gitTag || '…'}`}
            </span>
            {phase !== 'versions' && (
              <span className={connClass}>
                {connState === 'reconnecting' ? 'Reconnecting…' : connState === 'disconnected' ? 'Disconnected' : ''}
              </span>
            )}
          </Drawer.Title>

          <div className={styles.body}>
            {/* PHASE: versions */}
            {phase === 'versions' && (
              <>
                <div className={styles.versionsGrid}>
                  {currentVersions.map(v => (
                    <div key={v.serviceName} className={styles.versionRow}>
                      <span className={styles.versionServiceName}>{v.serviceName}</span>
                      <span className={styles.versionCurrent}>{v.version ?? '?'}</span>
                      <span className={styles.versionArrow}>→</span>
                      <div className={styles.versionInputWrap}>
                        <ZestTextbox
                          value={newVersions[v.serviceName] ?? ''}
                          onChange={e => setNewVersions(p => ({ ...p, [v.serviceName]: e.target.value }))}
                          zest={{ zSize: 'sm', stretch: true }}
                        />
                      </div>
                    </div>
                  ))}
                </div>
                <div className={styles.actions}>
                  <ZestButton onClick={handleStartBuild}
                    zest={{ visualOptions: { variant: 'standard' }, semanticType: 'submit' }}>
                    Start Build
                  </ZestButton>
                  <ZestButton onClick={handleClose} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>
                    Cancel
                  </ZestButton>
                </div>
              </>
            )}

            {/* PHASE: pipeline / done */}
            {(phase === 'pipeline' || phase === 'done') && (
              <>
                {/* Step tracker */}
                <div className={styles.stepTracker}>
                  {STEP_NAMES.map(name => {
                    const status = stepStatuses[name] ?? 'pending';
                    const cls = `${styles.step} ${styles[`step${status.charAt(0).toUpperCase() + status.slice(1)}`] ?? ''}`;
                    const icon = status === 'done' ? '✓' : status === 'failed' ? '✗' : status === 'running' ? '◎' : '○';
                    return (
                      <span key={name} className={cls}>
                        <span className={styles.stepIcon}>{icon}</span>
                        {name.replace(/([A-Z])/g, ' $1').trim()}
                      </span>
                    );
                  })}
                </div>

                {/* Log viewer */}
                <LogViewer lines={lines} isLive={phase === 'pipeline'} />

                {/* Error summary */}
                {buildRecord?.errorSummary && (
                  <div className={styles.errorBanner}>{buildRecord.errorSummary}</div>
                )}

                {/* Deploy row — appears when build succeeded */}
                {isBuildSucceeded && !isDeployed && (
                  <div className={styles.deploySection}>
                    <div>
                      <div className={styles.deployInfo}>Build succeeded — ready to deploy</div>
                      <div className={styles.deployTag}>{buildRecord?.gitTag}</div>
                    </div>
                    <ZestButton onClick={handleDeploy}
                      zest={{ visualOptions: { variant: 'standard' }, busyOptions: { handleInternally: true } }}>
                      Deploy to Production
                    </ZestButton>
                  </div>
                )}

                {isDeployed && (
                  <div className={styles.deploySection}>
                    <span className={styles.deployInfo}>
                      {buildRecord?.status === 'Deployed' ? '✓ Deployed successfully' : '✗ Deployment failed'}
                    </span>
                    <ZestButton onClick={handleClose} zest={{ buttonStyle: 'outline' }}>Close</ZestButton>
                  </div>
                )}
              </>
            )}
          </div>

          {/* Pause overlay */}
          {pause && (
            <div className={styles.pauseOverlay}>
              <div className={styles.pauseCard}>
                <div className={styles.pauseTitle}>Action Required</div>
                <p className={styles.pausePrompt}>{pause.prompt}</p>
                {pause.reason === 'git_dirty' && (
                  <div className={styles.pauseInput}>
                    <label className={styles.pauseLabel}>Commit message</label>
                    <ZestTextbox
                      value={pause.commitMessage ?? ''}
                      onChange={e => setPause(p => p ? { ...p, commitMessage: e.target.value } : null)}
                      placeholder="Describe your changes"
                      zest={{ stretch: true }}
                    />
                  </div>
                )}
                <div className={styles.pauseButtons}>
                  {pause.options.map(opt => (
                    <ZestButton key={opt} onClick={() => handlePauseRespond(opt)}
                      zest={{ visualOptions: { variant: opt === 'abort' ? 'danger' : 'standard' } }}>
                      {opt.charAt(0).toUpperCase() + opt.slice(1)}
                    </ZestButton>
                  ))}
                </div>
              </div>
            </div>
          )}
        </Drawer.Content>
      </Drawer.Portal>
    </Drawer.Root>
  );
}
