import { useCallback, useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import HoverTip from '@/shared/components/HoverTip';
import { api, sseUrl } from '@/shared/ApiService';
import {
  DEFAULT_JIT_CONFIG, JitConfig, JitConfigResponse, JitDependents, JitReleases, JitServiceHint,
  describeAge, phaseSummary, setDependentChoice, shortSha, validateJitConfig,
} from './jitDeploy';

interface Props {
  projectId: string;
}

type OpStatus = 'idle' | 'running' | 'success' | 'error';

const card: React.CSSProperties = {
  marginTop: 16, padding: '14px 16px', borderRadius: 8,
  background: 'var(--bg-card)', border: '1px solid var(--border-subtle)', fontSize: 13,
};
const muted: React.CSSProperties = { margin: '6px 0 0', fontSize: 12, color: 'var(--text-muted)' };
const row: React.CSSProperties = { display: 'flex', flexDirection: 'column', gap: 4, marginTop: 12 };
const input: React.CSSProperties = {
  padding: '7px 9px', borderRadius: 6, border: '1px solid var(--border-subtle)',
  background: 'var(--bg-input, transparent)', color: 'inherit', fontSize: 13, maxWidth: 420,
};
const err: React.CSSProperties = { color: '#B84040', fontSize: 12 };
const btn: React.CSSProperties = {
  padding: '7px 14px', borderRadius: 6, border: '1px solid var(--border-subtle)',
  background: 'transparent', color: 'inherit', cursor: 'pointer', fontSize: 13,
};
const primary: React.CSSProperties = { ...btn, background: 'var(--accent)', color: '#0B1220', border: 'none', fontWeight: 600 };
const tone = { ok: '#3D9970', warn: '#C9A84C', bad: '#B84040' } as const;

function Field(props: {
  id: string; label: string; tipLabel?: string; tip: string; error?: string; children: React.ReactNode;
}) {
  return (
    <div style={row}>
      <div>
        <label htmlFor={props.id}>{props.label}</label>
        <HoverTip label={props.tipLabel ?? props.label}>{props.tip}</HoverTip>
      </div>
      {props.children}
      {props.error && <span style={err}>{props.error}</span>}
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

  const [releases, setReleases] = useState<JitReleases | null>(null);
  const [dependents, setDependents] = useState<JitDependents | null>(null);

  const [force, setForce] = useState(false);
  const [commit, setCommit] = useState('');
  const [confirmTag, setConfirmTag] = useState<string | null>(null);

  const [op, setOp] = useState<{ status: OpStatus; logs: string[]; message: string }>({ status: 'idle', logs: [], message: '' });
  const esRef = useRef<EventSource | null>(null);

  const base = `/api/jit/${projectId}`;
  const dirty = JSON.stringify(cfg) !== JSON.stringify(saved);

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
        } else if (type === 'DeployCompleted') {
          es.close();
          const ok = data.status === 'Deployed';
          api.get<{ errorSummary?: string | null }>(`/api/builds/${buildId}`)
            .then(b => b?.errorSummary ?? null).catch(() => null)
            .then(summary => setOp(o => ({
              ...o,
              status: ok ? 'success' : 'error',
              message: ok ? 'Deployed successfully.' : (summary ?? 'It did not finish. See the log above.'),
            })));
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
    setOp({ status: 'running', logs: [], message: '' });
    try {
      const res = await api.post<{ buildId: string }>(path, body);
      stream(res.buildId);
    } catch (e) {
      setOp({ status: 'idle', logs: [], message: '' });
      toast.error((e as { message?: string })?.message ?? 'Could not start.');
    }
  };

  const running = op.status === 'running';
  const needsAttention = releases?.needsAttention === true;
  const canDeploy = saved.enabled && !dirty && !running && !needsAttention;
  const effectiveRestart = (service: string, dependent: string, fallback: boolean) =>
    cfg.dependentChoices.find(c => c.service === service && c.dependent === dependent)?.restart ?? fallback;

  if (!loaded || unavailable) return null;

  return (
    <div style={card}>
      <label style={{ display: 'flex', gap: 10, alignItems: 'center', cursor: 'pointer' }}>
        <input
          type="checkbox"
          aria-label="Build on the server instead of pushing to a registry"
          checked={cfg.enabled}
          onChange={e => set('enabled', e.target.checked)}
          style={{ accentColor: 'var(--accent)', width: 16, height: 16 }}
        />
        <span>Build on the server instead of pushing to a registry</span>
      </label>
      <p style={muted}>
        ShipRight fetches your code on the server, builds the images there, and swaps your running services for the new ones,
        all or nothing. If anything goes wrong it goes straight back to the previous release. No registry is needed.
      </p>

      {cfg.enabled && (
        <>
          <Field id="jit-repo" label="Git address" error={errors.repoUrl}
            tip={'The address the server uses to download your code.\nOn GitHub: open your repository, click the green Code button, choose SSH and copy the address.\nThe server needs permission to read it (a deploy key).'}>
            <input id="jit-repo" style={input} value={cfg.repoUrl} placeholder="git@github.com:you/your-app.git"
              onChange={e => set('repoUrl', e.target.value)} />
          </Field>

          <Field id="jit-branch" label="Branch" error={errors.branch}
            tip={'Which branch to deploy. Whatever is newest on this branch when you press Deploy is what goes live. Usually master or main.'}>
            <input id="jit-branch" style={input} value={cfg.branch} onChange={e => set('branch', e.target.value)} />
          </Field>

          <Field id="jit-env" label="Env file on the server (optional)" tipLabel="Env file on the server" error={errors.envFile}
            tip={'If your app reads settings from an env file that lives on the server (and not in git), give its full path, for example /home/ubuntu/shop/.env. Leave empty if you have none.'}>
            <input id="jit-env" style={input} value={cfg.envFile} onChange={e => set('envFile', e.target.value)} />
          </Field>

          <Field id="jit-project" label="Compose project name (optional)" tipLabel="Compose project name" error={errors.composeProject}
            tip={'The name docker uses to group this app\'s containers. Leave it empty and one is made from your project name. Only change it to match an app that is already running under a different name.'}>
            <input id="jit-project" style={input} value={cfg.composeProject} onChange={e => set('composeProject', e.target.value)} />
          </Field>

          <div style={{ marginTop: 12 }}>
            <button type="button" style={btn} onClick={() => setAdvanced(a => !a)}>
              {advanced ? 'Hide advanced settings' : 'Show advanced settings'}
            </button>
          </div>
          {advanced && (
            <div>
              <Field id="jit-keep" label="Releases to keep" error={errors.releasesToKeep}
                tip={'How many past releases stay on the server so you can go back to them. Never fewer than 2. Each one uses disk space.'}>
                <input id="jit-keep" type="number" style={input} value={Number.isNaN(cfg.releasesToKeep) ? '' : cfg.releasesToKeep} onChange={num('releasesToKeep')} />
              </Field>
              <Field id="jit-grace" label="Stop grace period (seconds)" tipLabel="Stop grace period" error={errors.stopGraceSeconds}
                tip={'How long a running service gets to finish what it is doing when it is told to stop, before it is forced to. Raise it if your app needs longer to shut down cleanly.'}>
                <input id="jit-grace" type="number" style={input} value={Number.isNaN(cfg.stopGraceSeconds) ? '' : cfg.stopGraceSeconds} onChange={num('stopGraceSeconds')} />
              </Field>
              <Field id="jit-health" label="Health check timeout (seconds)" tipLabel="Health check timeout" error={errors.healthTimeoutSeconds}
                tip={'How long a new service has to report that it is healthy before the deploy is judged a failure and rolled back. Raise it for apps that start slowly.'}>
                <input id="jit-health" type="number" style={input} value={Number.isNaN(cfg.healthTimeoutSeconds) ? '' : cfg.healthTimeoutSeconds} onChange={num('healthTimeoutSeconds')} />
              </Field>
              <Field id="jit-settle" label="Settle time (seconds)" tipLabel="Settle time" error={errors.settleSeconds}
                tip={'For a service with no health check: how long it must stay up, without restarting, to count as healthy.'}>
                <input id="jit-settle" type="number" style={input} value={Number.isNaN(cfg.settleSeconds) ? '' : cfg.settleSeconds} onChange={num('settleSeconds')} />
              </Field>
              <Field id="jit-disk" label="Minimum free disk space (MB)" tipLabel="Minimum free disk space" error={errors.minFreeDiskMb}
                tip={'A deploy refuses to start if the server has less free space than this, because building images needs room.'}>
                <input id="jit-disk" type="number" style={input} value={Number.isNaN(cfg.minFreeDiskMb) ? '' : cfg.minFreeDiskMb} onChange={num('minFreeDiskMb')} />
              </Field>
              <Field id="jit-nice" label="Build priority (0-19, optional)" tipLabel="Build priority" error={errors.buildNice}
                tip={'Higher numbers make the build politer to the services that are running while it builds. Leave empty for normal priority.'}>
                <input id="jit-nice" type="number" style={input} value={cfg.buildNice ?? ''}
                  onChange={e => set('buildNice', e.target.value === '' ? null : Number(e.target.value))} />
              </Field>
            </div>
          )}

          {services.length > 0 && (
            <div style={{ marginTop: 14 }}>
              <strong>What your compose file needs</strong>
              <p style={muted}>
                For every service ShipRight builds, your docker-compose.yml needs a build: section and exactly this image: line,
                so each release gets its own tag and can be rolled back.
              </p>
              <ul style={{ listStyle: 'none', padding: 0, margin: '8px 0 0' }}>
                {services.map(s => (
                  <li key={s.name} style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 4 }}>
                    <span>{s.name}:</span>
                    <code>{s.composeImageLine}</code>
                    <button type="button" style={{ ...btn, padding: '2px 8px', fontSize: 12 }}
                      aria-label={`Copy the image line for ${s.name}`}
                      onClick={() => navigator.clipboard.writeText(`image: ${s.composeImageLine}`)}>Copy</button>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {dependents && (
            <div style={{ marginTop: 14 }}>
              <strong>When a service is rebuilt, which others restart with it?</strong>
              {dependents.pairs.length === 0 && dependents.note && <p style={muted}>{dependents.note}</p>}
              {dependents.pairs.map(p => (
                <label key={`${p.service}>${p.dependent}`} style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 6, cursor: 'pointer' }}>
                  <input type="checkbox"
                    aria-label={`Restart ${p.dependent} when ${p.service} is replaced`}
                    checked={effectiveRestart(p.service, p.dependent, p.restart)}
                    onChange={e => set('dependentChoices', setDependentChoice(cfg.dependentChoices, p.service, p.dependent, e.target.checked))}
                    style={{ accentColor: 'var(--accent)' }} />
                  <span>Restart <b>{p.dependent}</b> when <b>{p.service}</b> is replaced</span>
                </label>
              ))}
            </div>
          )}
        </>
      )}

      {problems.length > 0 && (
        <ul style={{ margin: '12px 0 0', paddingLeft: 18, color: '#C9A84C' }}>
          {problems.map(p => <li key={p}>{p}</li>)}
        </ul>
      )}

      <div style={{ marginTop: 14, display: 'flex', gap: 10, alignItems: 'center' }}>
        <button type="button" style={primary} disabled={saving} onClick={save}>Save settings</button>
        {dirty && <span style={muted}>You have unsaved changes.</span>}
      </div>

      {saved.enabled && (
        <div style={{ marginTop: 18, paddingTop: 14, borderTop: '1px solid var(--border-subtle)' }}>
          {releases && !releases.error && (
            <p style={{ margin: '0 0 8px', fontSize: 12, color: tone[phaseSummary(releases.phase).tone] }}>
              Server status: {phaseSummary(releases.phase).label}
            </p>
          )}

          {needsAttention && (
            <div style={{ padding: 10, borderRadius: 6, border: `1px solid ${tone.bad}`, marginBottom: 10 }}>
              <div>{releases?.attention}</div>
              <button type="button" style={{ ...btn, marginTop: 8 }} disabled={running}
                onClick={() => start(`${base}/recover`, {})}>Recover</button>
            </div>
          )}

          <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
            <button type="button" style={primary} disabled={!canDeploy} onClick={() =>
              start(`${base}/deploy`, { force, commit: commit.trim() || null, allowWithoutRollback: false })}>Deploy now</button>
            {dirty && <span style={muted}>Save your settings before deploying.</span>}
          </div>
          <div style={{ display: 'flex', gap: 14, alignItems: 'center', flexWrap: 'wrap', marginTop: 10 }}>
            <label style={{ display: 'flex', gap: 6, alignItems: 'center', cursor: 'pointer' }}>
              <input type="checkbox" aria-label="Deploy even if nothing has changed" checked={force} onChange={e => setForce(e.target.checked)} />
              <span>Deploy even if nothing has changed</span>
            </label>
            <label style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
              <span>Deploy this exact commit (optional)</span>
              <input aria-label="Deploy this exact commit (optional)" style={{ ...input, width: 120 }} value={commit}
                placeholder="ab8b4a9" onChange={e => setCommit(e.target.value)} />
            </label>
          </div>

          {op.status !== 'idle' && (
            <div style={{ marginTop: 12 }}>
              <div role="log" aria-label="Deploy log" style={{
                maxHeight: 260, overflow: 'auto', padding: 10, borderRadius: 6, background: '#0B1220', color: '#E3E9F2',
                fontFamily: 'monospace', fontSize: 12, whiteSpace: 'pre-wrap',
              }}>
                {op.logs.map((l, i) => <div key={i}>{l}</div>)}
                {running && op.logs.length === 0 && <div>Starting…</div>}
              </div>
              {op.status === 'success' && <p style={{ color: tone.ok, margin: '8px 0 0' }}>{op.message}</p>}
              {op.status === 'error' && <p role="alert" style={{ color: tone.bad, margin: '8px 0 0' }}>{op.message}</p>}
            </div>
          )}

          <div style={{ marginTop: 16 }}>
            <strong>Releases kept on the server</strong>
            {releases?.error && (
              <p style={{ ...muted, color: tone.bad }}>
                {releases.error}{' '}
                <button type="button" style={{ ...btn, padding: '2px 8px', fontSize: 12 }} onClick={() => loadServer()}>Try again</button>
              </p>
            )}
            {releases && !releases.error && releases.releases.length === 0 && (
              <p style={muted}>No releases yet. They appear here after the first deploy.</p>
            )}
            {releases && releases.releases.length > 0 && (
              <ul aria-label="Releases kept on the server" style={{ listStyle: 'none', padding: 0, margin: '8px 0 0' }}>
                {releases.releases.map(r => (
                  <li key={r.tag} style={{ display: 'flex', gap: 10, alignItems: 'center', padding: '6px 0', borderBottom: '1px solid var(--border-subtle)' }}>
                    <b>{r.tag}</b>
                    <span style={{ color: 'var(--text-muted)' }}>commit {shortSha(r.sha)}</span>
                    <span style={{ color: 'var(--text-muted)' }}>{describeAge(r.createdUtc)}</span>
                    {r.isCurrent && <span style={{ color: tone.ok }}>running now</span>}
                    {r.isPrevious && <span style={{ color: tone.warn }}>previous</span>}
                    {!r.isCurrent && (
                      <button type="button" style={{ ...btn, marginLeft: 'auto', padding: '3px 10px' }} disabled={running}
                        aria-label={`Roll back to ${r.tag}`} onClick={() => setConfirmTag(r.tag)}>Roll back</button>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </div>

          {confirmTag && (
            <div style={{ marginTop: 10, padding: 10, borderRadius: 6, border: `1px solid ${tone.warn}` }}>
              <div>Go back to release {confirmTag}?</div>
              <p style={muted}>
                Your code and images go back to how they were. Information stored in your database does not change,
                so if a newer release changed how it is stored, going back may not work.
              </p>
              <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
                <button type="button" style={primary} onClick={() => start(`${base}/rollback`, { tag: confirmTag })}>Yes, roll back</button>
                <button type="button" style={btn} onClick={() => setConfirmTag(null)}>No, keep it as it is</button>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
