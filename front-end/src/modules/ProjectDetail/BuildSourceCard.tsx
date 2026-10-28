import { useState } from 'react';
import toast from 'react-hot-toast';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';

interface Props {
  projectId: string;
  /** Null/undefined means the project has never chosen, which builds in place as it always has. */
  buildSource?: 'InPlace' | 'CleanSnapshot' | null;
}

const cardStyle: React.CSSProperties = {
  marginTop: 16, padding: '14px 16px', borderRadius: 8,
  background: 'var(--bg-card)', border: '1px solid var(--border-subtle)',
};

export default function BuildSourceCard({ projectId, buildSource }: Props) {
  const [clean, setClean] = useState(buildSource === 'CleanSnapshot');
  const [saving, setSaving] = useState(false);

  const change = async (next: boolean) => {
    setSaving(true);
    try {
      await api.post(`/api/projects/${projectId}/build-source`, { buildSource: next ? 'CleanSnapshot' : 'InPlace' });
      setClean(next);
      toast.success(next
        ? 'Builds will now use a clean copy of your saved code.'
        : 'Builds will now use your project folder as it is on disk.');
    } catch (e) {
      toast.error((e as { message?: string })?.message ?? 'Could not change how this project builds.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div style={cardStyle}>
      <label style={{ display: 'flex', gap: 10, alignItems: 'center', cursor: 'pointer', fontSize: 13 }}>
        <input
          type="checkbox"
          aria-label="Build from a clean copy of my saved code"
          checked={clean}
          disabled={saving}
          onChange={e => change(e.target.checked)}
          style={{ accentColor: 'var(--accent)', width: 16, height: 16 }}
        />
        <span>
          Build from a clean copy of my saved code
          <HoverTip label="Build from a clean copy">
            {'Instead of building straight from your folder, ShipRight makes a fresh copy of the last version you saved in git and builds that. Files you have not saved can never sneak into a build, and builds run faster. If something is unsaved, ShipRight tells you what and waits.'}
          </HoverTip>
        </span>
      </label>
      <p style={{ margin: '8px 0 0 26px', fontSize: 12, color: 'var(--text-muted)' }}>
        Your build is exactly what you last saved in git.
      </p>
    </div>
  );
}
