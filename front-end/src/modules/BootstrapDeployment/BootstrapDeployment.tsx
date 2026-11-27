import { useMemo, useState } from 'react';
import { useRouter } from 'next/router';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import {
  BootstrapApplyResponse,
  BootstrapApplyResult,
  BootstrapPlan,
  BootstrapRequest,
  GitInspection,
} from '@/shared/types/IBootstrap';
import styles from './BootstrapDeployment.module.css';

type Preset = 'self' | 'custom';

interface ServiceRow {
  name: string;
  image: string;
  dockerfile: string;
  context: string;
}

const emptyService = (): ServiceRow => ({ name: 'app', image: '', dockerfile: 'Dockerfile', context: '.' });

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

interface Props {
  /** Cloud installs do not map the bootstrap routes: they change a person's own machine and a server. */
  cloud: boolean;
}

/**
 * Writes a complete deployment from scratch: the server, the project and the build-on-server settings. The address
 * is the only thing required; the user, key, folder and domain can all be added later. It previews first and never
 * deploys.
 */
export default function BootstrapDeployment({ cloud }: Props) {
  const router = useRouter();

  const [preset, setPreset] = useState<Preset>('self');

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
  const [services, setServices] = useState<ServiceRow[]>([emptyService()]);
  const [envText, setEnvText] = useState('');
  const [gitInfo, setGitInfo] = useState<GitInspection | null>(null);
  const [gitBusy, setGitBusy] = useState(false);
  const [gitError, setGitError] = useState<string | null>(null);

  const [advanced, setAdvanced] = useState(false);
  const [plan, setPlan] = useState<BootstrapPlan | null>(null);
  const [result, setResult] = useState<BootstrapApplyResult | null>(null);
  const [busy, setBusy] = useState<'preview' | 'apply' | null>(null);
  const [error, setError] = useState<string | null>(null);

  const suggestedFolder = useMemo(
    () => (username.trim() ? `/home/${username.trim()}/shipright` : ''),
    [username],
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

  const buildRequest = (): BootstrapRequest => {
    const target = {
      host: host.trim(),
      username: username.trim(),
      remoteWorkingDir: folder.trim(),
      domain: domain.trim(),
      sshKeyPath: sshKeyPath.trim(),
    };

    if (preset === 'self') {
      return {
        ...target,
        preset: 'ShipRightSelf',
        repoUrl: repoUrl.trim() || undefined,
        branch: branch.trim() || 'master',
      };
    }

    return {
      ...target,
      preset: 'None',
      projectName: projectName.trim(),
      repoUrl: repoUrl.trim(),
      branch: branch.trim() || 'master',
      composeFile: composeFile.trim() || 'docker-compose.yml',
      composeProject: composeProject.trim() || undefined,
      services: services
        .filter((row) => row.name.trim() || row.image.trim())
        .map((row) => ({
          name: row.name.trim(),
          image: row.image.trim(),
          dockerfile: row.dockerfile.trim(),
          context: row.context.trim() || '.',
        })),
      env: parseEnv(envText),
    };
  };

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

  if (cloud) {
    return (      <section className={styles.card}>
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
              onClick={() => { setResult(null); setPlan(null); }}
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
        <h1 className={styles.title}>Set up a deployment from scratch</h1>
        <p className={styles.lead}>
          Start with the public address. Add the user, key and domain whenever you like — nothing is required beyond
          the address.
        </p>
      </header>

      <div className={styles.card}>
        <label className={styles.field}>
          <span className={styles.label}>What are you deploying?</span>
          <select
            className={styles.input}
            value={preset}
            onChange={(e) => { setPreset(e.target.value as Preset); clear(); }}
          >
            <option value="self">ShipRight (this product)</option>
            <option value="custom">Another app</option>
          </select>
        </label>

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
              onChange={onChange(setUsername)}
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

        {preset === 'self' ? (
          <>
            <button type="button" className={styles.toggle} onClick={() => setAdvanced((v) => !v)}>
              {advanced ? 'Hide advanced' : 'Advanced'}
            </button>

            {advanced && (
              <div className={styles.grid}>
                <label className={styles.field}>
                  <span className={styles.label}>Repository (optional)</span>
                  <input
                    className={styles.input}
                    value={repoUrl}
                    onChange={onChange(setRepoUrl)}
                    placeholder="the ShipRight repository"
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
              </div>
            )}
          </>
        ) : (
          <>
            <div className={styles.grid}>
              <label className={styles.field}>
                <span className={styles.label}>Project name</span>
                <input
                  className={styles.input}
                  value={projectName}
                  onChange={onChange(setProjectName)}
                  placeholder="shop"
                  autoComplete="off"
                />
              </label>

              <label className={styles.field}>
                <span className={styles.label}>Repository</span>
                <input
                  className={styles.input}
                  value={repoUrl}
                  onChange={onChange(setRepoUrl)}
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
            </div>

            <div className={styles.inlineActions}>
              <ZestButton
                onClick={inspectRepo}
                disabled={gitBusy || !repoUrl.trim()}
                zest={{ buttonStyle: 'outline', semanticType: 'submit', visualOptions: { size: 'sm' } }}
              >
                {gitBusy ? 'Checking…' : 'Check repository'}
              </ZestButton>
              <span className={styles.hint}>Reads the branch, compose file and Dockerfiles from GitHub.</span>
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
                {gitInfo.dockerfiles.length > 0 && (
                  <span className={styles.hint}>Dockerfiles: {gitInfo.dockerfiles.join(', ')}</span>
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
          </>
        )}

        {error && (
          <div className={styles.problem} role="alert">
            <strong>Not ready.</strong> {error}
            <span className={styles.hint}>Fix the field above, then preview again.</span>
          </div>
        )}

        <div className={styles.actions}>
          <ZestButton
            onClick={preview}
            disabled={busy !== null}
            zest={{ buttonStyle: 'outline', semanticType: 'submit', visualOptions: { variant: 'standard' } }}
          >
            {busy === 'preview' ? 'Checking…' : 'Preview'}
          </ZestButton>
          <ZestButton
            onClick={apply}
            disabled={busy !== null || !plan?.ok}
            zest={{ semanticType: 'submit', visualOptions: { variant: 'standard' } }}
          >
            {busy === 'apply' ? 'Writing…' : 'Create deployment'}
          </ZestButton>
        </div>

        {!plan?.ok && (
          <span className={styles.hint}>
            Preview first. ShipRight writes nothing until the plan is clean.
          </span>
        )}
      </div>

      {plan && (
        <div className={styles.card}>
          <h2 className={styles.subtitle}>{plan.ok ? 'Ready to create' : 'Still needs attention'}</h2>

          <p className={styles.fact}>
            This writes a server record, a project and the deploy settings on this computer. Nothing runs on the
            server until you deploy.
          </p>

          {plan.problems.length > 0 && (
            <ul className={styles.problems}>
              {plan.problems.map((problem) => <li key={problem}>{problem}</li>)}
            </ul>
          )}

          {plan.deployBlockers.length > 0 && (
            <div className={styles.deferred}>
              <span className={styles.label}>Saved now, deploy later</span>
              <ul className={styles.warnings}>
                {plan.deployBlockers.map((item) => <li key={item}>{item}</li>)}
              </ul>
            </div>
          )}

          {plan.warnings.length > 0 && (
            <ul className={styles.warnings}>
              {plan.warnings.map((warning) => <li key={warning}>{warning}</li>)}
            </ul>
          )}

          <ol className={styles.steps}>
            {plan.steps.map((step) => (
              <li key={step.id}>
                <strong>{step.title}</strong>
                <span className={styles.hint}>{step.detail}</span>
              </li>
            ))}
          </ol>
        </div>
      )}
    </section>
  );
}
