import { useCallback, useEffect, useRef, useState } from 'react';
import { Drawer } from 'vaul';
import confetti from 'canvas-confetti';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { api } from '@/shared/ApiService';
import { buildSse } from '@/shared/SseService';
import { IBuildRecord } from '@/shared/types/IBuildRecord';
import { IServiceVersion } from '@/shared/types/IBuildRecord';
import { DeployMode } from '@/shared/types/IProject';
import LogViewer, { LogEntry } from './LogViewer';
import OptionPicker, { PickerOption } from './OptionPicker';
import styles from './Styles/BuildWizard.module.css';

interface Props {
  projectId: string;
  projectName: string;
  currentVersions: IServiceVersion[];
  defaultDeployMode: DeployMode;
  isOpen: boolean;
  onClose: () => void;
  initialBuildId?: string;
}

type Phase = 'versions' | 'pipeline' | 'done';
type StepStatus = 'pending' | 'running' | 'done' | 'failed';

interface BuildStats {
  sampleCount: number;
  stageExpected: Record<string, number>;
  totalBuildExpected: number | null;
  totalPushExpected: number | null;
  totalDeployExpected: number | null;
}

const BUILD_STEP_NAMES = [
  'PreconditionCheck', 'GitStatusCheck', 'BranchCheck',
  'WriteVersionsAndTag', 'ComposeRepoSync', 'DockerBuild', 'BuildComplete',
];
const PUSH_STEP_NAMES = ['DockerLoginCheck', 'DockerPush', 'PushComplete'];

const OPTION_LABELS: Record<string, string> = {
  commit_and_push:          'Commit & Push',
  commit:                   'Commit Only',
  merge:                    'Merge into Deploy Branch',
  switch:                   'Switch to Deploy Branch',
  build_here:               'Build Current Branch',
  abort:                    'Abort',
  login:                    'Log In',
  resume:                   'Resume from Failed Step',
  start_fresh:              'Start Fresh',
  install_buildx:           'Install BuildKit',
  continue_without_buildkit:'Continue Without BuildKit',
};

const OPTION_DESCS: Record<string, string> = {
  commit_and_push:          'Stage, commit, and push to remote immediately',
  commit:                   'Stage and commit locally — push manually later',
  merge:                    'Checkout deploy branch, pull, merge current, push',
  switch:                   'Checkout deploy branch and pull latest',
  build_here:               'Stay on current branch and build as-is',
  abort:                    'Stop this build and exit',
  login:                    'Enter Docker Hub credentials to proceed',
  resume:                   'Skip already-completed steps and continue from the failed step',
  start_fresh:              'Run all steps from scratch',
  install_buildx:           'Auto-detect WSL arch, download buildx from GitHub, and enable DOCKER_BUILDKIT=1',
  continue_without_buildkit:'Use the classic Docker builder (DOCKER_BUILDKIT=0) — slower, no layer cache',
};

interface PauseState {
  reason: string;
  prompt: string;
  options: string[];
  selected: string | null;
  commitMessage?: string;
  fields?: string[];
  fieldValues?: Record<string, string>;
  checkboxes?: string[];
  checkboxValues?: Record<string, boolean>;
}

const fmtSecs = (s: number) =>
  `${Math.floor(s / 60).toString().padStart(2, '0')}:${(s % 60).toString().padStart(2, '0')}`;

const fmtExpected = (s: number | null | undefined) => {
  if (!s) return null;
  if (s < 60) return `~${s}s`;
  return `~${Math.floor(s / 60)}m${s % 60 > 0 ? `${s % 60}s` : ''}`;
};

let lineCounter = 0;

export default function BuildWizard({ projectId, projectName, currentVersions, defaultDeployMode, isOpen, onClose, initialBuildId }: Props) {
  const [phase, setPhase] = useState<Phase>('versions');
  const [deployModeOverride, setDeployModeOverride] = useState<DeployMode>(defaultDeployMode);
  const [newVersions, setNewVersions] = useState<Record<string, string>>({});
  const [buildId, setBuildId] = useState<string | null>(null);
  const [buildRecord, setBuildRecord] = useState<IBuildRecord | null>(null);
  const [lines, setLines] = useState<LogEntry[]>([]);
  const [stepStatuses, setStepStatuses] = useState<Record<string, StepStatus>>({});
  const [currentStepName, setCurrentStepName] = useState<string | null>(null);
  const [stepStartTimes, setStepStartTimes] = useState<Record<string, number>>({});
  const [stepActualDurations, setStepActualDurations] = useState<Record<string, number>>({});
  const [activePushPhase, setActivePushPhase] = useState(false);
  const [pause, setPause] = useState<PauseState | null>(null);
  const [connState, setConnState] = useState<'connected' | 'reconnecting' | 'disconnected'>('connected');
  const [elapsed, setElapsed] = useState(0);
  const [buildStats, setBuildStats] = useState<BuildStats | null>(null);
  const sseConnected = useRef(false);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => {
    const map: Record<string, string> = {};
    currentVersions.forEach(v => { map[v.serviceName] = v.suggestedNext ?? v.version ?? ''; });
    setNewVersions(map);
  }, [currentVersions]);

  // When opened with an existing build ID, skip straight to the done phase
  useEffect(() => {
    if (!isOpen || !initialBuildId) return;
    api.get<IBuildRecord>(`/api/builds/${initialBuildId}`).then(record => {
      setBuildId(initialBuildId);
      setBuildRecord(record);
      setPhase('done');
      const isPush = record.status === 'PushSucceeded' || record.status === 'BuildSucceeded'
        || record.status === 'PushFailed';
      const stepNames = isPush ? PUSH_STEP_NAMES : BUILD_STEP_NAMES;
      const doneStatuses: Record<string, StepStatus> = {};
      stepNames.forEach(n => { doneStatuses[n] = 'done'; });
      setStepStatuses(doneStatuses);
      setActivePushPhase(isPush);
    }).catch(() => {});
  }, [isOpen, initialBuildId]);

  // Fetch build stats when wizard opens
  useEffect(() => {
    if (isOpen && projectId) {
      api.get<BuildStats>(`/api/projects/${projectId}/build-stats`).then(setBuildStats).catch(() => {});
    }
  }, [isOpen, projectId]);

  // Elapsed timer — runs while pipeline phase is active
  useEffect(() => {
    if (phase === 'pipeline') {
      setElapsed(0);
      timerRef.current = setInterval(() => setElapsed(e => e + 1), 1000);
    } else {
      if (timerRef.current) { clearInterval(timerRef.current); timerRef.current = null; }
    }
    return () => { if (timerRef.current) clearInterval(timerRef.current); };
  }, [phase]);

  // Celebration effects on key milestones
  useEffect(() => {
    const status = buildRecord?.status;
    if (status === 'Deployed') {
      // Full gold-and-green burst for a successful deploy
      confetti({ particleCount: 120, spread: 80, origin: { y: 0.55 },
        colors: ['#C9A84C', '#3D9970', '#F0F2F5', '#C9D6E3', '#C9943A'] });
      setTimeout(() => confetti({ particleCount: 60, spread: 50, origin: { x: 0.2, y: 0.6 },
        colors: ['#C9A84C', '#3D9970'] }), 250);
      setTimeout(() => confetti({ particleCount: 60, spread: 50, origin: { x: 0.8, y: 0.6 },
        colors: ['#C9A84C', '#3D9970'] }), 400);
    } else if (status === 'PushSucceeded' || status === 'BuildSucceeded') {
      // Smaller side-burst for a push success
      confetti({ particleCount: 55, spread: 55, origin: { y: 0.6 },
        colors: ['#4A7FA8', '#C9A84C', '#F0F2F5'] });
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [buildRecord?.status]);

  const appendLine = useCallback((source: string, line: string) => {
    setLines(prev => [...prev, { id: lineCounter++, source, line }]);
  }, []);

  const connectSse = useCallback((id: string) => {
    if (sseConnected.current) return;
    sseConnected.current = true;

    buildSse.connect(id, {
      onLogLine:       e => appendLine(e.source, e.line),
      onStepStarted: e => {
        setStepStatuses(p => ({ ...p, [e.stepName]: 'running' }));
        setCurrentStepName(e.stepName);
        setStepStartTimes(p => ({ ...p, [e.stepName]: Date.now() }));
      },
      onStepCompleted: e => {
        setStepStatuses(p => ({ ...p, [e.stepName]: e.success ? 'done' : 'failed' }));
        setStepStartTimes(p => {
          const started = p[e.stepName];
          if (started) setStepActualDurations(d => ({ ...d, [e.stepName]: Math.round((Date.now() - started) / 1000) }));
          return p;
        });
      },
      onPauseRequested: e => setPause({
        reason: e.reason, prompt: e.prompt, options: e.options, selected: null,
        fields: e.fields, fieldValues: {},
        checkboxes: e.checkboxes,
        checkboxValues: e.checkboxes ? Object.fromEntries(e.checkboxes.map(c => [c, true])) : undefined,
      }),
      onBuildCompleted: e => {
        setBuildRecord(prev => prev ? { ...prev, status: e.status as IBuildRecord['status'], gitTag: e.gitTag ?? prev.gitTag } : null);
        if (e.status === 'ImageBuilt' || e.status === 'BuildFailed' ||
            e.status === 'Aborted' || e.status === 'Interrupted')
          setPhase('done');
      },
      onPushCompleted: e => {
        setBuildRecord(prev => prev ? { ...prev, status: e.status as IBuildRecord['status'] } : null);
        setActivePushPhase(false);
        setPhase('done');
      },
      onDeployCompleted: e => {
        setBuildRecord(prev => prev ? { ...prev, status: e.status as IBuildRecord['status'] } : null);
      },
      onConnectionChange: s => {
        setConnState(s);
        if (s === 'connected' && id)
          buildSse.catchUp(id).then(r => {
            if (!r) return;
            setBuildRecord(r);
            // If the server-side record is already in a terminal state (build finished
            // before or during reconnect), drive the UI out of the spinning pipeline phase.
            const terminal: string[] = [
              'ImageBuilt', 'BuildFailed', 'Aborted', 'Interrupted',
              'PushSucceeded', 'PushFailed', 'Deployed', 'DeployFailed',
            ];
            if (terminal.includes(r.status)) setPhase('done');
          });
      },
    });
  }, [appendLine]);

  const handleStartBuild = async () => {
    const serviceVersions = currentVersions.map(v => ({
      serviceName: v.serviceName,
      newVersion: newVersions[v.serviceName] ?? '',
    }));
    const result = await api.post<{ buildId: string }>('/api/builds/start', { projectId, serviceVersions });
    setBuildId(result.buildId);
    const record = await api.get<IBuildRecord>(`/api/builds/${result.buildId}`);
    setBuildRecord(record);
    setPhase('pipeline');
    connectSse(result.buildId);
  };

  const handlePauseRespond = async () => {
    if (!buildId || !pause) return;
    if (!pause.selected && pause.options.length > 0) return;
    const data: Record<string, string> = {};
    if (pause.commitMessage) data.commitMessage = pause.commitMessage;
    if (pause.fieldValues) Object.assign(data, pause.fieldValues);
    if (pause.checkboxValues) {
      Object.entries(pause.checkboxValues).forEach(([k, v]) => { data[k] = v ? 'true' : 'false'; });
    }
    const choice = pause.selected ?? 'confirm';
    await api.post(`/api/builds/${buildId}/respond`, { reason: pause.reason, choice, data });
    setPause(null);
  };

  const handlePush = async () => {
    if (!buildId) return;
    setActivePushPhase(true);
    setStepStatuses({});
    setCurrentStepName(null);
    setStepStartTimes({});
    setStepActualDurations({});
    setElapsed(0);
    await api.post(`/api/builds/${buildId}/push`, {});
    sseConnected.current = false;
    connectSse(buildId);
  };

  const handleCancel = async () => {
    if (!buildId) return;
    try { await api.post(`/api/builds/${buildId}/cancel`, {}); } catch { /* already done */ }
  };

  const handleDeploy = async () => {
    if (!buildId) return;
    setElapsed(0);
    await api.post(`/api/builds/${buildId}/deploy`, { deployModeOverride });
    sseConnected.current = false;
    connectSse(buildId);
  };

  const handleClose = () => {
    buildSse.disconnect();
    sseConnected.current = false;
    setPhase('versions');
    setLines([]);
    setStepStatuses({});
    setCurrentStepName(null);
    setStepStartTimes({});
    setStepActualDurations({});
    setActivePushPhase(false);
    setPause(null);
    setBuildId(null);
    setBuildRecord(null);
    setElapsed(0);
    onClose();
  };

  const connClass = connState === 'reconnecting' ? styles.connStatusReconnecting
    : connState === 'disconnected' ? styles.connStatusDisconnected
    : styles.connStatus;

  const status = buildRecord?.status;
  const isImageBuilt    = status === 'ImageBuilt';
  const isPushSucceeded = status === 'PushSucceeded' || status === 'BuildSucceeded';
  const isPushFailed    = status === 'PushFailed';
  const isDeployed      = status === 'Deployed' || status === 'DeployFailed';
  const activeStepNames = activePushPhase ? PUSH_STEP_NAMES : BUILD_STEP_NAMES;

  // Expected duration for current step and overall
  const stepExpected = currentStepName && buildStats ? buildStats.stageExpected[currentStepName] : null;
  const totalExpected = activePushPhase
    ? buildStats?.totalPushExpected
    : buildStats?.totalBuildExpected;

  // Build picker options for pause
  const pickerOptions: PickerOption[] = pause?.options.map(opt => ({
    value: opt,
    label: OPTION_LABELS[opt] ?? opt,
    desc: OPTION_DESCS[opt],
    danger: opt === 'abort',
  })) ?? [];

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
                {buildStats && buildStats.sampleCount > 0 && buildStats.totalBuildExpected && (
                  <div className={styles.timerBar}>
                    <span className={styles.timerExpected}>
                      Based on {buildStats.sampleCount} build{buildStats.sampleCount !== 1 ? 's' : ''}:
                    </span>
                    <span>Build {fmtExpected(buildStats.totalBuildExpected)}</span>
                    {buildStats.totalPushExpected && <span className={styles.timerSep}>·</span>}
                    {buildStats.totalPushExpected && <span>Push {fmtExpected(buildStats.totalPushExpected)}</span>}
                    {buildStats.totalDeployExpected && <span className={styles.timerSep}>·</span>}
                    {buildStats.totalDeployExpected && <span>Deploy {fmtExpected(buildStats.totalDeployExpected)}</span>}
                  </div>
                )}
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
                  {activeStepNames.map(name => {
                    const s = stepStatuses[name] ?? 'pending';
                    const cls = `${styles.step} ${styles[`step${s.charAt(0).toUpperCase() + s.slice(1)}`] ?? ''}`;
                    const label = name.replace(/([A-Z])/g, ' $1').trim();
                    // Duration annotation: actual for done/failed, live counter for running
                    const durationNote = (() => {
                      if (s === 'done' || s === 'failed') {
                        const d = stepActualDurations[name];
                        return d != null ? ` (${d}s)` : null;
                      }
                      if (s === 'running') {
                        const started = stepStartTimes[name];
                        const stepSec = started ? Math.round((Date.now() - started) / 1000) : 0;
                        const exp = buildStats?.stageExpected[name];
                        return exp ? ` ${fmtSecs(stepSec)}/${fmtExpected(exp)}` : ` ${fmtSecs(stepSec)}`;
                      }
                      return null;
                    })();
                    return (
                      <span key={name} className={cls}>
                        {s === 'running'
                          ? <span className={styles.stepSpinner} />
                          : <span className={styles.stepIcon}>{s === 'done' ? '✓' : s === 'failed' ? '✗' : '○'}</span>
                        }
                        {label}
                        {durationNote && <span className={styles.stepDuration}>{durationNote}</span>}
                      </span>
                    );
                  })}
                </div>

                {/* Timer bar */}
                {(phase === 'pipeline' || elapsed > 0) && (
                  <div className={styles.timerBar}>
                    <span className={styles.timerElapsed}>⏱ {fmtSecs(elapsed)}</span>
                    {totalExpected && (
                      <span className={styles.timerExpected}>
                        est. {fmtExpected(totalExpected)} total
                      </span>
                    )}
                    {stepExpected && currentStepName && (
                      <>
                        <span className={styles.timerSep}>·</span>
                        <span className={styles.timerStep}>
                          {currentStepName.replace(/([A-Z])/g, ' $1').trim()}: {fmtExpected(stepExpected)}
                        </span>
                      </>
                    )}
                  </div>
                )}

                {/* Log viewer */}
                <LogViewer lines={lines} isLive={phase === 'pipeline'} />

                {/* Error summary */}
                {buildRecord?.errorSummary && (
                  <div className={styles.errorBanner}>{buildRecord.errorSummary}</div>
                )}

                {/* Cancel — visible while the pipeline is actively running */}
                {phase === 'pipeline' && !pause && (
                  <div className={styles.actions}>
                    <ZestButton onClick={handleCancel}
                      zest={{ buttonStyle: 'outline', semanticType: 'cancel',
                              busyOptions: { handleInternally: true } }}>
                      Cancel Build
                    </ZestButton>
                  </div>
                )}

                {/* Action row */}
                {isImageBuilt && (
                  <div className={styles.deploySection}>
                    <div>
                      <div className={styles.deployInfo}>Images built locally — ready to push to registry</div>
                      <div className={styles.deployTag}>{buildRecord?.gitTag}</div>
                    </div>
                    <ZestButton onClick={handlePush}
                      zest={{ visualOptions: { variant: 'standard' }, busyOptions: { handleInternally: true } }}>
                      Push to Registry
                    </ZestButton>
                  </div>
                )}

                {isPushFailed && (
                  <div className={styles.deploySection}>
                    <span className={styles.deployInfo}>✗ Push to registry failed</span>
                    <ZestButton onClick={handlePush} zest={{ visualOptions: { variant: 'standard' } }}>
                      Retry Push
                    </ZestButton>
                  </div>
                )}

                {isPushSucceeded && !isDeployed && (
                  <div className={styles.deploySection}>
                    <div>
                      <div className={styles.deployInfo}>Pushed to registry — ready to deploy</div>
                      <div className={styles.deployTag}>{buildRecord?.gitTag}</div>
                    </div>
                    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 6 }}>
                      <select
                        value={deployModeOverride}
                        onChange={e => setDeployModeOverride(e.target.value as DeployMode)}
                        style={{ background: '#131D30', color: '#C9D6E3', border: '1px solid rgba(255,255,255,0.12)',
                          borderRadius: 6, padding: '5px 10px', fontSize: 12, width: '100%' }}>
                        <option value="GitScript">Git + Script</option>
                        <option value="GitCompose">Git + Compose</option>
                        <option value="EnvCompose">Env + Compose</option>
                      </select>
                      {deployModeOverride !== defaultDeployMode && (
                        <span style={{ fontSize: 11, color: '#C9943A' }}>
                          Override — project default: {defaultDeployMode}
                        </span>
                      )}
                      <ZestButton onClick={handleDeploy}
                        zest={{ visualOptions: { variant: 'standard' }, busyOptions: { handleInternally: true } }}>
                        Deploy to Production
                      </ZestButton>
                    </div>
                  </div>
                )}

                {isDeployed && buildRecord?.status === 'Deployed' && (
                  <div className={`${styles.deploySection} ${styles.deploySectionSuccess}`}>
                    <div>
                      <div className={styles.deploySuccessHeading}>Deployed successfully</div>
                      <div className={styles.deployTag}>{buildRecord?.gitTag}</div>
                    </div>
                    <ZestButton onClick={handleClose} zest={{ buttonStyle: 'outline' }}>Close</ZestButton>
                  </div>
                )}

                {isDeployed && buildRecord?.status !== 'Deployed' && (
                  <div className={styles.deploySection}>
                    <span className={styles.deployInfo}>✗ Deployment failed</span>
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
                {pause.fields?.map(field => (
                  <div key={field} className={styles.pauseInput}>
                    <label className={styles.pauseLabel}>{field.charAt(0).toUpperCase() + field.slice(1)}</label>
                    <ZestTextbox
                      type={field === 'password' ? 'password' : 'text'}
                      value={pause.fieldValues?.[field] ?? ''}
                      onChange={e => setPause(p => p ? { ...p, fieldValues: { ...(p.fieldValues ?? {}), [field]: e.target.value } } : null)}
                      placeholder={field}
                      zest={{ stretch: true }}
                    />
                  </div>
                ))}
                {pause.checkboxes && (
                  <div className={styles.checkboxList}>
                    {pause.checkboxes.map(img => {
                      const shortName = img.split('/').pop() ?? img;
                      const checked = pause.checkboxValues?.[img] ?? true;
                      return (
                        <label key={img} className={styles.checkboxRow}>
                          <input
                            type="checkbox"
                            checked={checked}
                            onChange={e => setPause(p => p ? {
                              ...p, checkboxValues: { ...(p.checkboxValues ?? {}), [img]: e.target.checked }
                            } : null)}
                          />
                          <span className={styles.checkboxLabel}>
                            <span className={styles.checkboxName}>{shortName}</span>
                            <span className={styles.checkboxHint}>{checked ? 'skip — already built' : 'rebuild'}</span>
                          </span>
                        </label>
                      );
                    })}
                  </div>
                )}
                <OptionPicker
                  options={pickerOptions}
                  value={pause.selected}
                  onChange={selected => setPause(p => p ? { ...p, selected } : null)}
                  onConfirm={handlePauseRespond}
                />
              </div>
            </div>
          )}
        </Drawer.Content>
      </Drawer.Portal>
    </Drawer.Root>
  );
}
