import ZestButton from 'jattac.libs.web.zest-button';
import { sseUrl } from '@/shared/ApiService';
import StreamLog from '@/shared/components/StreamLog/StreamLog';
import { useServerOperation } from './useServerOperation';
import styles from './Styles/ServerDetail.module.css';

const LABEL = { bootstrap: 'Prepare', deploy: 'Deploy' } as const;

export default function ServerActionsTab({ serverId, serverName }: { serverId: string; serverName: string }) {
  const op = useServerOperation(serverId);
  const slug = serverName.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'server';

  return (
    <div className={styles.actionsTab}>
      <div className={styles.actionBar}>
        <ZestButton onClick={() => op.start('bootstrap')} disabled={op.running}
          zest={{ buttonStyle: 'outline' }}>{op.running && op.action === 'bootstrap' ? 'Preparing…' : 'Prepare server'}</ZestButton>
        <ZestButton onClick={() => op.start('deploy')} disabled={op.running}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid' }}>{op.running && op.action === 'deploy' ? 'Deploying…' : 'Deploy now'}</ZestButton>
        <a className={styles.linkBtn} href={sseUrl(`/api/servers/${serverId}/checkpoint-log`)} download
          title="Downloads ~/.shipright/checkpoints.log from the server">
          Download server log
        </a>
        {op.action && (
          <span className={`${styles.chip} ${op.running ? styles.chipRun : op.outcome === 'ok' ? styles.chipOk : styles.chipErr}`}>
            {LABEL[op.action]}: {op.running ? 'running…' : op.outcome === 'ok' ? 'succeeded' : 'failed'}
          </span>
        )}
      </div>
      <StreamLog lines={op.lines} isLive={op.running} onClear={op.running ? undefined : op.clear}
        filename={`${slug}-${op.action ?? 'output'}.log`}
        emptyText="Run Prepare or Deploy — output streams here." />
    </div>
  );
}
