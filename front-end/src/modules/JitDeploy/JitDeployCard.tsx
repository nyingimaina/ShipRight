import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import HoverTip from '@/shared/components/HoverTip';
import styles from './Styles/JitDeploy.module.css';
import StreamLog from '@/shared/components/StreamLog/StreamLog';
import { api, sseUrl } from '@/shared/ApiService';
import {
  DEFAULT_JIT_CONFIG, JitConfig, JitConfigResponse, JitDependents, JitDryRun, JitReleases, JitServiceHint,
  describeAge, phaseSummary, setDependentChoice, shortSha, validateJitConfig,
} from './jitDeploy';
import JitGitAccess from './JitGitAccess';
import JitCredentialPicker, { isHttps } from './JitCredentialPicker';
import JitComposeServices, { ServiceHelp } from './JitComposeServices';
import {
  addressOptions, addressWarning, branchAfterPick, JitHintsResponse, JitStacksResponse, prefillPatch,
} from './jitHints';
import { describeEnv, envKeys, envSummary } from './envVars';
import { DuplicateChoice, EnvDuplicate, EnvDuplicatesResponse } from './envDuplicates';
import EnvDivergenceWarning from './EnvDivergenceWarning';
import { envFileWarnings } from '@/modules/ServerDeploy/envFileWarnings';
import { stepText } from './jitSteps';

interface Props {
  projectId: string;
}

type OpStatus = 'idle' | 'running' | 'success' | 'error';

/** This card tracks its own busy state, so ZestButton must not also lock itself after a click. */
const NOBUSY = { busyOptions: { handleInternally: false } };


function Field(props: {
  id: string; label: string; tipLabel?: string; tip: string; error?: string; children: React.ReactNode;
}) {
  return (
    <div className={styles.row}>
      <div>
        <label htmlFor={props.id}>{props.label}</label>
        <HoverTip label={props.tipLabel ?? props.label}>{props.tip}</HoverTip>
      </div>
      {props.children}
      {props.error && <span className={styles.err}>{props.error}</span>}
    </div>
  );
}

export default function JitDeployCard({ projectId }: Props) {
  const [loaded, setLoaded] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [cfg, setCfg] = useState<JitConfig>(DEFAULT_JIT_CONFIG);
  const [saved, setSaved] = useState<JitConfig>(DEFAULT_JIT_CONFIG);
  const [services, setServices] = useState<JitServiceHint[]>([]);
  const [problems, setProblems] = useState<string[]>([]);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [advanced, setAdvanced] = useState(false);
  const [saving, setSaving] = useState(false);

  // What ShipRight already knows (offered instead of asked). All of it is optional: the card works without it.
  const [hints, setHints] = useState<JitHintsResponse | null>(null);
  const [stacks, setStacks] = useState<JitStacksResponse | null>(null);
  const [serviceHelp, setServiceHelp] = useState<ServiceHelp | null>(null);
  const prefillPending = useRef(false);
  const savedRef = useRef<JitConfig>(DEFAULT_JIT_CONFIG);

  const [releases, setReleases] = useState<JitReleases | null>(null);
  const [dependents, setDependents] = useState<JitDependents | null>(null);

  const [force, setForce] = useState(false);
  const [rebuildAll, setRebuildAll] = useState(false);
  const [commit, setCommit] = useState('');
  const [confirmTag, setConfirmTag] = useState<string | null>(null);
  const [confirmRestart, setConfirmRestart] = useState(false);
  const [pendingDeploy, setPendingDeploy] = useState<{ path: string; body: unknown } | null>(null);
  const [duplicates, setDuplicates] = useState<EnvDuplicate[]>([]);
  const kindRef = useRef<'deploy' | 'restart'>('deploy');

  const [op, setOp] = useState<{ status: OpStatus; logs: string[]; message: string }>({ status: 'idle', logs: [], message: '' });
  const [steps, setSteps] = useState<string[]>([]);
  const [dryRun, setDryRun] = useState<JitDryRun | null>(null);
  const [dryRunning, setDryRunning] = useState(false);
  const [attempt, setAttempt] = useState<{ message: string; url: string | null } | null>(null);
  const [diagnosticsUrl, setDiagnosticsUrl] = useState<string | null>(null);
  const esRef = useRef<EventSource | null>(null);

  const base = `/api/jit/${projectId}`;
  const dirty = JSON.stringify(cfg) !== JSON.stringify(saved);
  const envWarnings = useMemo(() => envFileWarnings(cfg.envText), [cfg.envText]);

  useEffect(() => () => { esRef.current?.close(); }, []);

  const loadServer = useCallback(async () => {
    const [rel, dep] = await Promise.all([
      api.get<JitReleases>(`${base}/releases`).catch((e: { message?: string }) =>
        ({ phase: 'Unknown', currentTag: null, previousTag: null, needsAttention: false, attention: null, releases: [], error: e?.message ?? 'Could not load the releases.' } as JitReleases)),
      api.get<JitDependents>(`${base}/dependents`).catch(() => null),
    ]);
    setReleases(rel);
    setDependents(dep);
  }, [base]);

  const loadConfig = useCallback(async () => {
    const res = await api.get<JitConfigResponse>(`${base}/config`);
    const c = { ...DEFAULT_JIT_CONFIG, ...res.config, dependentChoices: res.config.dependentChoices ?? [] };
    setCfg(c);
    setSaved(c);
    setServices(res.services ?? []);
    setProblems(res.problems ?? []);
    setServiceHelp((res as { serviceHelp?: ServiceHelp | null }).serviceHelp ?? null);
    return c;
  }, [base]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const c = await loadConfig();
        if (!cancelled) setLoaded(true);
        if (c.enabled && !cancelled) await loadServer();
      } catch {
        // The server does not offer this (cloud mode, an older server): stay out of the way, silently.
        if (!cancelled) { setUnavailable(true); setLoaded(true); }
      }
    })();
    return () => { cancelled = true; };
  }, [loadConfig, loadServer]);

  savedRef.current = saved;

  // Only looked up once building on the server is switched on, so a project that is off is left completely alone.
  useEffect(() => {
    if (!loaded || unavailable || !cfg.enabled) return;
    let cancelled = false;
    api.get<JitHintsResponse>(`${base}/hints`)
      .then(h => { if (!cancelled) setHints(h); })
      .catch(() => { if (!cancelled) setHints(null); });
    return () => { cancelled = true; };
  }, [loaded, unavailable, cfg.enabled, base]);

  // Right after the person switches it on, fill the empty fields from what is known (as soon as it is known).
  useEffect(() => {
    if (!prefillPending.current || !hints) return;
    prefillPending.current = false;
    setCfg(c => ({ ...c, ...prefillPatch(c, savedRef.current, hints) }));
  }, [hints, cfg.enabled]);

  useEffect(() => {
    if (!loaded || unavailable || !saved.enabled) { setStacks(null); return; }
    let cancelled = false;
    api.get<JitStacksResponse>(`${base}/stacks`)
      .then(r => { if (!cancelled) setStacks(r); })
      .catch(() => { if (!cancelled) setStacks(null); });
    return () => { cancelled = true; };
  }, [loaded, unavailable, saved.enabled, saved.composeProject, base]);

  const toggleEnabled = (on: boolean) => {
    if (on && !cfg.enabled) prefillPending.current = true;
    set('enabled', on);
  };

  const pickAddress = (value: string) => {
    const picked = addressOptions(hints).find(o => o.value === value);
    setCfg(c => ({ ...c, repoUrl: value, credentialResourceId: isHttps(value) ? c.credentialResourceId : null, ...branchAfterPick(c, picked) }));
  };

  const set = <K extends keyof JitConfig>(key: K, value: JitConfig[K]) => setCfg(c => ({ ...c, [key]: value }));
  const num = (key: 'releasesToKeep' | 'stopGraceSeconds' | 'healthTimeoutSeconds' | 'settleSeconds' | 'minFreeDiskMb') =>
    (e: React.ChangeEvent<HTMLInputElement>) => set(key, e.target.value === '' ? Number.NaN : Number(e.target.value));

  const save = async () => {
    const found = validateJitConfig(cfg);
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    setSaving(true);
    try {
      await api.put(`${base}/config`, cfg);
      toast.success(cfg.enabled ? 'Saved. This project can now be built on its server.' : 'Saved. Building on the server is off.');
      const c = await loadConfig();
      if (c.enabled) await loadServer(); else { setReleases(null); setDependents(null); }
    } catch (e) {
      toast.error((e as { message?: string })?.message ?? 'Could not save the settings.');
    } finally {
      setSaving(false);
    }
  };

  // ── running an operation and watching it live ──────────────────────────────

  const stream = (buildId: string) => {
    const es = new EventSource(sseUrl(`/api/builds/${buildId}/stream`));
    esRef.current = es;
    es.onmessage = (event) => {
      try {
        const { type, data } = JSON.parse(event.data) as { type: string; data: Record<string, string> };
        if (type === 'LogLine') {
          setOp(o => ({ ...o, logs: [...o.logs, data.line] }));
          const done = stepText(data.line);
          if (done) {
            setSteps(prev => [...prev, done]);
            toast.success(done, { id: 'jit-step' });   // one notification that always shows the latest finished step
          }
        } else if (type === 'DeployCompleted') {
          es.close();
          const ok = data.status === 'Deployed';
          api.get<{ errorSummary?: string | null }>(`/api/builds/${buildId}`)
            .then(b => b?.errorSummary ?? null).catch(() => null)
            .then(summary => {
              const message = ok ? (kindRef.current === 'restart' ? 'Every service was restarted.' : 'Deployed successfully.') : (summary ?? 'It did not finish. See the log above.');
              setOp(o => ({ ...o, status: ok ? 'success' : 'error', message }));
              if (ok) toast.success(message, { id: 'jit-step' }); else toast.error(message, { id: 'jit-step' });
            });
          loadServer();
        }
      } catch { /* ignore a malformed event */ }
    };
    es.onerror = () => {
      if (es.readyState === EventSource.CLOSED)
        setOp(o => ({ ...o, status: 'error', message: 'Lost the live connection to the server. It may still be working: check the releases below in a moment.' }));
    };
  };

  const start = async (path: string, body: unknown) => {
    esRef.current?.close();
    setConfirmTag(null);
    setConfirmRestart(false);
    kindRef.current = path.endsWith('/restart-all') ? 'restart' : 'deploy';
    setOp({ status: 'running', logs: [], message: '' });
    setSteps([]);
    setDryRun(null);
    setAttempt(null);
    setDiagnosticsUrl(null);
    try {
      const res = await api.post<{ buildId: string; diagnosticsUrl?: string | null }>(path, body);
      setDiagnosticsUrl(res.diagnosticsUrl ?? null);
      stream(res.buildId);
    } catch (e) {
      const err = e as { message?: string; diagnosticsUrl?: string | null };
      const message = err?.message ?? 'Could not start.';
      setOp({ status: 'idle', logs: [], message: '' });
      setAttempt({ message, url: err?.diagnosticsUrl ?? null });
      if (err?.diagnosticsUrl) setDiagnosticsUrl(err.diagnosticsUrl);
      toast.error(message);
    }
  };

  /** Asks the server what it would do, without it doing any of it. Nothing on the target is written. */
  const runDryRun = async () => {
    esRef.current?.close();
    setConfirmTag(null);
    setConfirmRestart(false);
    setOp({ status: 'idle', logs: [], message: '' });
    setSteps([]);
    setDryRun(null);
    setAttempt(null);
    setDiagnosticsUrl(null);
    setDryRunning(true);
    try {
      const res = await api.post<JitDryRun>(`${base}/dry-run`, {
        force, commit: commit.trim() || null, allowWithoutRollback: false, rebuildAll,
      });
      setDryRun(res);
      setDiagnosticsUrl(res.diagnosticsUrl ?? null);
    } catch (e) {
      const err = e as { message?: string; diagnosticsUrl?: string | null };
      const message = err?.message ?? 'Could not run the dry run.';
      setAttempt({ message, url: err?.diagnosticsUrl ?? null });
      if (err?.diagnosticsUrl) setDiagnosticsUrl(err.diagnosticsUrl);
      toast.error(message);
    } finally {
      setDryRunning(false);
    }
  };

  // A deploy first asks the server which variables are set on both sides. An older server (or one where the check
  // fails) simply carries on as before, so this never blocks a deploy that used to work.
  const askDeploy = async (path: string, body: unknown) => {
    try {
      const found = await api.get<EnvDuplicatesResponse>(`${base}/env-duplicates`);
      if (!found.skipped && found.duplicates.length > 0) {
        setDuplicates(found.duplicates);
        setPendingDeploy({ path, body });
        return;
      }
    } catch { /* check unavailable: deploy exactly as before */ }
    start(path, body);
  };

  const resolveDuplicates = async (choice: DuplicateChoice) => {
    const pending = pendingDeploy;
    if (!pending) return;
    if (choice !== 'none') {
      try {
        await api.post(`${base}/env-duplicates/remove`, { removeFrom: choice, names: duplicates.map(d => d.name) });
        if (choice === 'project') await loadConfig();
        else await api.get<JitHintsResponse>(`${base}/hints`).then(setHints).catch(() => undefined);
      } catch (e) {
        toast.error((e as { message?: string })?.message ?? 'Could not remove those variables.');
        return;
      }
    }
    setDuplicates([]);
    setPendingDeploy(null);
    start(pending.path, pending.body);
  };

  const logLines = useMemo(() => op.logs.map((text, id) => ({ id, text })), [op.logs]);
  const running = op.status === 'running';
  const needsAttention = releases?.needsAttention === true;
  const canDeploy = saved.enabled && !dirty && !running && !needsAttention;
  const canRestart = saved.enabled && !running && !needsAttention && !!releases?.currentTag;
  const canDryRun = saved.enabled && !dirty && !running && !dryRunning;
  const effectiveRestart = (service: string, dependent: string, fallback: boolean) =>
    cfg.dependentChoices.find(c => c.service === service && c.dependent === dependent)?.restart ?? fallback;

  if (!loaded || unavailable) return null;

  const ownServices = services.length > 0 && cfg.composeServices.length === 0;
  const stackWarnings = stacks?.warnings ?? [];

  return (
    <div className={styles.card}>
      <label className={styles.toggle}>
        <input
          type="checkbox"
          aria-label="Build on the server instead of pushing to a registry"
          checked={cfg.enabled}
          onChange={e => toggleEnabled(e.target.checked)}
          className={styles.checkbox}
        />
        <span>Build on the server instead of pushing to a registry</span>
      </label>

      {cfg.enabled && (
        <>
          <EnvDivergenceWarning base={base} />
          <Field id="jit-repo" label="Git address" error={errors.repoUrl}
            tip={'The address the server uses to download your code.\nOn GitHub: open your repository, click the green Code button, choose SSH and copy the address.\nThe server needs permission to read it (a deploy key).'}>
            <>
              {addressOptions(hints).length > 0 && (
                <select aria-label="Addresses ShipRight already knows" className={styles.input} value="" onChange={e => { if (e.target.value) pickAddress(e.target.value); }}>
                  <option value="">Pick an address ShipRight already knows…</option>
                  {addressOptions(hints).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              )}
              <input id="jit-repo" className={styles.input} value={cfg.repoUrl} placeholder="git@github.com:you/your-app.git"
                onChange={e => { const v = e.target.value; setCfg(c => ({ ...c, repoUrl: v, credentialResourceId: isHttps(v) ? c.credentialResourceId : null })); }} />
              {addressWarning(cfg.repoUrl) && <span className={styles.err}>{addressWarning(cfg.repoUrl)}</span>}
              {hints?.notes.map(n => <span key={n} className={styles.muted}>{n}</span>)}
            </>
          </Field>

          <Field id="jit-branch" label="Branch" error={errors.branch}
            tip={'Which branch to deploy. Whatever is newest on this branch when you press Deploy is what goes live. Usually master or main.'}>
            <input id="jit-branch" className={styles.input} value={cfg.branch} onChange={e => set('branch', e.target.value)} />
          </Field>

          <JitCredentialPicker base={base} repoUrl={cfg.repoUrl} value={cfg.credentialResourceId}
            onChange={id => setCfg(c => ({ ...c, credentialResourceId: id, useDeployKey: id ? false : c.useDeployKey }))} />
          <JitGitAccess base={base} repoUrl={cfg.repoUrl} useDeployKey={cfg.useDeployKey} credentialId={cfg.credentialResourceId}
            onUseDeployKey={on => setCfg(c => ({ ...c, useDeployKey: on, credentialResourceId: on ? null : c.credentialResourceId }))} />

          <Field id="jit-compose" label="Compose file in your repository" tipLabel="Compose file in your repository" error={errors.composeFile}
            tip={'Pick your docker-compose file. ShipRight finds the services in it and the Dockerfile of each, and adds the build instructions and the image tags itself: your compose file needs no changes.'}>
            <>
              <input id="jit-compose" className={styles.input} value={cfg.composeFile} placeholder="Use Browse to choose your compose file" readOnly />
              <JitComposeServices base={base} needsServices={serviceHelp !== null || cfg.composeServices.length > 0} current={cfg.composeServices} help={serviceHelp}
                onApply={(file, picked) => setCfg(c => ({ ...c, composeFile: file, ...(picked ? { composeServices: picked } : {}) }))}
                onClear={() => set('composeServices', [])}
                onUpdate={(name, patch) => setCfg(c => ({ ...c, composeServices: c.composeServices.map(s => (s.name === name ? { ...s, ...patch } : s)) }))} />
              {errors.composeServices && <span className={styles.err}>{errors.composeServices}</span>}
            </>
          </Field>

          {stackWarnings.length > 0 && (
            <div className={styles.mt10}>
              {stackWarnings.map(w => <p key={w} className={`${styles.muted} ${styles.warn}`}>{w}</p>)}
              {stacks?.suggestedName && cfg.composeProject !== stacks.suggestedName && (
                <ZestButton onClick={() => set('composeProject', stacks.suggestedName!)} zest={{ ...NOBUSY, semanticType: 'edit', buttonStyle: 'outline' }}>Take over &apos;{stacks.suggestedName}&apos;</ZestButton>
              )}
            </div>
          )}

          {ownServices && (
            <div className={styles.mt14}>
              <strong>What your compose file needs</strong>
              <p className={styles.muted}>
                For every service ShipRight builds, your docker-compose.yml needs a build: section and exactly this image: line,
                so each release gets its own tag and can be rolled back.
              </p>
              <ul className={styles.list}>
                {services.map(s => (
                  <li key={s.name} className={styles.item}>
                    <span>{s.name}:</span>
                    <code>{s.composeImageLine}</code>
                    <ZestButton aria-label={`Copy the image line for ${s.name}`}
                      onClick={() => navigator.clipboard.writeText(`image: ${s.composeImageLine}`)}
                      zest={{ ...NOBUSY, semanticType: 'copy', buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Copy</ZestButton>
                  </li>
                ))}
              </ul>
            </div>
          )}

          <Field id="jit-envtext" label="Variables for compose (optional)" tipLabel="Variables for compose" error={errors.envText}
            tip={'The private settings your compose file reads, such as passwords and keys, written one per line as NAME=value. ShipRight writes them to a protected file on the server before every deploy.\n\nThe settings of the server this project deploys to already supply their own variables. Anything you write here replaces the server\'s value of the same name, and the rest of the server\'s variables still apply.\n\nWhere to get them: your repository usually has a .env.example listing every setting. Never share these values.'}>
            <>
              <textarea id="jit-envtext" className={styles.textarea} rows={6} spellCheck={false}
                placeholder={'NAME=value\nANOTHER=value'} value={cfg.envText} onChange={e => set('envText', e.target.value)} />
              <span className={styles.muted}>
                {cfg.envFile.trim() !== ''
                  ? 'Not used because an env file on the server is named under advanced settings.'
                  : describeEnv(envSummary(hints?.envFile, cfg.envText))}
              </span>
              {cfg.envFile.trim() === '' && envKeys(hints?.envFile).length > 0 && (
                <span className={styles.muted}>From the server settings: {envKeys(hints?.envFile).join(', ')}</span>
              )}
              {cfg.envFile.trim() === '' && envWarnings.length > 0 && (
                <div className={styles.envWarn} role="status">
                  <strong>Check these values — a character may be read as something else:</strong>
                  <ul>{envWarnings.map((w, i) => <li key={i}>{w.message}</li>)}</ul>
                </div>
              )}
            </>
          </Field>

          <div className={styles.mt12}>
            <ZestButton onClick={() => setAdvanced(a => !a)} zest={{ ...NOBUSY, semanticType: 'settings', buttonStyle: 'text' }}>
              {advanced ? 'Hide advanced settings' : 'Show advanced settings'}
            </ZestButton>
          </div>
          {advanced && (
            <div>
              <Field id="jit-env" label="Env file on the server (optional)" tipLabel="Env file on the server" error={errors.envFile}
                tip={'Only if you keep and manage an env file on the server yourself (and not in git): give its full path, for example /home/ubuntu/shop/.env. ShipRight uses that file exactly as it is and does not create or change it. The variables above are then not used. Leave empty to let ShipRight write the file for you.'}>
                <input id="jit-env" className={styles.input} value={cfg.envFile} onChange={e => set('envFile', e.target.value)} />
              </Field>
              <Field id="jit-project" label="Compose project name (optional)" tipLabel="Compose project name" error={errors.composeProject}
                tip={'The name docker uses to group this app\'s containers. Leave it empty and one is made from your project name. Only change it to match an app that is already running under a different name.'}>
                <input id="jit-project" className={styles.input} value={cfg.composeProject} placeholder={hints?.composeProject}
                  onChange={e => set('composeProject', e.target.value)} />
              </Field>
              <Field id="jit-keep" label="Releases to keep" error={errors.releasesToKeep}
                tip={'How many past releases stay on the server so you can go back to them. Never fewer than 2. Each one uses disk space.'}>
                <input id="jit-keep" type="number" className={styles.input} value={Number.isNaN(cfg.releasesToKeep) ? '' : cfg.releasesToKeep} onChange={num('releasesToKeep')} />
              </Field>
              <Field id="jit-grace" label="Stop grace period (seconds)" tipLabel="Stop grace period" error={errors.stopGraceSeconds}
                tip={'How long a running service gets to finish what it is doing when it is told to stop, before it is forced to. Raise it if your app needs longer to shut down cleanly.'}>
                <input id="jit-grace" type="number" className={styles.input} value={Number.isNaN(cfg.stopGraceSeconds) ? '' : cfg.stopGraceSeconds} onChange={num('stopGraceSeconds')} />
              </Field>
              <Field id="jit-health" label="Health check timeout (seconds)" tipLabel="Health check timeout" error={errors.healthTimeoutSeconds}
                tip={'How long a new service has to report that it is healthy before the deploy is judged a failure and rolled back. Raise it for apps that start slowly.'}>
                <input id="jit-health" type="number" className={styles.input} value={Number.isNaN(cfg.healthTimeoutSeconds) ? '' : cfg.healthTimeoutSeconds} onChange={num('healthTimeoutSeconds')} />
              </Field>
              <Field id="jit-settle" label="Settle time (seconds)" tipLabel="Settle time" error={errors.settleSeconds}
                tip={'For a service with no health check: how long it must stay up, without restarting, to count as healthy.'}>
                <input id="jit-settle" type="number" className={styles.input} value={Number.isNaN(cfg.settleSeconds) ? '' : cfg.settleSeconds} onChange={num('settleSeconds')} />
              </Field>
              <Field id="jit-disk" label="Minimum free disk space (MB)" tipLabel="Minimum free disk space" error={errors.minFreeDiskMb}
                tip={'A deploy refuses to start if the server has less free space than this, because building images needs room.'}>
                <input id="jit-disk" type="number" className={styles.input} value={Number.isNaN(cfg.minFreeDiskMb) ? '' : cfg.minFreeDiskMb} onChange={num('minFreeDiskMb')} />
              </Field>
              <Field id="jit-nice" label="Build priority (0-19, optional)" tipLabel="Build priority" error={errors.buildNice}
                tip={'Higher numbers make the build politer to the services that are running while it builds. Leave empty for normal priority.'}>
                <input id="jit-nice" type="number" className={styles.input} value={cfg.buildNice ?? ''}
                  onChange={e => set('buildNice', e.target.value === '' ? null : Number(e.target.value))} />
              </Field>

              {dependents && dependents.pairs.length > 0 && (
                <div className={styles.mt14}>
                  <strong>When a service is rebuilt, which others restart with it?</strong>
                  {dependents.pairs.map(p => (
                    <label key={`${p.service}>${p.dependent}`} className={styles.check}>
                      <input type="checkbox"
                        aria-label={`Restart ${p.dependent} when ${p.service} is replaced`}
                        checked={effectiveRestart(p.service, p.dependent, p.restart)}
                        onChange={e => set('dependentChoices', setDependentChoice(cfg.dependentChoices, p.service, p.dependent, e.target.checked))}
                        className={styles.checkbox} />
                      <span>Restart <b>{p.dependent}</b> when <b>{p.service}</b> is replaced</span>
                    </label>
                  ))}
                </div>
              )}

              <div className={styles.options}>
                <label className={styles.inline}>
                  <input type="checkbox" aria-label="Deploy even if nothing has changed" checked={force} onChange={e => setForce(e.target.checked)} />
                  <span>Deploy even if nothing has changed</span>
                </label>
                <label className={styles.inline}>
                  <input type="checkbox" aria-label="Rebuild every image, even if its files have not changed" checked={rebuildAll} onChange={e => setRebuildAll(e.target.checked)} />
                  <span>Rebuild every image, even if its files have not changed</span>
                  <HoverTip label="Rebuild every image">
                    {'ShipRight works out which images have exactly the same inputs as the running release. For now it only reports this in the log and still builds everything; once reuse is switched on, an unchanged image is reused instead of rebuilt. Tick this to build every image anyway, for example to pick up a base image security update.'}
                  </HoverTip>
                </label>
                <label className={styles.inline}>
                  <span>Deploy this exact commit (optional)</span>
                  <input aria-label="Deploy this exact commit (optional)" className={`${styles.input} ${styles.narrow}`} value={commit}
                    placeholder="ab8b4a9" onChange={e => setCommit(e.target.value)} />
                </label>
              </div>
            </div>
          )}
        </>
      )}

      {problems.length > 0 && (
        <ul className={styles.warnings}>
          {problems.map(p => <li key={p}>{p}</li>)}
        </ul>
      )}

      <div className={styles.saveRow}>
        <ZestButton disabled={saving} onClick={save} zest={{ ...NOBUSY, semanticType: 'save' }}>Save settings</ZestButton>
        {dirty && <span className={styles.muted}>You have unsaved changes.</span>}
      </div>

      {saved.enabled && (
        <div className={styles.section}>
          {releases && !releases.error && phaseSummary(releases.phase).tone !== 'ok' && (
            <p className={`${styles.phase} ${styles[phaseSummary(releases.phase).tone]}`}>
              Server status: {phaseSummary(releases.phase).label}
            </p>
          )}

          {needsAttention && (
            <div className={styles.confirmBad}>
              <div>{releases?.attention}</div>
              <ZestButton disabled={running} onClick={() => start(`${base}/recover`, {})}
                zest={{ ...NOBUSY, semanticType: 'undo', buttonStyle: 'outline' }}>Recover</ZestButton>
            </div>
          )}

          <div className={styles.actions}>
            <ZestButton disabled={!canDeploy} zest={{ ...NOBUSY, semanticType: 'send' }} onClick={() =>
              askDeploy(`${base}/deploy`, { force, commit: commit.trim() || null, allowWithoutRollback: false, rebuildAll })}>Deploy now</ZestButton>
            <ZestButton disabled={!canDryRun} onClick={runDryRun}
              zest={{ ...NOBUSY, semanticType: 'view', buttonStyle: 'outline' }}>Dry run</ZestButton>
            <ZestButton disabled={!canRestart} onClick={() => { setConfirmTag(null); setConfirmRestart(true); }}
              zest={{ ...NOBUSY, semanticType: 'refresh', buttonStyle: 'outline' }}>Restart all services</ZestButton>
            {dirty && <span className={styles.muted}>Save your settings before deploying.</span>}
            {dryRunning && <span className={styles.muted}>Checking what a deploy would do. Nothing on the server is being changed.</span>}
          </div>

          {attempt && (
            <div className={styles.confirmBad} role="alert">
              <div>{attempt.message}</div>
              {attempt.url && !dryRun && (
                <p className={styles.mt8}>
                  <a className={styles.diagnosticLink} href={attempt.url} download>Download diagnostics</a>
                </p>
              )}
            </div>
          )}

          {dryRun && (
            <div className={styles.plan} role="region" aria-label="Dry run plan">
              <strong>{dryRun.ok ? 'Dry run: this deploy can start' : 'Dry run: this deploy would not start'}</strong>
              <p className={styles.muted}>
                Nothing on the server was changed.{' '}
                {dryRun.targetChecked ? 'The server was read with read-only commands only.' : 'The server was not contacted.'}
              </p>
              {dryRun.guardSummary && <p className={styles.muted}>{dryRun.guardSummary}</p>}
              {dryRun.error && <p role="alert" className={`${styles.message} ${styles.bad}`}>{dryRun.error}</p>}
              <div className={styles.planFacts}>
                {dryRun.commit && <span>Commit <code>{shortSha(dryRun.commit)}</code></span>}
                {dryRun.tag && <span>Release <b>{dryRun.tag}</b></span>}
                {dryRun.services.length > 0 && <span>Services {dryRun.services.join(', ')}</span>}
                {dryRun.currentTag && <span>Running now {dryRun.currentTag}</span>}
              </div>
              {dryRun.warnings.length > 0 && (
                <ul className={styles.warnings}>
                  {dryRun.warnings.map((w, i) => <li key={i}>{w}</li>)}
                </ul>
              )}
              {dryRun.problems.length > 0 && (
                <ul className={`${styles.warnings} ${styles.bad}`}>
                  {dryRun.problems.map((p, i) => <li key={i}>{p}</li>)}
                </ul>
              )}
              {dryRun.commands.length > 0 && (
                <details className={styles.mt10}>
                  <summary>What a real deploy would run ({dryRun.commands.length} commands)</summary>
                  <ol className={styles.planCommands}>
                    {dryRun.commands.map((c, i) => (
                      <li key={i}><b>{c.step}</b><code>{c.command}</code></li>
                    ))}
                  </ol>
                </details>
              )}
              <div className={styles.planActions}>
                {dryRun.diagnosticsUrl && <a className={styles.diagnosticLink} href={dryRun.diagnosticsUrl} download>Download diagnostics</a>}
                <ZestButton onClick={() => setDryRun(null)} zest={{ ...NOBUSY, semanticType: 'close', buttonStyle: 'text' }}>Hide</ZestButton>
              </div>
            </div>
          )}

          {op.status !== 'idle' && (
            <div className={styles.mt12}>
              {steps.length > 0 && (
                <ol aria-label="Progress" className={styles.steps}>
                  {steps.map((text, i) => <li key={i} className={styles.step}>{text}</li>)}
                </ol>
              )}
              <div role="log" aria-label="Deploy log" className={`${styles.log} ${styles.mt12}`}>
                <StreamLog lines={logLines} isLive={running} filename="deploy.log" emptyText="Starting…" />
              </div>
              {op.status === 'success' && <p className={`${styles.message} ${styles.ok}`}>{op.message}</p>}
              {op.status === 'success' && kindRef.current === 'deploy' && canRestart && (
                <p className={styles.muted}>
                  Want a completely fresh start? That stops every service and starts them all again.{' '}
                  <ZestButton onClick={() => setConfirmRestart(true)} zest={{ ...NOBUSY, semanticType: 'refresh', buttonStyle: 'text' }}>Restart everything now</ZestButton>
                </p>
              )}
              {op.status === 'error' && <p role="alert" className={`${styles.message} ${styles.bad}`}>{op.message}</p>}
              {diagnosticsUrl && (
                <p className={styles.muted}>
                  <a className={styles.diagnosticLink} href={diagnosticsUrl} download>Download deploy diagnostics</a>
                </p>
              )}
            </div>
          )}

          {(releases?.error || (releases && releases.releases.length > 0)) && (
            <div className={styles.mt16}>
              <strong>Releases kept on the server</strong>
              {releases?.error && (
                <p className={`${styles.muted} ${styles.bad}`}>
                  {releases.error}{' '}
                  <ZestButton onClick={() => loadServer()} zest={{ ...NOBUSY, semanticType: 'refresh', buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Try again</ZestButton>
                </p>
              )}
              {releases && releases.releases.length > 0 && (
                <ul aria-label="Releases kept on the server" className={styles.list}>
                  {releases.releases.map(r => (
                    <li key={r.tag} className={styles.release}>
                      <b>{r.tag}</b>
                      <span className={styles.dim}>commit {shortSha(r.sha)}</span>
                      <span className={styles.dim}>{describeAge(r.createdUtc)}</span>
                      {r.isCurrent && <span className={styles.ok}>running now</span>}
                      {r.isPrevious && <span className={styles.warn}>previous</span>}
                      {!r.isCurrent && (
                        <span className={styles.pushRight}>
                          <ZestButton disabled={running} aria-label={`Roll back to ${r.tag}`} onClick={() => { setConfirmRestart(false); setConfirmTag(r.tag); }}
                            zest={{ ...NOBUSY, semanticType: 'undo', buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Roll back</ZestButton>
                        </span>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}

          {confirmRestart && (
            <div className={styles.confirm}>
              <div>Restart every service?</div>
              <p className={styles.muted}>
                This stops all of this project&apos;s services and starts them again on the release that is running
                {releases?.currentTag ? ` (${releases.currentTag})` : ''}. Expect a short downtime. Your data volumes are kept,
                and your saved variables are applied again. Nothing is built.
              </p>
              <div className={styles.confirmActions}>
                <ZestButton onClick={() => start(`${base}/restart-all`, {})} zest={{ ...NOBUSY, semanticType: 'approve' }}>Yes, restart everything</ZestButton>
                <ZestButton onClick={() => setConfirmRestart(false)} zest={{ ...NOBUSY, semanticType: 'cancel' }}>No, leave them running</ZestButton>
              </div>
            </div>
          )}

          {confirmTag && (
            <div className={styles.confirm}>
              <div>Go back to release {confirmTag}?</div>
              <p className={styles.muted}>
                Your code and images go back to how they were. Information stored in your database does not change,
                so if a newer release changed how it is stored, going back may not work.
              </p>
              <div className={styles.confirmActions}>
                <ZestButton onClick={() => start(`${base}/rollback`, { tag: confirmTag })} zest={{ ...NOBUSY, semanticType: 'approve' }}>Yes, roll back</ZestButton>
                <ZestButton onClick={() => setConfirmTag(null)} zest={{ ...NOBUSY, semanticType: 'cancel' }}>No, keep it as it is</ZestButton>
              </div>
            </div>
          )}

          {pendingDeploy && duplicates.length > 0 && (
            <div className={styles.confirm} role="alertdialog" aria-label="Duplicate variables">
              <div>Some variables are set in both the server settings and this project.</div>
              <p className={styles.muted}>
                The project&apos;s value is the one a deploy would use. Choose which side to remove them from, or keep both.
              </p>
              <ul className={styles.list}>
                {duplicates.map(d => (
                  <li key={d.name} className={styles.item}>
                    <b>{d.name}</b>: server <code>{d.serverValue}</code>, project <code>{d.projectValue}</code>
                  </li>
                ))}
              </ul>
              <div className={styles.confirmActions}>
                <ZestButton onClick={() => resolveDuplicates('server')} zest={{ ...NOBUSY, semanticType: 'approve' }}>Remove from server</ZestButton>
                <ZestButton onClick={() => resolveDuplicates('project')} zest={{ ...NOBUSY, semanticType: 'edit' }}>Remove from project</ZestButton>
                <ZestButton onClick={() => resolveDuplicates('none')} zest={{ ...NOBUSY, semanticType: 'send' }}>Keep both and deploy</ZestButton>
                <ZestButton onClick={() => { setDuplicates([]); setPendingDeploy(null); }} zest={{ ...NOBUSY, semanticType: 'cancel' }}>Cancel</ZestButton>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
