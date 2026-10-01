import React, { useEffect, useRef, useState } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { api, sseUrl } from '@/shared/ApiService';
import HoverTip from '@/shared/components/HoverTip';
import styles from './Styles/ServerDeploySection.module.css';

export interface IServerDeployValues {
  composeRepoUrl?: string;
  ecrRegistry?: string;
  ecrRegion?: string;
  healthCheckUrl?: string;
  envFile?: string;
  skipHealthCheck?: boolean;
}

type TextField = Exclude<keyof IServerDeployValues, 'skipHealthCheck'>;

interface Props {
  serverId?: string;
  values: IServerDeployValues;
  onChange: (field: keyof IServerDeployValues, value: string | boolean) => void;
}

interface OutLine { id: number; kind: 'log' | 'ok' | 'error'; text: string; }

export default function ServerDeploySection({ serverId, values, onChange }: Props) {
  const [lines, setLines] = useState<OutLine[]>([]);
  const [running, setRunning] = useState(false);
  const esRef = useRef<EventSource | null>(null);
  const idRef = useRef(0);
  const endRef = useRef<HTMLDivElement>(null);

  useEffect(() => () => { esRef.current?.close(); }, []);
  useEffect(() => { endRef.current?.scrollIntoView?.({ behavior: 'smooth' }); }, [lines]);

  const push = (kind: OutLine['kind'], text: string) =>
    setLines(prev => [...prev, { id: idRef.current++, kind, text }]);

  const start = async (action: 'bootstrap' | 'deploy') => {
    if (!serverId || running) return;
    setRunning(true);
    setLines([]);
    try {
      const { opId } = await api.post<{ opId: string }>(`/api/servers/${serverId}/${action}`, {});
      const es = new EventSource(sseUrl(`/api/servers/${serverId}/ssh/ops/${opId}/stream`));
      esRef.current = es;

      es.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data);
          if (data.type === 'log') {
            push('log', data.data?.line ?? '');
          } else if (data.type === 'done') {
            const ok = (data.data?.exitCode ?? 0) === 0;
            push(ok ? 'ok' : 'error', `${ok ? '✓' : '✗'} ${data.data?.message ?? (ok ? 'Done' : 'Failed')}`);
            setRunning(false);
            es.close();
          } else if (data.type === 'error') {
            push('error', `✗ ${data.data?.message ?? 'Server error'}`);
            setRunning(false);
            es.close();
          }
        } catch { /* ignore malformed */ }
      };
      es.onerror = () => {
        push('error', '✗ Connection lost');
        setRunning(false);
        es.close();
      };
    } catch (e: unknown) {
      push('error', `✗ ${(e as { message?: string })?.message ?? 'Failed to start'}`);
      setRunning(false);
    }
  };

  const text = (field: TextField, label: string, placeholder: string, tip: React.ReactNode) => (
    <div className={styles.row}>
      <label className={styles.label}>{label}<HoverTip label={label}>{tip}</HoverTip></label>
      <ZestTextbox value={values[field] ?? ''} placeholder={placeholder}
        onChange={e => onChange(field, e.target.value)} zest={{ stretch: true }} />
    </div>
  );

  return (
    <div className={styles.section}>
      <h4 className={styles.title}>Remote deploy (EC2)</h4>
      <p className={styles.hint}>
        Attach an IAM instance role with ECR read access to the server, then use
        “Prepare server” once and “Deploy now” for every release.
      </p>

      {text('composeRepoUrl', 'Compose repo URL', 'https://github.com/you/repo.git',
        'The web address of the Git repository that holds your docker-compose.yml file. ShipRight downloads it onto the server.\n\n' +
        'Where to get it: open the repository on GitHub, click the green "Code" button, choose HTTPS, and copy the link (it ends in .git).')}
      {text('ecrRegistry', 'ECR registry', '123456789012.dkr.ecr.us-east-2.amazonaws.com',
        'ECR is Amazon\'s storage for your app images. This is the address of your storage, so the server knows where to download from.\n\n' +
        'Where to get it: sign in to the AWS Console, search for "ECR", open "Repositories", and copy the URI of your repository. ' +
        'Only keep the part before the first "/" (it looks like 123456789012.dkr.ecr.us-east-2.amazonaws.com).\n\n' +
        'Leave blank if your images are not stored on Amazon ECR.')}
      {text('ecrRegion', 'ECR region', 'us-east-2',
        'The AWS location where your images are stored, for example us-east-2.\n\n' +
        'Where to get it: it is the part right after "ecr." in the registry address above, and it is also shown at the top-right of the AWS Console. ' +
        'Leave blank and ShipRight will read it from the registry address.')}
      <div className={styles.row}>
        <span className={styles.checkRow}>
          <input id="health-check-toggle" type="checkbox" checked={!values.skipHealthCheck}
            onChange={e => onChange('skipHealthCheck', !e.target.checked)} />
          <label htmlFor="health-check-toggle" className={styles.label}>Check that the app is running after each deploy</label>
          <HoverTip label="Check that the app is running">
            {'After each deploy ShipRight visits your app from the server to confirm it started properly, and tells you if it did not. This is recommended.\n\n' +
              'Turn this off if your app has no status page, or you would rather check it yourself. The deploy is then reported as done as soon as the containers have been restarted.'}
          </HoverTip>
        </span>
      </div>
      {!values.skipHealthCheck && text('healthCheckUrl', 'Health check URL (optional)', 'http://localhost:5200/api/health',
        'The address ShipRight visits from the server to confirm your app started properly. This is optional.\n\n' +
        'Leave blank to use the standard ShipRight address (http://localhost:5200/api/health). ' +
        'If you are deploying a different app, enter that app\'s own status page as seen from the server, for example http://localhost:8080/health.')}

      <div className={styles.row}>
        <label className={styles.label}>.env contents
          <HoverTip label=".env contents">
            {'These are the private settings your app needs to run, such as passwords, secret keys and email details. ' +
              'Write one per line as NAME=value. ShipRight saves them in a protected file on the server called .env.\n\n' +
              'Where to get them: your compose repo contains a file called .env.example that lists every setting. Copy its contents here and replace each example value with your real one. ' +
              'For secret keys, use a long random text. Never share these values.'}
          </HoverTip>
        </label>
        <textarea className={styles.env} rows={8} spellCheck={false}
          value={values.envFile ?? ''} placeholder={'KEY=value\nANOTHER=value'}
          onChange={e => onChange('envFile', e.target.value)} />
        <span className={styles.hint}>Written to the working directory as .env (chmod 600) on every prepare/deploy.</span>
      </div>

      <div className={styles.actions}>
        <ZestButton onClick={() => start('bootstrap')} disabled={!serverId || running}
          zest={{ buttonStyle: 'outline' }}>Prepare server</ZestButton>
        <ZestButton onClick={() => start('deploy')} disabled={!serverId || running}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid' }}>Deploy now</ZestButton>
      </div>
      {!serverId && <p className={styles.hint}>Save the server first, then these actions become available. Actions use the saved settings.</p>}

      {lines.length > 0 && (
        <div className={styles.output}>
          {lines.map(l => (
            <span key={l.id} className={`${styles.line} ${l.kind === 'ok' ? styles.ok : l.kind === 'error' ? styles.err : ''}`}>
              {l.text}
            </span>
          ))}
          <div ref={endRef} />
        </div>
      )}
    </div>
  );
}
