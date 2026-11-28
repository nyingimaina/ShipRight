import { useMemo, useState } from 'react';
import { useRouter } from 'next/router';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import type { JitDryRun } from '@/modules/JitDeploy/jitDeploy';
import {
  BootstrapApplyResponse,
  BootstrapApplyResult,
  BootstrapPlan,
  BootstrapRequest,
  GitInspection,
} from '@/shared/types/IBootstrap';
import styles from './BootstrapDeployment.module.css';

type Step = 1 | 2 | 3;

interface ServiceRow {
  name: string;
  image: string;
  dockerfile: string;
  context: string;
}

const emptyService = (): ServiceRow => ({ name: 'app', image: '', dockerfile: 'Dockerfile', context: '.' });

const slug = (value: string) =>
  value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'app';

const parseEnv = (text: string): Record<string, string> => {
  const env: Record<string, string> = {};
  for (const raw of text.split('\n')) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const at = line.indexOf('=');
    if (at <= 0) continue;
    env[line.slice(0, at).trim()] = line.slice(at + 1);
  }
  return env;
};

const STEPS: { id: Step; label: string }[] = [
  { id: 1, label: 'Source' },
  { id: 2, label: 'Server' },
  { id: 3, label: 'Review' },
];

interface Props {
  /** Cloud installs do not map the bootstrap routes: they change a person's own machine and a server. */
  cloud: boolean;
}

/**
 * Writes a deployment in three short steps: source, server, review. The address is the only required target fact;
 * everything else can be added later. It previews before writing and never deploys.
 */
export default function BootstrapDeployment({ cloud }: Props) {
  const router = useRouter();

  const [step, setStep] = useState<Step>(1);

  const [host, setHost] = useState('');
  const [username, setUsername] = useState('');
  const [folder, setFolder] = useState('');
  const [domain, setDomain] = useState('');
  const [sshKeyPath, setSshKeyPath] = useState('');

  const [projectName, setProjectName] = useState('');
  const [repoUrl, setRepoUrl] = useState('');
  const [branch, setBranch] = useState('master');
  const [composeFile, setComposeFile] = useState('docker-compose.yml');
  const [composeProject, setComposeProject] = useState('');
  const [pathPrefix, setPathPrefix] = useState('');
  const [services, setServices] = useState<ServiceRow[]>([emptyService()]);
  const [envText, setEnvText] = useState('');

  const [gitInfo, setGitInfo] = useState<GitInspection | null>(null);
  const [gitBusy, setGitBusy] = useState(false);
  const [gitError, setGitError] = useState<string | null>(null);

  const [plan, setPlan] = useState<BootstrapPlan | null>(null);
  const [result, setResult] = useState<BootstrapApplyResult | null>(null);
  const [busy, setBusy] = useState<'preview' | 'apply' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dryRun, setDryRun] = useState<JitDryRun | null>(null);
  const [dryBusy, setDryBusy] = useState(false);

  const suggestedFolder = useMemo(
    () => (username.trim() ? `/home/${username.trim()}/${slug(projectName || 'app')}` : ''),
    [username, projectName],
  );

  const clear = () => {
    setPlan(null);
    setResult(null);
    setError(null);
  };

  const onChange = (set: (value: string) => void) => (event: React.ChangeEvent<HTMLInputElement>) => {
    set(event.target.value);
    clear();
  };

  const updateService = (index: number, field: keyof ServiceRow, value: string) => {
    setServices((prev) => prev.map((row, i) => (i === index ? { ...row, [field]: value } : row)));
    clear();
  };

  const sourceReady =
    projectName.trim().length > 0 &&
    repoUrl.trim().length > 0 &&
    composeFile.trim().length > 0 &&
    services.some((row) => row.name.trim() && row.image.trim() && row.dockerfile.trim());

  const canLeave = step === 1
    ? sourceReady
    : step === 2
      ? host.trim().length > 0
      : plan?.ok === true;

  const buildRequest = (): BootstrapRequest => ({
    preset: 'None',
    host: host.trim(),
    username: username.trim(),
    remoteWorkingDir: folder.trim(),
    domain: domain.trim(),
    sshKeyPath: sshKeyPath.trim(),
    projectName: projectName.trim(),
    repoUrl: repoUrl.trim(),
    branch: branch.trim() || 'master',
    composeFile: composeFile.trim() || 'docker-compose.yml',
    composeProject: composeProject.trim() || undefined,
    providerRepoId: gitInfo?.repoId ? String(gitInfo.repoId) : undefined,
    pathPrefix: pathPrefix.trim() || undefined,
    services: services
      .filter((row) => row.name.trim() || row.image.trim())
      .map((row) => ({
        name: row.name.trim(),
        image: row.image.trim(),
        dockerfile: row.dockerfile.trim(),
        context: row.context.trim() || '.',
      })),
    env: parseEnv(envText),
  });

  const preview = async () => {
    setBusy('preview');
    setError(null);
    try {
      const next = await api.post<BootstrapPlan>('/api/bootstrap/plan', buildRequest());
      setPlan(next);
      if (!next.ok) setError(next.problems[0] ?? 'The configuration is not ready yet.');
    } catch (e: any) {
      setError(e?.message ?? 'ShipRight could not check that configuration. Try again.');
    } finally {
      setBusy(null);
    }
  };

  const apply = async () => {
    setBusy('apply');
    setError(null);
    try {
      const next = await api.post<BootstrapApplyResponse>('/api/bootstrap/apply', buildRequest());
      setPlan(next.plan);
      setResult(next.result);
      toast.success('The deployment is written.');
    } catch (e: any) {
      setError(e?.message ?? 'ShipRight could not write that deployment. Try again.');
      if (Array.isArray(e?.problems) && plan) setPlan({ ...plan, problems: e.problems, ok: false });
    } finally {
      setBusy(null);
    }
  };

  const inspectRepo = async () => {
    setGitBusy(true);
    setGitError(null);
    try {
      const info = await api.post<GitInspection>('/api/git/inspect', { url: repoUrl.trim() });
      setGitInfo(info);
      if (!branch.trim() || branch === 'master') setBranch(info.defaultBranch);
      if (info.suggestedComposeFile) setComposeFile(info.suggestedComposeFile);
      if (!projectName.trim()) {
        const suggested = slug(info.name);
        setProjectName(suggested);
        if (!composeProject.trim()) setComposeProject(suggested);
      } else if (!composeProject.trim()) {
        setComposeProject(slug(projectName));
      }
      if (info.services.length > 0) {
        setServices(info.services.map((service) => ({
          name: service.name,
          image: service.image,
          dockerfile: service.dockerfile || 'Dockerfile',
          context: service.context || '.',
        })));
      }
      clear();
    } catch (e: any) {
      setGitError(e?.message ?? 'ShipRight could not read that repository.');
    } finally {
      setGitBusy(false);
    }
  };

  const goNext = () => {
    if (!canLeave) return;
    if (step === 2) {
      setStep(3);
      void preview();
      return;
    }
    setStep((current) => Math.min(3, current + 1) as Step);
  };

  const checkServer = async () => {
    if (!result?.projectId) return;
    setDryBusy(true);
    try {
      const next = await api.post<JitDryRun>(`/api/jit/${result.projectId}/dry-run`, {});
      setDryRun(next);
    } catch (e: any) {
      setDryRun(null);
      setError(e?.message ?? 'The server check failed. Try again.');
    } finally {
      setDryBusy(false);
    }
  };

  if (cloud) {
    return (
      <section className={styles.card}>
        <h1 className={styles.title}>Bootstrap runs in the desktop app</h1>
        <p className={styles.lead}>
          Writing a deployment from scratch changes this computer and a server over SSH. Open the ShipRight
          desktop app to do it.
        </p>
      </section>
    );
  }

  if (result) {
    return (
      <section className={styles.wrap}>
        <div className={styles.card}>
          <h1 className={styles.title}>Deployment written</h1>
          <p className={styles.lead}>
            The server, the project and the deploy settings are saved. Nothing has run on the server yet.
          </p>

          {result.publicKey && (
            <div className={styles.keyBox}>
              <span className={styles.label}>Public key</span>
              <code className={styles.code}>{result.publicKey}</code>
              <ZestButton
                onClick={() => {
                  navigator.clipboard?.writeText(result.publicKey ?? '');
                  toast.success('Public key copied.');
                }}
                zest={{ buttonStyle: 'text', semanticType: 'submit', visualOptions: { size: 'sm' } }}
              >
                Copy
              </ZestButton>
              <span className={styles.hint}>
                Add this to the server&apos;s authorized keys once, then run a dry run.
              </span>
            </div>
          )}

          {plan && plan.deployBlockers.length > 0 && (
            <div className={styles.deferred}>
              <span className={styles.label}>Saved — deploy needs these later</span>
              <ul className={styles.warnings}>
                {plan.deployBlockers.map((item) => <li key={item}>{item}</li>)}
              </ul>
            </div>
          )}

          <ol className={styles.steps}>
            {result.nextSteps.map((step) => <li key={step}>{step}</li>)}
          </ol>

          <div className={styles.inlineActions}>
            <ZestButton
              onClick={checkServer}
              disabled={dryBusy}
              zest={{ buttonStyle: 'outline', semanticType: 'submit', visualOptions: { size: 'sm' } }}
            >
              {dryBusy ? 'Checking…' : 'Check the server'}
            </ZestButton>
            <span className={styles.hint}>Reads the server and the code without changing anything.</span>
          </div>

          {dryRun && (
            <div className={styles.deferred}>
              <span className={styles.label}>
                {dryRun.ok ? 'The server is ready' : 'The server needs attention'}
              </span>
              {dryRun.problems.length > 0 && (
                <ul className={styles.problems}>
                  {dryRun.problems.map((problem) => <li key={problem}>{problem}</li>)}
                </ul>
              )}
              {dryRun.warnings.length > 0 && (
                <ul className={styles.warnings}>
                  {dryRun.warnings.map((warning) => <li key={warning}>{warning}</li>)}
                </ul>
              )}
              {dryRun.commands.length > 0 && (
                <span className={styles.hint}>{dryRun.commands.length} commands would run.</span>
              )}
            </div>
          )}

          <div className={styles.actions}>
            <ZestButton
              onClick={() => router.push(`/projects/${result.projectId}`)}
              zest={{ semanticType: 'submit', visualOptions: { variant: 'standard' } }}
            >
              Open the project
            </ZestButton>
            <ZestButton
              onClick={() => router.push('/servers')}
              zest={{ buttonStyle: 'outline', semanticType: 'submit', visualOptions: { variant: 'standard' } }}
            >
              Open the server
            </ZestButton>
            <ZestButton
              onClick={() => { setResult(null); setPlan(null); setStep(1); }}
              zest={{ buttonStyle: 'text', semanticType: 'cancel' }}
            >
              Set up another
            </ZestButton>
          </div>
        </div>
      </section>
    );
  }

  return (
    <section className={styles.wrap}>
      <header className={styles.header}>
        <h1 className={styles.title}>Set up a deployment</h1>
        <p className={styles.lead}>
          Deploy an app from its repository to a server. Only the address is required; the user, key and domain can
          be added later.
        </p>
      </header>

      <ol className={styles.stepper} aria-label="Deployment steps">
        {STEPS.map((item) => (
          <li
            key={item.id}
            className={`${styles.stepItem} ${step === item.id ? styles.stepCurrent : ''} ${step > item.id ? styles.stepDone : ''}`}
            aria-current={step === item.id ? 'step' : undefined}
          >
            <span className={styles.stepNumber}>{step > item.id ? '✓' : item.id}</span>
            <span className={styles.stepLabel}>{item.label}</span>
          </li>
        ))}
      </ol>

      <div className={styles.card}>
        {step === 1 && (
          <div className={styles.stepBody}>
            <h2 className={styles.subtitle}>Where is the app built from?</h2>
            <p className={styles.lead}>Point at a repository, then check it to fill the branch and services.</p>

            <div className={styles.grid}>
              <label className={styles.field}>
                <span className={styles.label}>Project name</span>
                <input
                  className={styles.input}
                  value={projectName}
                  onChange={(e) => {
                    const value = e.target.value;
                    setProjectName(value);
                    if (!composeProject.trim()) setComposeProject(value.trim() ? slug(value) : '');
                    clear();
                  }}
                  placeholder="shop"
                  autoComplete="off"
                />
              </label>

              <label className={styles.field}>
                <span className={styles.label}>Repository</span>
                <input
                  className={styles.input}
                  value={repoUrl}
                  onChange={(e) => { setRepoUrl(e.target.value); setGitInfo(null); clear(); }}
                  placeholder="git@github.com:owner/repo.git"
                  autoComplete="off"
                />
              </label>

              <label className={styles.field}>
                <span className={styles.label}>Branch</span>
                <input
                  className={styles.input}
                  value={branch}
                  onChange={onChange(setBranch)}
                  placeholder="master"
                  autoComplete="off"
                />
              </label>

              <label className={styles.field}>
                <span className={styles.label}>Compose file</span>
                <input
                  className={styles.input}
                  value={composeFile}
                  onChange={onChange(setComposeFile)}
                  placeholder="docker-compose.yml"
                  autoComplete="off"
                />
              </label>

              <label className={styles.field}>
                <span className={styles.label}>Compose project (optional)</span>
                <input
                  className={styles.input}
                  value={composeProject}
                  onChange={onChange(setComposeProject)}
                  placeholder="derived from the name"
                  autoComplete="off"
                />
              </label>

              <label className={styles.field}>
                <span className={styles.label}>Path prefix (optional)</span>
                <input
                  className={styles.input}
                  value={pathPrefix}
                  onChange={onChange(setPathPrefix)}
                  placeholder="apps/shop"
                  autoComplete="off"
                />
                <span className={styles.hint}>For a monorepo: the folder this project uses.</span>
              </label>
            </div>

            <div className={styles.inlineActions}>
              <ZestButton
                onClick={inspectRepo}
                disabled={gitBusy || !repoUrl.trim()}
                zest={{ buttonStyle: 'outline', semanticType: 'submit', visualOptions: { size: 'sm' } }}
              >
                {gitBusy ? 'Checking…' : 'Check repository'}
              </ZestButton>
              <span className={styles.hint}>Reads the branch, compose file and services from GitHub.</span>
            </div>

            {gitError && (
              <div className={styles.problem} role="alert">
                <strong>Could not read it.</strong> {gitError}
              </div>
            )}

            {gitInfo && (
              <div className={styles.deferred}>
                <span className={styles.label}>
                  {gitInfo.owner}/{gitInfo.name}{gitInfo.private ? ' · private' : ''}
                </span>
                <span className={styles.hint}>
                  Default branch: {gitInfo.defaultBranch} · {gitInfo.branches.length}{' '}
                  branch{gitInfo.branches.length === 1 ? '' : 'es'}
                </span>
                {gitInfo.composeFiles.length > 0 && (
                  <span className={styles.hint}>Compose files: {gitInfo.composeFiles.join(', ')}</span>
                )}
                {gitInfo.services.length > 0 && (
                  <span className={styles.hint}>
                    Services to build: {gitInfo.services.map((service) => service.name).join(', ')}
                  </span>
                )}
                {gitInfo.notes.map((note) => (
                  <span className={styles.hint} key={note}>{note}</span>
                ))}
              </div>
            )}

            <div className={styles.services}>
              <span className={styles.label}>Services to build</span>
              {services.map((row, index) => (
                <div className={styles.serviceRow} key={index}>
                  <input
                    className={styles.input}
                    aria-label={`Service ${index + 1} name`}
                    value={row.name}
                    onChange={(e) => updateService(index, 'name', e.target.value)}
                    placeholder="api"
                  />
                  <input
                    className={styles.input}
                    aria-label={`Service ${index + 1} image`}
                    value={row.image}
                    onChange={(e) => updateService(index, 'image', e.target.value)}
                    placeholder="owner/app-api"
                  />
                  <input
                    className={styles.input}
                    aria-label={`Service ${index + 1} Dockerfile`}
                    value={row.dockerfile}
                    onChange={(e) => updateService(index, 'dockerfile', e.target.value)}
                    placeholder="Dockerfile"
                  />
                  <input
                    className={styles.input}
                    aria-label={`Service ${index + 1} context`}
                    value={row.context}
                    onChange={(e) => updateService(index, 'context', e.target.value)}
                    placeholder="."
                  />
                  <button
                    type="button"
                    className={styles.toggle}
                    disabled={services.length === 1}
                    onClick={() => { setServices((prev) => prev.filter((_, i) => i !== index)); clear(); }}
                  >
                    Remove
                  </button>
                </div>
              ))}
              <button
                type="button"
                className={styles.toggle}
                onClick={() => { setServices((prev) => [...prev, emptyService()]); clear(); }}
              >
                Add service
              </button>
              <span className={styles.hint}>Dockerfile and context are relative to the compose file.</span>
            </div>

            <label className={styles.field}>
              <span className={styles.label}>Environment (optional)</span>
              <textarea
                className={styles.textarea}
                value={envText}
                onChange={(e) => { setEnvText(e.target.value); clear(); }}
                placeholder={'KEY=value\nANOTHER=value'}
                rows={4}
              />
              <span className={styles.hint}>One KEY=value per line. Add secrets later if you prefer.</span>
            </label>
          </div>
        )}

        {step === 2 && (
          <div className={styles.stepBody}>
            <h2 className={styles.subtitle}>Where should it run?</h2>
            <p className={styles.lead}>The public address is enough. The rest can be added later.</p>

            <div className={styles.grid}>
              <label className={styles.field}>
                <span className={styles.label}>Public address</span>
                <input
                  className={styles.input}
                  value={host}
                  onChange={onChange(setHost)}
                  placeholder="203.0.113.10"
                  autoComplete="off"
                />
                <span className={styles.hint}>The server&apos;s public IP is enough to start.</span>
              </label>

              <label className={styles.field}>
                <span className={styles.label}>User (optional)</span>
                <input
                  className={styles.input}
                  value={username}
                  onChange={(e) => {
                    const value = e.target.value;
                    setUsername(value);
                    if (!folder.trim() && value.trim()) setFolder(`/home/${value.trim()}/${slug(projectName || 'app')}`);
                    clear();
                  }}
                  placeholder="add it later"
                  autoComplete="off"
                />
                <span className={styles.hint}>The account ShipRight signs in as. Add it when you are ready.</span>
              </label>

              <label className={styles.field}>
                <span className={styles.label}>Folder on the server (optional)</span>
                <input
                  className={styles.input}
                  value={folder}
                  onChange={onChange(setFolder)}
                  placeholder={suggestedFolder || '/home/<user>/<app>'}
                  autoComplete="off"
                />
                <span className={styles.hint}>Leave it empty to use a sensible folder once a user is set.</span>
              </label>

              <label className={styles.field}>
                <span className={styles.label}>Domain (optional)</span>
                <input
                  className={styles.input}
                  value={domain}
                  onChange={onChange(setDomain)}
                  placeholder="add it later"
                  autoComplete="off"
                />
                <span className={styles.hint}>Without one, the app is reachable by address over HTTP.</span>
              </label>

              <label className={styles.field}>
                <span className={styles.label}>SSH key (optional)</span>
                <input
                  className={styles.input}
                  value={sshKeyPath}
                  onChange={onChange(setSshKeyPath)}
                  placeholder="ShipRight makes one when a user is set"
                  autoComplete="off"
                />
                <span className={styles.hint}>Leave it empty and ShipRight creates a key once it knows the user.</span>
              </label>
            </div>
          </div>
        )}

        {step === 3 && (
          <div className={styles.stepBody}>
            <h2 className={styles.subtitle}>Review</h2>
            <p className={styles.lead}>Check the shape, then create. Nothing runs on the server yet.</p>

            <div className={styles.summary}>
              <span className={styles.label}>{projectName || 'App'}</span>
              <span className={styles.hint}>{repoUrl || 'no repository yet'} · {branch}</span>
              <span className={styles.hint}>
                {services.filter((s) => s.name.trim()).map((s) => s.name).join(', ') || 'no services yet'}
              </span>
              <span className={styles.hint}>
                {host || 'no address yet'}{domain ? ` · ${domain}` : ' · address only'}
              </span>
            </div>

            {busy === 'preview' && <span className={styles.hint}>Checking the configuration…</span>}

            {plan && plan.problems.length > 0 && (
              <ul className={styles.problems}>
                {plan.problems.map((problem) => <li key={problem}>{problem}</li>)}
              </ul>
            )}

            {plan && plan.deployBlockers.length > 0 && (
              <div className={styles.deferred}>
                <span className={styles.label}>Saved now, deploy later</span>
                <ul className={styles.warnings}>
                  {plan.deployBlockers.map((item) => <li key={item}>{item}</li>)}
                </ul>
              </div>
            )}

            {plan && plan.warnings.length > 0 && (
              <ul className={styles.warnings}>
                {plan.warnings.map((warning) => <li key={warning}>{warning}</li>)}
              </ul>
            )}

            {plan && plan.steps.length > 0 && (
              <ol className={styles.steps}>
                {plan.steps.map((item) => (
                  <li key={item.id}>
                    <strong>{item.title}</strong>
                    <span className={styles.hint}>{item.detail}</span>
                  </li>
                ))}
              </ol>
            )}
          </div>
        )}

        {error && (
          <div className={styles.problem} role="alert">
            <strong>Not ready.</strong> {error}
            <span className={styles.hint}>Fix the field, then try again.</span>
          </div>
        )}

        <div className={styles.actions}>
          {step > 1 && (
            <ZestButton
              onClick={() => setStep((current) => Math.max(1, current - 1) as Step)}
              zest={{ buttonStyle: 'text', semanticType: 'cancel' }}
            >
              Back
            </ZestButton>
          )}
          {step < 3 && (
            <ZestButton
              onClick={goNext}
              disabled={!canLeave}
              zest={{ semanticType: 'submit', visualOptions: { variant: 'standard' } }}
            >
              Continue
            </ZestButton>
          )}
          {step === 3 && (
            <>
              <ZestButton
                onClick={preview}
                disabled={busy !== null}
                zest={{ buttonStyle: 'outline', semanticType: 'refresh', visualOptions: { size: 'sm' } }}
              >
                {busy === 'preview' ? 'Checking…' : 'Refresh preview'}
              </ZestButton>
              <ZestButton
                onClick={apply}
                disabled={busy !== null || !plan?.ok}
                zest={{ semanticType: 'submit', visualOptions: { variant: 'standard' } }}
              >
                {busy === 'apply' ? 'Writing…' : 'Create deployment'}
              </ZestButton>
            </>
          )}
        </div>

        {step === 1 && !canLeave && (
          <span className={styles.hint}>
            Add a project name, a repository and at least one service with an image.
          </span>
        )}
        {step === 2 && !canLeave && <span className={styles.hint}>Enter the server address to continue.</span>}
        {step === 3 && !plan?.ok && (
          <span className={styles.hint}>ShipRight writes nothing until the preview is clean.</span>
        )}
      </div>
    </section>
  );
}
