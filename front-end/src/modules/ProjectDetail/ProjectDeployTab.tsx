import ZestButton from 'jattac.libs.web.zest-button';
import StreamLog from '@/shared/components/StreamLog/StreamLog';
import { useServerOperation } from '@/modules/ServerDetail/useServerOperation';

interface Props {
  serverId?: string;
  projectName: string;
}

const chipStyle = (color: string) => ({
  fontSize: 12, padding: '3px 10px', borderRadius: 999, border: `1px solid ${color}`, color,
});

/** Deploys the project's server (pull images, compose up) and streams the output live. */
export default function ProjectDeployTab({ serverId, projectName }: Props) {
  const op = useServerOperation(serverId);
  const slug = projectName.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'project';

  if (!serverId) {
    return <p style={{ color: '#A8B8CC' }}>This project has no server configured, so there is nothing to deploy to.</p>;
  }

  const status = op.running ? 'running…' : op.outcome === 'ok' ? 'succeeded' : 'failed';
  const statusColor = op.running ? '#C9A84C' : op.outcome === 'ok' ? '#4CAF7D' : '#E5604D';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <ZestButton onClick={() => op.start('deploy')} disabled={op.running}
          zest={{ visualOptions: { variant: 'standard' }, buttonStyle: 'solid' }}>
          {op.running ? 'Deploying…' : 'Deploy now'}
        </ZestButton>
        {op.action && <span style={chipStyle(statusColor)}>Deploy: {status}</span>}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', height: 480 }}>
        <StreamLog lines={op.lines} isLive={op.running} onClear={op.running ? undefined : op.clear}
          filename={`${slug}-deploy.log`} emptyText="Press Deploy now — output streams here." />
      </div>
    </div>
  );
}
