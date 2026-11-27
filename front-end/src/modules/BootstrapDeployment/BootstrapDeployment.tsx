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
} from '@/shared/types/IBootstrap';
import styles from './BootstrapDeployment.module.css';

const DEFAULT_USER = 'ubuntu';

interface Props {
  /** Cloud installs do not map the bootstrap routes: they change a person's own machine and a server. */
  cloud: boolean;
}

/**
 * Writes a complete deployment from scratch: the server, the project and the build-on-server settings. It previews
 * first, so a person sees the consequences before anything is written, and it never deploys.
 */
export default function BootstrapDeployment({ cloud }: Props) {
  const router = useRouter();

  const [host, setHost] = useState('');
  const [username, setUsername] = useState(DEFAULT_USER);
  const [folder, setFolder] = useState('');
  const [domain, setDomain] = useState('');
  const [sshKeyPath, setSshKeyPath] = useState('');
  const [advanced, setAdvanced] = useState(false);
  const [repoUrl, setRepoUrl] = useState('');
  const [branch, setBranch] = useState('master');

  const [plan, setPlan] = useState<BootstrapPlan | null>(null);
  const [result, setResult] = useState<BootstrapApplyResult | null>(null);
  const [busy, setBusy] = useState<'preview' | 'apply' | null>(null);
  const [error, setError] = useState<string | null>(null);

  const suggestedFolder = useMemo(() => {
    const user = username.trim() || DEFAULT_USER;
    return `/home/${user}/shipright`;
  }, [username]);

  const effectiveFolder = folder.trim() || suggestedFolder;

  const onChange = (set: (value: string) => void) => (event: React.ChangeEvent<HTMLInputElement>) => {
    set(event.target.value);
    setPlan(null);
    setResult(null);
    setError(null);
  };

  const buildRequest = (): BootstrapRequest => ({
    preset: 'ShipRightSelf',
    host: host.trim(),
    username: username.trim(),
    remoteWorkingDir: effectiveFolder,
    domain: domain.trim(),
    sshKeyPath: sshKeyPath.trim(),
    repoUrl: repoUrl.trim() || undefined,
    branch: branch.trim() || 'master',
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
          ShipRight writes the server, the project and the deploy settings. Preview first: nothing runs until you
          confirm.
        </p>
      </header>

      <div className={styles.card}>
        <div className={styles.grid}>
          <label className={styles.field}>
            <span className={styles.label}>Server address</span>
            <input
              className={styles.input}
              value={host}
              onChange={onChange(setHost)}
              placeholder="staging.example.com"
              autoComplete="off"
            />
            <span className={styles.hint}>The box that will run ShipRight. Use the address you SSH to.</span>
          </label>

          <label className={styles.field}>
            <span className={styles.label}>User</span>
            <input
              className={styles.input}
              value={username}
              onChange={onChange(setUsername)}
              placeholder={DEFAULT_USER}
              autoComplete="off"
            />
            <span className={styles.hint}>The account ShipRight signs in as.</span>
          </label>

          <label className={styles.field}>
            <span className={styles.label}>Folder on the server</span>
            <input
              className={styles.input}
              value={folder}
              onChange={onChange(setFolder)}
              placeholder={suggestedFolder}
              autoComplete="off"
            />
            <span className={styles.hint}>Where the app lives. Leave it empty to use the suggestion.</span>
          </label>

          <label className={styles.field}>
            <span className={styles.label}>Domain</span>
            <input
              className={styles.input}
              value={domain}
              onChange={onChange(setDomain)}
              placeholder="staging.shipright.example.com"
              autoComplete="off"
            />
            <span className={styles.hint}>The address people will visit. The front door gets its certificate from it.</span>
          </label>

          <label className={styles.field}>
            <span className={styles.label}>SSH key (optional)</span>
            <input
              className={styles.input}
              value={sshKeyPath}
              onChange={onChange(setSshKeyPath)}
              placeholder="ShipRight makes one"
              autoComplete="off"
            />
            <span className={styles.hint}>Leave it empty and ShipRight creates a key for this server.</span>
          </label>
        </div>

        <button type="button" className={styles.toggle} onClick={() => setAdvanced((v) => !v)}>
          {advanced ? 'Hide advanced' : 'Advanced'}
        </button>

        {advanced && (
          <div className={styles.grid}>
            <label className={styles.field}>
              <span className={styles.label}>Repository</span>
              <input
                className={styles.input}
                value={repoUrl}
                onChange={onChange(setRepoUrl)}
                placeholder="git@github.com:owner/repo.git"
                autoComplete="off"
              />
              <span className={styles.hint}>Leave it empty to use the ShipRight repository.</span>
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
