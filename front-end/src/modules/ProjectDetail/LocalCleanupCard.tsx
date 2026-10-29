import { useState } from 'react';
import toast from 'react-hot-toast';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';

interface ServiceLike {
  name: string;
  skipLocalCleanup?: boolean | null;
  /** Newest versions kept on this computer; 0 deletes an image as soon as it has been pushed. Default 2. */
  localImageKeepCount?: number | null;
}

interface Props {
  projectId: string;
  services: ServiceLike[];
}

const cardStyle: React.CSSProperties = {
  marginTop: 16, padding: '14px 16px', borderRadius: 8,
  background: 'var(--bg-card)', border: '1px solid var(--border-subtle)',
};

function describe(s: ServiceLike, kept: boolean): string {
  if (kept) return 'Never cleaned up — every image stays on this computer.';
  const keep = s.localImageKeepCount ?? 2;
  return keep === 0
    ? 'Images are deleted from this computer right after they are pushed (the registry keeps them).'
    : `The newest ${keep} version${keep === 1 ? ' is' : 's are'} kept; older ones are deleted after a push.`;
}

export default function LocalCleanupCard({ projectId, services }: Props) {
  const [kept, setKept] = useState<Record<string, boolean>>(
    () => Object.fromEntries(services.map(s => [s.name, !!s.skipLocalCleanup])));
  const [saving, setSaving] = useState<string | null>(null);

  if (services.length === 0) return null;

  const change = async (name: string, skip: boolean) => {
    setSaving(name);
    try {
      await api.post(`/api/projects/${projectId}/local-cleanup`, { service: name, skipLocalCleanup: skip });
      setKept(prev => ({ ...prev, [name]: skip }));
      toast.success(skip
        ? `${name} images will stay on this computer.`
        : `${name} images will be cleaned up after each push.`);
    } catch (e) {
      toast.error((e as { message?: string })?.message ?? 'Could not save this setting.');
    } finally {
      setSaving(null);
    }
  };

  return (
    <div style={cardStyle}>
      <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 8 }}>
        Free disk space after pushing
        <HoverTip label="Free disk space after pushing">
          {'Once an image is safely in your registry, the copy on this computer only takes up space. ShipRight deletes it after a successful push — never before, and never an image a container is using. Switch this on for a service if you want its images to stay here.'}
        </HoverTip>
      </div>
      {services.map(s => (
        <div key={s.name} style={{ marginTop: 8 }}>
          <label style={{ display: 'flex', gap: 10, alignItems: 'center', cursor: 'pointer', fontSize: 13 }}>
            <input
              type="checkbox"
              aria-label={`Keep ${s.name} images on this computer`}
              checked={!!kept[s.name]}
              disabled={saving === s.name}
              onChange={e => change(s.name, e.target.checked)}
              style={{ accentColor: 'var(--accent)', width: 16, height: 16 }}
            />
            <span>Keep {s.name} images on this computer</span>
          </label>
          <p style={{ margin: '4px 0 0 26px', fontSize: 12, color: 'var(--text-muted)' }}>{describe(s, !!kept[s.name])}</p>
        </div>
      ))}
    </div>
  );
}
