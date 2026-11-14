import { useState } from 'react';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { api } from '@/shared/ApiService';
import styles from './Styles/JitDeploy.module.css';

export interface ComposeCandidate {
  name: string;
  imageRef: string;
  tagVariable: string;
  built: boolean;
  suggested: boolean;
  note: string | null;
  dockerfile?: string | null;
  context?: string | null;
  target?: string | null;
  args?: Record<string, string> | null;
  argsNeedingAValue?: string[];
  otherStages?: string[];
}

export interface ComposeReading {
  relativePath: string;
  repoRoot: string;
  services: ComposeCandidate[];
  leftAlone: string[];
  notes: string[];
}

export interface ServiceHelp {
  explanation: string;
  steps: string[];
  found: ComposeReading | null;
}

/** A service to build, with how: its Dockerfile and build folder are paths inside the repository. */
export interface ConfiguredService {
  name: string;
  imageRef: string;
  tagVariable: string;
  dockerfile?: string | null;
  context?: string | null;
  target?: string | null;
  args?: Record<string, string> | null;
}

interface DockerfileDescription {
  repoRoot: string;
  dockerfileRelative: string;
  contextRelative: string;
  argsNeedingAValue: string[];
}

interface Props {
  base: string;
  /** True when build-on-server has no services of the project's own, so the services must come from the compose file. */
  needsServices: boolean;
  /** The services already taken from a compose file and kept in the settings. */
  current: ConfiguredService[];
  /** Why there are no services and what to do, when that is the problem. */
  help: ServiceHelp | null;
  /** `services` is null when only the file path should be set. */
  onApply: (composeFile: string, services: ConfiguredService[] | null) => void;
  onClear: () => void;
  /** One service's settings changed (its image name, Dockerfile, build folder or arguments). */
  onUpdate: (name: string, patch: Partial<ConfiguredService>) => void;
}


const messageOf = (e: unknown, fallback: string) =>
  e && typeof e === 'object' && 'message' in e && typeof (e as { message: unknown }).message === 'string' ? (e as { message: string }).message : fallback;

/** Empty slots for the build arguments a Dockerfile needs, next to the ones that already have a value. */
const withArgSlots = (args: Record<string, string> | null | undefined, needed: string[] | undefined): Record<string, string> => {
  const merged: Record<string, string> = { ...(args ?? {}) };
  for (const a of needed ?? []) if (!(a in merged)) merged[a] = '';
  return merged;
};

const toConfigured = (c: ComposeCandidate): ConfiguredService => ({
  name: c.name, imageRef: c.imageRef, tagVariable: c.tagVariable,
  dockerfile: c.dockerfile ?? null, context: c.context ?? null, target: c.target ?? null,
  args: withArgSlots(c.args, c.argsNeedingAValue),
});

function ServiceRow({ base, service, onUpdate }: { base: string; service: ConfiguredService; onUpdate: Props['onUpdate'] }) {
  const [picking, setPicking] = useState(false);
  const [details, setDetails] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const args = service.args ?? {};
  // An argument that still needs a value is always in view; the rest (and the image name) stay tucked away.
  const needValue = Object.keys(args).filter(k => args[k] === '');
  const shownArgs = details ? Object.keys(args) : needValue;

  const pick = async (path: string) => {
    setPicking(false); setError(null);
    try {
      const d = await api.post<DockerfileDescription>(`${base}/dockerfile`, { path });
      onUpdate(service.name, { dockerfile: d.dockerfileRelative, context: d.contextRelative, args: withArgSlots(service.args, d.argsNeedingAValue) });
    } catch (e) {
      setError(messageOf(e, 'Could not read that file.'));
    }
  };

  return (
    <li className={styles.service}>
      <strong className={styles.serviceName}>{service.name}</strong>
      {service.dockerfile ? (
        <div className={styles.mt6}>
          <div>Dockerfile: {service.dockerfile}</div>
          <div className={styles.muted}>Built from: {!service.context || service.context === '.' ? 'the repository root' : service.context}</div>
        </div>
      ) : (
        <div role="alert" className={`${styles.muted} ${styles.bad}`}>Pick this service&apos;s Dockerfile.</div>
      )}
      {picking ? (
        <div className={styles.mt6}>
          <FilePicker storageKey="jit-dockerfile" label={`Dockerfile for ${service.name}`} onSelect={pick} />
          <button type="button" className={`${styles.btn} ${styles.mt6}`} onClick={() => setPicking(false)}>Cancel</button>
        </div>
      ) : (
        <div className={styles.mt6}>
          <button type="button" className={styles.btn} aria-label={`Browse for Dockerfile for ${service.name}`} onClick={() => setPicking(true)}>
            {service.dockerfile ? 'Choose a different Dockerfile' : 'Browse for the Dockerfile'}
          </button>
        </div>
      )}
      {error && <p role="alert" className={`${styles.muted} ${styles.bad}`}>{error}</p>}
      <div className={styles.mt6}>
        <button type="button" className={`${styles.btn} ${styles.small}`} aria-label={`Details for ${service.name}`} aria-expanded={details}
          onClick={() => setDetails(d => !d)}>{details ? 'Hide details' : 'Details'}</button>
      </div>
      {details && (
        <div className={styles.mt4}>
          <label htmlFor={`img-${service.name}`} className={styles.muted}>Image name (the part before the tag)</label>
          <input id={`img-${service.name}`} aria-label={`Image name for ${service.name}`} className={styles.input} value={service.imageRef}
            onChange={e => onUpdate(service.name, { imageRef: e.target.value })} />
        </div>
      )}
      {shownArgs.map(k => (
        <div key={k} className={styles.mt6}>
          <label htmlFor={`arg-${service.name}-${k}`} className={styles.muted}>Build argument {k}{args[k] === '' ? ' (needs a value)' : ''}</label>
          <input id={`arg-${service.name}-${k}`} aria-label={`Value for ${k} in ${service.name}`} className={styles.input} value={args[k]}
            onChange={e => onUpdate(service.name, { args: { ...args, [k]: e.target.value } })} />
        </div>
      ))}
    </li>
  );
}

/**
 * Pick the compose file on this computer. ShipRight works out where it sits in the repository and which of its services to build,
 * finds each service's Dockerfile (each can be changed with the file picker), and explains tagging. When the project has no services
 * of its own it also explains why and how to fix that, using the compose file it can find.
 */
export default function JitComposeServices({ base, needsServices, current, help, onApply, onClear, onUpdate }: Props) {
  const [picking, setPicking] = useState(false);
  const [reading, setReading] = useState<ComposeReading | null>(null);
  const [ticked, setTicked] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const read = async (path: string) => {
    setPicking(false); setBusy(true); setError(null); setReading(null);
    try {
      const r = await api.post<ComposeReading>(`${base}/compose-file`, { path });
      setReading(r);
      setTicked(new Set(r.services.filter(s => s.suggested).map(s => s.name)));
    } catch (e) {
      setError(messageOf(e, 'Could not read that file.'));
    } finally { setBusy(false); }
  };

  const toggle = (name: string) => setTicked(prev => { const next = new Set(prev); if (next.has(name)) next.delete(name); else next.add(name); return next; });

  return (
    <div>
      {help && (
        <div className={styles.boxNote} role="note" aria-label="Why there are no services">
          <strong>Why this cannot deploy yet</strong>
          <p className={styles.muted}>{help.explanation}</p>
          <ol className={styles.steps}>
            {help.steps.map(s => <li key={s} className={styles.mt4}>{s}</li>)}
          </ol>
          {help.found && help.found.services.length > 0 && (
            <div className={styles.mt8}>
              <span className={styles.muted}>Found in {help.found.relativePath}: {help.found.services.map(s => s.name).join(', ')}</span>{' '}
              <button type="button" className={styles.primary} onClick={() => onApply(help.found!.relativePath, help.found!.services.map(toConfigured))}>Use these services</button>
            </div>
          )}
        </div>
      )}

      {current.length > 0 && (
        <div className={styles.box}>
          <p className={`${styles.muted} ${styles.mt0}`}>
            Services to build, from your compose file: {current.map(s => s.name).join(', ')}{' '}
            <button type="button" className={styles.btn} onClick={onClear}>Clear these services</button>
          </p>
          <p className={styles.muted}>Tags are added for you (like r12-ab12cd3: a release number and the commit); your compose file needs no changes.</p>
          <ul className={styles.list}>
            {current.map(s => <ServiceRow key={s.name} base={base} service={s} onUpdate={onUpdate} />)}
          </ul>
        </div>
      )}

      {picking ? (
        <div className={styles.mt8}>
          <FilePicker storageKey="compose-file" label="Your compose file" onSelect={read} />
          <button type="button" className={`${styles.btn} ${styles.mt6}`} onClick={() => setPicking(false)}>Cancel</button>
        </div>
      ) : (
        <div className={styles.mt8}>
          <button type="button" className={styles.btn} disabled={busy} onClick={() => setPicking(true)}>{busy ? 'Reading…' : 'Browse for the compose file'}</button>
        </div>
      )}

      {error && <p role="alert" className={`${styles.muted} ${styles.bad}`}>{error}</p>}

      {reading && (
        <div className={styles.box}>
          <strong>{reading.relativePath}</strong>
          <span className={styles.muted}> (its place in your repository)</span>
          {reading.services.length > 0 && needsServices && (
            <>
              <p className={styles.muted}>ShipRight found these services to build:</p>
              <ul className={styles.list}>
                {reading.services.map(s => (
                  <li key={s.name} className={styles.mt6}>
                    <label className={styles.check}>
                      <input type="checkbox" aria-label={`Build ${s.name}`} checked={ticked.has(s.name)} onChange={() => toggle(s.name)} />
                      <strong>{s.name}</strong> <span className={styles.muted}>{s.imageRef}</span>
                    </label>
                    <div className={styles.muted}>{s.dockerfile ? `Dockerfile: ${s.dockerfile}` : 'No Dockerfile found yet: you will pick it next.'}</div>
                  </li>
                ))}
              </ul>
            </>
          )}
          {reading.services.length > 0 && !needsServices && (
            <p className={styles.muted}>This file builds: {reading.services.map(s => s.name).join(', ')}.</p>
          )}
          {reading.services.length === 0 && reading.notes.map(n => <p key={n} className={styles.muted}>{n}</p>)}
          {reading.services.length > 0 && (
            <div className={styles.mt8}>
              {needsServices ? (
                <button type="button" className={styles.primary} disabled={ticked.size === 0}
                  onClick={() => onApply(reading.relativePath, reading.services.filter(s => ticked.has(s.name)).map(toConfigured))}>
                  Use this compose file and these services
                </button>
              ) : (
                <button type="button" className={styles.primary} onClick={() => onApply(reading.relativePath, null)}>Use this compose file</button>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
