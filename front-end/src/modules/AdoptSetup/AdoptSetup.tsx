import { useCallback, useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import FilePicker from '@/modules/FilePicker/FilePicker';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';
import { getBrowserTimeZone } from '@/shared/timeZone';
import { IProject } from '@/shared/types/IProject';
import {
  AdoptionPlan, buildAdoptedProjectInput, canCreate, ComposeMode, ComposePreview, needsChoice, serviceFacts, suggestAdoptedName,
} from './adopt';

interface Props {
  /** Open straight on a folder (for example the one already picked in the guided flow). */
  initialRoot?: string;
  onSaved: (project: IProject) => void;
  onCancel: () => void;
}

const wrap: React.CSSProperties = { fontSize: 13, display: 'flex', flexDirection: 'column', gap: 12 };
const muted: React.CSSProperties = { margin: 0, fontSize: 12, color: 'var(--text-muted)' };
const card: React.CSSProperties = { padding: '10px 12px', borderRadius: 8, border: '1px solid var(--border-subtle)' };
const input: React.CSSProperties = {
  padding: '7px 9px', borderRadius: 6, border: '1px solid var(--border-subtle)', background: 'var(--bg-input, transparent)',
  color: 'inherit', fontSize: 13, width: '100%', boxSizing: 'border-box',
};
const btn: React.CSSProperties = { padding: '7px 16px', borderRadius: 6, border: '1px solid var(--border-subtle)', background: 'transparent', color: 'inherit', cursor: 'pointer', fontSize: 13 };
const primary: React.CSSProperties = { ...btn, background: 'var(--accent, #C9A84C)', color: '#10192E', border: 'none', fontWeight: 600 };
const err: React.CSSProperties = { color: '#E08A8A', fontSize: 12 };

const messageOf = (e: unknown, fallback: string) =>
  e && typeof e === 'object' && 'message' in e && typeof (e as { message: unknown }).message === 'string' ? (e as { message: string }).message : fallback;

/**
 * Adopting a project that already has its own Docker setup (Dockerfiles with any name, a compose file): ShipRight reads it,
 * shows what it understood, lets the person settle anything it could not decide, and creates the project with each service's
 * real Dockerfile, build folder and arguments. Changes to the person's own files are listed first, ticked by the person,
 * and the compose file is backed up before it is touched.
 */
export default function AdoptSetup({ initialRoot, onSaved, onCancel }: Props) {
  const [rootPath, setRootPath] = useState(initialRoot ?? '');
  const [plan, setPlan] = useState<AdoptionPlan | null>(null);
  const [choices, setChoices] = useState<Record<string, string>>({});
  const [included, setIncluded] = useState<Set<string>>(new Set());
  const [argValues, setArgValues] = useState<Record<string, Record<string, string>>>({});
  const [name, setName] = useState('');
  const [mode, setMode] = useState<ComposeMode>('registry');
  const [preview, setPreview] = useState<ComposePreview | null>(null);
  const [updateCompose, setUpdateCompose] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const started = useRef(false);

  const look = useCallback(async (root: string, ch: Record<string, string>, tick?: string) => {
    setBusy(true); setError(null);
    try {
      const { plan: p } = await api.post<{ plan: AdoptionPlan }>('/api/setup/adopt/plan', { rootPath: root, choices: ch });
      setPlan(p);
      setIncluded(prev => {
        if (prev.size === 0 && tick === undefined) return new Set(p.services.filter(s => s.dockerfilePath !== null).map(s => s.name));
        const next = new Set([...prev].filter(n => p.services.some(s => s.name === n)));
        if (tick) next.add(tick);
        return next;
      });
      setName(n => n || suggestAdoptedName(p));
    } catch (e) {
      setError(messageOf(e, 'ShipRight could not look at that folder.'));
    } finally {
      setBusy(false);
    }
  }, []);

  useEffect(() => {
    if (!initialRoot || started.current) return;
    started.current = true;
    look(initialRoot, {});
  }, [initialRoot, look]);

  // What would change in the compose file, for the chosen way of deploying.
  useEffect(() => {
    if (!plan?.composeFile) { setPreview(null); return; }
    let cancelled = false;
    api.post<ComposePreview>('/api/setup/adopt/compose-preview', { rootPath: plan.root, mode, choices })
      .then(p => { if (!cancelled) { setPreview(p); setUpdateCompose(p.changes.length > 0); } })
      .catch(() => { if (!cancelled) setPreview(null); });
    return () => { cancelled = true; };
  }, [plan, mode, choices]);

  const choose = (service: string, file: string) => {
    if (!file) return;
    const next = { ...choices, [service]: file };
    setChoices(next);
    look(rootPath, next, service);
  };

  const toggle = (n: string) => setIncluded(prev => { const next = new Set(prev); if (next.has(n)) next.delete(n); else next.add(n); return next; });
  const setArg = (service: string, arg: string, value: string) =>
    setArgValues(prev => ({ ...prev, [service]: { ...(prev[service] ?? {}), [arg]: value } }));

  const create = async () => {
    if (!plan) return;
    setBusy(true); setError(null);
    try {
      // First, so a machine without a working WSL is told before a single file has been written.
      const { home } = await api.get<{ home: string }>('/api/setup/wsl-home');

      const needVersion = plan.services.filter(s => included.has(s.name) && s.dockerfilePath !== null && s.versionFile === null).map(s => s.name);
      if (needVersion.length > 0) await api.post('/api/setup/adopt/version-files', { rootPath: plan.root, services: needVersion });
      if (plan.composeFile && updateCompose && (preview?.changes.length ?? 0) > 0)
        await api.post('/api/setup/adopt/compose-apply', { rootPath: plan.root, mode, choices });

      const input = buildAdoptedProjectInput(plan, included, argValues, name.trim(), getBrowserTimeZone(), home);
      const project = await api.post<IProject>('/api/projects', input);
      toast.success('Project created. Commit the changes ShipRight made before your first build.');
      onSaved(project);
    } catch (e) {
      setError(messageOf(e, 'Something went wrong while creating the project.'));
    } finally {
      setBusy(false);
    }
  };

  // ── screens ────────────────────────────────────────────────────────────────

  if (!plan) {
    return (
      <div style={wrap}>
        <h2 style={{ margin: 0, fontSize: 16 }}>Adopt a project that already has Docker files</h2>
        <p style={muted}>
          Pick the folder. ShipRight reads its Dockerfiles (whatever they are called) and its compose file, and sets the project up with
          what it finds, without creating Dockerfiles of its own.
        </p>
        <FilePicker dirsOnly storageKey="project-root" label="Project folder" onSelect={setRootPath} />
        {rootPath && <p style={muted}>{rootPath}</p>}
        {error && <p role="alert" style={err}>{error}</p>}
        <div style={{ display: 'flex', gap: 8 }}>
          <button type="button" style={primary} disabled={!rootPath || busy} onClick={() => look(rootPath, {})}>{busy ? 'Looking…' : 'Look at this folder'}</button>
          <button type="button" style={btn} onClick={onCancel}>Cancel</button>
        </div>
      </div>
    );
  }

  if (plan.services.length === 0) {
    return (
      <div style={wrap}>
        <h2 style={{ margin: 0, fontSize: 16 }}>ShipRight could not find a Docker setup here</h2>
        {plan.warnings.map(w => <p key={w} style={muted}>{w}</p>)}
        <div><button type="button" style={btn} onClick={() => { setPlan(null); setChoices({}); setIncluded(new Set()); }}>Choose a different folder</button></div>
      </div>
    );
  }

  const changes = preview?.changes ?? [];

  return (
    <div style={wrap}>
      <h2 style={{ margin: 0, fontSize: 16 }}>ShipRight found an existing Docker setup</h2>
      <p style={muted}>{plan.root}</p>

      {plan.services.map(s => (
        <div key={s.name} style={card}>
          <label style={{ display: 'flex', gap: 8, alignItems: 'center', cursor: 'pointer' }}>
            <input type="checkbox" aria-label={`Adopt ${s.name}`} checked={included.has(s.name)} disabled={needsChoice(s)} onChange={() => toggle(s.name)} />
            <strong>{s.name}</strong>
            <span style={muted}>{s.confidence === 'High' ? 'sure' : s.confidence === 'Medium' ? 'fairly sure' : 'please check'}</span>
          </label>
          <div style={{ marginLeft: 24, marginTop: 6, display: 'grid', gap: 3 }}>
            {serviceFacts(s).map(f => <div key={f}>{f}</div>)}
            <div style={muted}>
              {s.versionFile ? `Version file: ${s.versionFile}` : `ShipRight will create ${s.suggestedVersionFile} (starting at 0.1.0)`}
            </div>
            {s.reasons.map(r => <div key={r} style={muted}>{r}</div>)}
            {s.alternatives.length > 0 && (
              <div>
                <select aria-label={`Dockerfile for ${s.name}`} style={input} value="" onChange={e => choose(s.name, e.target.value)}>
                  <option value="">{needsChoice(s) ? 'Pick the Dockerfile that builds it…' : 'Use a different Dockerfile…'}</option>
                  {s.alternatives.map(a => <option key={a} value={a}>{a}</option>)}
                </select>
              </div>
            )}
            {s.argsNeedingAValue.map(a => (
              <div key={a}>
                <label htmlFor={`arg-${s.name}-${a}`} style={muted}>Build argument {a} has no value in the Dockerfile</label>
                <input id={`arg-${s.name}-${a}`} aria-label={`Value for ${a} in ${s.name}`} style={input} placeholder="Value to build with"
                  value={argValues[s.name]?.[a] ?? ''} onChange={e => setArg(s.name, a, e.target.value)} />
              </div>
            ))}
            {s.otherStages.length > 0 && <div style={muted}>This Dockerfile also ends in: {s.otherStages.join(', ')}. Each could be its own image later.</div>}
          </div>
        </div>
      ))}

      {plan.thirdParty.length > 0 && (
        <div style={card}><strong>Left alone</strong> <span style={muted}>ShipRight does not build these:</span> <span>{plan.thirdParty.join(', ')}</span></div>
      )}
      {plan.unusedDockerfiles.length > 0 && (
        <div style={card}>
          <strong>Not used by anything ShipRight can see</strong>
          <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>{plan.unusedDockerfiles.map(u => <li key={u}>{u}</li>)}</ul>
        </div>
      )}
      {plan.warnings.map(w => <p key={w} style={muted}>{w}</p>)}

      {plan.composeFile && (
        <div style={card}>
          <strong>Your compose file</strong>
          <HoverTip label="Your compose file">ShipRight deploys by giving each service its own image tag. Your compose file may need a small change for that. Nothing is changed unless you tick the box, and a backup copy is kept.</HoverTip>
          <div role="radiogroup" aria-label="How will you deploy?" style={{ display: 'grid', gap: 4, margin: '8px 0' }}>
            <label><input type="radio" name="adopt-mode" checked={mode === 'registry'} onChange={() => setMode('registry')} /> Push images to a registry (ShipRight&apos;s usual deploy)</label>
            <label><input type="radio" name="adopt-mode" checked={mode === 'build-on-server'} onChange={() => setMode('build-on-server')} /> Build on the server</label>
          </div>
          {changes.length === 0 && preview && <p style={muted}>Your compose file already works with ShipRight.</p>}
          {changes.length > 0 && (
            <>
              <ul style={{ margin: '4px 0', paddingLeft: 18, display: 'grid', gap: 4 }}>
                {changes.map((c, i) => (
                  <li key={`${c.service}-${c.kind}-${i}`}>
                    <span style={muted}>{c.service} ({c.kind}): </span><code>{c.after}</code>
                  </li>
                ))}
              </ul>
              {preview?.notes.map(n => <p key={n} style={muted}>{n}</p>)}
              <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <input type="checkbox" aria-label="Update my compose file (a backup is kept)" checked={updateCompose} onChange={e => setUpdateCompose(e.target.checked)} />
                Update my compose file (a backup is kept)
              </label>
            </>
          )}
        </div>
      )}

      <p style={muted}>Commit these changes before your first build: ShipRight builds only from saved versions of your code.</p>

      <div>
        <label htmlFor="adopt-name" style={muted}>Project name</label>
        <input id="adopt-name" style={input} value={name} onChange={e => setName(e.target.value)} />
      </div>

      {error && <p role="alert" style={err}>{error}</p>}
      <div style={{ display: 'flex', gap: 8 }}>
        <button type="button" style={primary} disabled={busy || !canCreate(plan, included, name)} onClick={create}>{busy ? 'Working…' : 'Create project'}</button>
        <button type="button" style={btn} onClick={onCancel}>Cancel</button>
      </div>
    </div>
  );
}
