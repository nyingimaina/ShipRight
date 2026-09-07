import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { RiAlertLine } from 'react-icons/ri';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { api } from '@/shared/ApiService';
import { getBrowserTimeZone } from '@/shared/timeZone';
import { IDetectedProjectConfig } from '@/shared/types/IDetectedProject';
import { IProject, IProjectInput, IApiError, IServerConfig, ICredentialResource, IDockerRegistryResource } from '@/shared/types/IProject';
import ProjectIdentityAtom, { IdentityState } from './atoms/ProjectIdentityAtom';
import GitReposAtom from './atoms/GitReposAtom';
import ServicesAtom from './atoms/ServicesAtom';
import WslAtom from './atoms/WslAtom';
import ServerAtom, { ServerDraft } from './atoms/ServerAtom';
import DatabaseAtom, { DatabaseDraft } from './atoms/DatabaseAtom';
import WatchAtom, { WatchDraft } from './atoms/WatchAtom';
import AdvancedAtom from './atoms/AdvancedAtom';
import { ProjectDraft, ServiceDraft, GitRepoDraft } from './atoms/types';
import styles from './Styles/ProjectSetupWizard.module.css';

interface Props {
  existing?: IProject;
  onSaved: (project: IProject) => void;
  onCancel: () => void;
}

type Step = 'type' | 'pick' | 'review' | 'server';

const defaultFreeformFeatures = { docker: true, git: true, deploy: true, database: false };

function featureFallback(existing: IProject) {
  return {
    docker: (existing.services.length ?? 0) > 0,
    git: (existing.gitRepos.length ?? 0) > 0,
    deploy: !!(existing.server.host || existing.server.sshKeyPath),
    database: !!existing.database,
  };
}

function identityFrom(existing: IProject | undefined): IdentityState {
  return {
    name: existing?.name ?? '',
    timeZone: existing?.timeZone ?? getBrowserTimeZone(),
    type: existing?.type === 'Freeform' ? 'Freeform' : 'Pipeline',
    features: existing ? (existing.features ?? featureFallback(existing)) : defaultFreeformFeatures,
  };
}

function draftFrom(existing: IProject | undefined): ProjectDraft {
  return {
    name: existing?.name ?? '',
    timeZone: existing?.timeZone ?? getBrowserTimeZone(),
    type: existing?.type === 'Freeform' ? 'Freeform' : 'Pipeline',
    features: existing ? (existing.features ?? featureFallback(existing)) : defaultFreeformFeatures,
    gitPushTimeoutSeconds: existing?.gitPushTimeoutSeconds ?? 300,
    services: existing
      ? existing.services.map(s => ({ name: s.name, versionFilePath: s.versionFilePath, buildContextPath: s.buildContextPath, dockerImageName: s.dockerImageName, dockerRegistry: s.dockerRegistry ?? '', composeServiceName: s.composeServiceName ?? '', dockerUsername: s.dockerUsername ?? '', dockerPassword: '', imageRetentionCount: s.imageRetentionCount ?? 5, localImageKeepCount: s.localImageKeepCount ?? 2, version: null }))
      : [],
    gitRepos: existing ? existing.gitRepos.map(r => ({ repoPath: r.repoPath, deployBranch: r.deployBranch, pushArgs: r.pushArgs, credentialResourceId: r.credentialResourceId })) : [],
    wslWorkingDir: existing?.wsl.workingDir ?? '',
    server: {
      host: existing?.server.host ?? '',
      username: existing?.server.username ?? 'ubuntu',
      sshKeyPath: existing?.server.sshKeyPath ?? '',
      remoteWorkingDir: existing?.server.remoteWorkingDir ?? '',
      rebuildScript: existing?.server.rebuildScript ?? '',
      deployMode: existing?.server.deployMode ?? 'GitScript',
    },
    serverId: existing?.serverId ?? '',
    databaseEnabled: !!existing?.database,
    database: {
      provider: existing?.database?.provider ?? 'MariaDb',
      containerName: existing?.database?.containerName ?? '',
      databaseName: existing?.database?.databaseName ?? '',
      rootUser: existing?.database?.rootUser ?? 'root',
      backupRetainCount: existing?.database?.backupRetainCount ?? 10,
      rootPassword: existing?.database?.rootPassword ?? '',
    },
    watchBranch: existing?.watchBranch ?? '',
    watchPollSeconds: existing?.watchPollSeconds ?? 300,
    watchSteps: existing?.watchSteps ?? 'Build',
    localCachePruneKeepGb: existing?.localCachePruneKeepGb ?? 5,
  };
}

export default function ProjectSetupWizard({ existing, onSaved, onCancel }: Props) {
  const [step, setStep] = useState<Step>(existing ? 'review' : 'type');
  const [draft, setDraft] = useState<ProjectDraft>(() => draftFrom(existing));
  const [rootPath, setRootPath] = useState(existing?.gitRepos[0]?.repoPath ?? '');
  const [detected, setDetected] = useState<IDetectedProjectConfig | null>(null);
  const [detecting, setDetecting] = useState(false);
  const [detectError, setDetectError] = useState<string | null>(null);

  const [errors, setErrors] = useState<Record<string, string>>({});
  const [globalServers, setGlobalServers] = useState<IServerConfig[]>([]);
  const [credentials, setCredentials] = useState<ICredentialResource[]>([]);
  const [registries, setRegistries] = useState<IDockerRegistryResource[]>([]);
  const [dbRefreshToken, setDbRefreshToken] = useState(0);

  const patch = (p: Partial<ProjectDraft>) => setDraft(prev => ({ ...prev, ...p }));
  const refreshDatabase = () => setDbRefreshToken(t => t + 1);

  // Step 1: detect from root path
  const handleDetect = async () => {
    if (!rootPath.trim()) { setDetectError('Enter your project root directory first.'); return; }
    setDetecting(true);
    setDetectError(null);
    try {
      const result = await api.post<IDetectedProjectConfig>('/api/projects/detect', { rootPath: rootPath.trim() });
      setDetected(result);
      if (result.suggestedName && !draft.name) patch({ name: result.suggestedName });
      patch({
        gitRepos: result.gitRepos.map(r => ({ repoPath: r.repoPath, deployBranch: r.deployBranch, pushArgs: r.pushArgs, credentialResourceId: r.credentialResourceId })),
        wslWorkingDir: result.wslWorkingDir ?? draft.wslWorkingDir,
        services: result.services.map(s => ({
          name: s.suggestedName,
          versionFilePath: s.versionFilePath,
          buildContextPath: s.buildContextPath,
          dockerImageName: s.dockerImageName ?? '',
          dockerRegistry: s.dockerRegistry ?? '',
          composeServiceName: s.composeServiceName ?? '',
          dockerUsername: '',
          dockerPassword: '',
          version: s.version,
        })),
      });
      setStep('review');
    } catch (e: unknown) {
      setDetectError((e as { message?: string })?.message ?? 'Detection failed.');
    } finally {
      setDetecting(false);
    }
  };

  // Fetch global servers + credentials + registries for selectors
  useEffect(() => {
    api.get<IServerConfig[]>('/api/servers').then(setGlobalServers).catch(() => {});
    api.get<ICredentialResource[]>('/api/resources/credentials').then(setCredentials).catch(() => {});
    api.get<IDockerRegistryResource[]>('/api/resources/registries').then(setRegistries).catch(() => {});
  }, []);

  const mapApiErrors = (errs: unknown): Record<string, string> => {
    const list = Array.isArray(errs) ? errs : [errs];
    const mapped: Record<string, string> = {};
    (list as IApiError[]).forEach(e => { if (e.field) mapped[e.field] = e.message; });
    return mapped;
  };

  const persist = async (input: IProjectInput) => {
    const saved = existing
      ? await api.put<IProject>(`/api/projects/${existing.id}`, input)
      : await api.post<IProject>('/api/projects', input);
    onSaved(saved);
  };

  const buildInput = (): IProjectInput => ({
    name: draft.name,
    type: draft.type,
    features: draft.features,
    serverId: draft.serverId || undefined,
    services: draft.services.map(({ version, ...s }) => ({
      ...s,
      dockerRegistry: s.dockerRegistry || undefined,
      dockerRegistryResourceId: s.dockerRegistryResourceId || undefined,
      dockerUsername: s.dockerUsername || undefined,
      dockerPassword: s.dockerPassword || undefined,
      imageRetentionCount: s.imageRetentionCount,
      localImageKeepCount: s.localImageKeepCount,
    })),
    gitRepos: draft.gitRepos.map(r => ({ ...r, pushArgs: r.pushArgs || undefined })),
    wsl: { workingDir: draft.wslWorkingDir },
    server: {
      host: draft.server.host, username: draft.server.username, sshKeyPath: draft.server.sshKeyPath,
      remoteWorkingDir: draft.server.remoteWorkingDir, rebuildScript: draft.server.rebuildScript,
      deployMode: draft.server.deployMode,
    },
    database: draft.databaseEnabled
      ? {
          provider: draft.database.provider,
          containerName: draft.database.containerName,
          databaseName: draft.database.databaseName,
          rootUser: draft.database.rootUser,
          backupRetainCount: draft.database.backupRetainCount,
          rootPassword: draft.database.rootPassword || undefined,
        }
      : undefined,
    timeZone: draft.timeZone,
    watchBranch: draft.watchBranch || undefined,
    watchPollSeconds: draft.watchBranch ? draft.watchPollSeconds : undefined,
    watchSteps: draft.watchBranch ? draft.watchSteps : undefined,
    gitPushTimeoutSeconds: draft.gitPushTimeoutSeconds,
    localCachePruneKeepGb: draft.localCachePruneKeepGb,
  });

  const handleSave = async () => {
    setErrors({});
    try {
      await persist(buildInput());
    } catch (errs: unknown) {
      const apiErrors = mapApiErrors(errs);
      setErrors(apiErrors);
      if (Object.keys(apiErrors).some(k => k.startsWith('server'))) setStep('server');
      if (Object.keys(apiErrors).length === 0) {
        const msg = (errs as IApiError)?.message ?? 'Failed to save project. Please try again.';
        toast.error(msg);
      }
    }
  };

  const handleMinimalSave = async () => {
    setErrors({});
    if (!draft.name.trim()) {
      setErrors({ name: 'Project name is required.' });
      return;
    }
    try {
      await persist({
        name: draft.name,
        type: 'Freeform',
        features: { docker: false, git: false, deploy: false, database: false },
        serverId: '',
        services: [],
        gitRepos: [],
        wsl: { workingDir: '' },
        server: { host: '', username: 'ubuntu', sshKeyPath: '', remoteWorkingDir: '', rebuildScript: '', deployMode: 'GitScript' },
        database: undefined,
        timeZone: draft.timeZone,
      });
    } catch (errs: unknown) {
      const apiErrors = mapApiErrors(errs);
      setErrors(apiErrors);
      if (Object.keys(apiErrors).length === 0) toast.error('Failed to save project.');
    }
  };

  const handleContinue = async () => {
    if (draft.type === 'Pipeline') {
      setStep('server');
    } else if (draft.features.deploy || draft.features.database) {
      setDraft(prev => ({ ...prev, databaseEnabled: prev.features.database }));
      setStep('server');
    } else if (draft.features.docker || draft.features.git) {
      setStep('pick');
    } else {
      await handleMinimalSave();
    }
  };

  const handleApplyRemoteDir = () => refreshDatabase();

  const stepIndex = step === 'server' ? 0 : step === 'pick' ? 1 : 2;

  const databaseDraft: DatabaseDraft = {
    enabled: draft.databaseEnabled,
    provider: draft.database.provider,
    containerName: draft.database.containerName,
    databaseName: draft.database.databaseName,
    rootUser: draft.database.rootUser,
    rootPassword: draft.database.rootPassword,
  };
  const watchDraft: WatchDraft = {
    watchBranch: draft.watchBranch,
    watchPollSeconds: draft.watchPollSeconds,
    watchSteps: draft.watchSteps,
  };

  return (
    <div className={styles.wizard}>
      {step !== 'type' && (
        <div className={styles.steps}>
          {['server', 'pick', 'review'].map((s, i) => (
            <div key={s} style={{ display: 'contents' }}>
              {i > 0 && <div className={styles.stepDotLine} />}
              <div className={`${styles.stepDot} ${i < stepIndex ? styles.stepDotDone : i === stepIndex ? styles.stepDotActive : ''}`} />
            </div>
          ))}
        </div>
      )}

      {/* ── INTRO: Pipeline vs Freeform ── */}
      {step === 'type' && (
        <>
          <div>
            <h2 className={styles.stepTitle}>How do you want to set up this project?</h2>
            <p className={styles.stepSub}>Choose a mode that fits your workflow.</p>
          </div>
          <ProjectIdentityAtom
            state={{ name: draft.name, timeZone: draft.timeZone, type: draft.type, features: draft.features }}
            onChange={patch}
            errors={errors}
            showModeChoice
          />
          <div className={styles.footer}>
            <ZestButton
              zest={{ visualOptions: { variant: 'standard' } }}
              onClick={handleContinue}>
              Continue
            </ZestButton>
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          </div>
        </>
      )}

      {step === 'pick' && (
        <>
          <div>
            <h2 className={styles.stepTitle}>Where is your project?</h2>
            <p className={styles.stepSub}>Navigate to your source code root — ShipRight will auto-detect the rest.</p>
          </div>

          <FilePicker
            initialPath={rootPath || undefined}
            dirsOnly
            storageKey="project-root"
            label="Source code root directory"
            onSelect={path => setRootPath(path)}
          />

          {detectError && <p className={styles.errorText}>{detectError}</p>}

          <div className={styles.footer}>
            <ZestButton onClick={() => setStep('server')} zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>
            <ZestButton onClick={handleDetect}
              zest={{ visualOptions: { variant: 'standard' }, busyOptions: { handleInternally: true } }}>
              {detecting ? 'Detecting…' : 'Detect & Continue'}
            </ZestButton>
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          </div>
        </>
      )}

      {/* ── REVIEW: detected config + atoms ── */}
      {step === 'review' && (
        <>
          <div>
            <h2 className={styles.stepTitle}>Review detected config</h2>
            <p className={styles.stepSub}>Fields marked in gold need your input.</p>
          </div>

          <ProjectIdentityAtom
            state={{ name: draft.name, timeZone: draft.timeZone, type: draft.type, features: draft.features }}
            onChange={patch}
            errors={errors}
            showModeChoice={false}
          />

          {detected && detected.detected.length > 0 && (
            <div className={styles.section}>
              <span className={styles.sectionTitle}>Auto-detected</span>
              <div className={styles.detectedList}>
                {detected.detected.map((d, i) => (
                  <div key={i} className={styles.detectedChip}>
                    <RiAlertLine className={styles.detectedIcon} />
                    <span className={styles.detectedText}>{d}</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          <GitReposAtom
            repos={draft.gitRepos}
            credentials={credentials}
            errors={errors}
            onReposChange={gitRepos => patch({ gitRepos })}
          />

          <ServicesAtom
            services={draft.services}
            composeNames={detected ? Array.from(new Set(detected.services.map(s => s.composeServiceName).filter(Boolean) as string[])) : []}
            registries={registries}
            errors={errors}
            onServicesChange={services => patch({ services })}
            onRegistriesChange={setRegistries}
          />

          <WslAtom
            workingDir={draft.wslWorkingDir}
            errors={errors}
            onWorkingDirChange={value => patch({ wslWorkingDir: value })}
          />

          <WatchAtom draft={watchDraft} onDraftChange={p => patch(p as Partial<ProjectDraft>)} />
          <AdvancedAtom
            gitPushTimeoutSeconds={draft.gitPushTimeoutSeconds}
            onGitPushTimeoutChange={value => patch({ gitPushTimeoutSeconds: value })}
          />

          <div className={styles.footer}>
            {!existing && (
              <ZestButton onClick={() => setStep(draft.type === 'Freeform' && !draft.features.deploy ? 'type' : 'server')}
                zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>
            )}
            {existing
              ? <>
                  <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>Save</ZestButton>
                  <ZestButton onClick={() => setStep('server')} zest={{ buttonStyle: 'outline' }}>Server config →</ZestButton>
                </>
              : <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>Create Project</ZestButton>
            }
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          </div>
        </>
      )}

      {/* ── SERVER: server + database atoms ── */}
      {step === 'server' && (
        <>
          <div>
            <h2 className={styles.stepTitle}>Server</h2>
            <p className={styles.stepSub}>Where does this project deploy?</p>
          </div>

          {!existing && (
            <ProjectIdentityAtom
              state={{ name: draft.name, timeZone: draft.timeZone, type: draft.type, features: draft.features }}
              onChange={patch}
              errors={errors}
              showModeChoice={false}
            />
          )}

          <ServerAtom
            draft={draft.server}
            serverId={draft.serverId}
            globalServers={globalServers}
            errors={errors}
            onDraftChange={p => patch({ server: { ...draft.server, ...p } })}
            onServerIdChange={id => patch({ serverId: id })}
            onBrowseRemoteDir={handleApplyRemoteDir}
          />

          <DatabaseAtom
            draft={databaseDraft}
            existingId={existing?.id}
            serverDraft={{ host: draft.server.host, username: draft.server.username, sshKeyPath: draft.server.sshKeyPath }}
            refreshToken={dbRefreshToken}
            onDraftChange={p => {
              if ('enabled' in p) patch({ databaseEnabled: !!p.enabled });
              const dbPatch = { ...p };
              delete dbPatch.enabled;
              patch({ database: { ...draft.database, ...dbPatch } });
            }}
          />

          {Object.keys(errors).some(k => !k.startsWith('server')) && (
            <div className={styles.warningBox}>
              <RiAlertLine /> Some fields on the previous step also have errors — go back to fix them.
            </div>
          )}

          <div className={styles.footer}>
            {existing && <ZestButton onClick={() => setStep('review')} zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>}
            {!existing && <ZestButton onClick={() => setStep('type')} zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>}
            {existing
              ? <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>Save Changes</ZestButton>
              : draft.type === 'Freeform' && !draft.features.docker && !draft.features.git
                ? <ZestButton onClick={handleSave} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>Create Project</ZestButton>
                : <ZestButton onClick={() => setStep('pick')} zest={{ visualOptions: { variant: 'standard' } }}>Source code →</ZestButton>
            }
            <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          </div>
        </>
      )}
    </div>
  );
}
