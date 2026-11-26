import { useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';
import { useAuthStore } from '@/shared/auth';
import styles from './MoveToCompanyDialog.module.css';

interface Props {
  kind: 'project' | 'server';
  id: string;
  name: string;
  onClose: () => void;
}

interface MoveError {
  message?: string;
  code?: string;
  isError?: boolean;
}

/**
 * Moves a project or server into another company the person administers. When the resource has a dependency
 * (a project's server, or a server's projects) the first attempt explains it and offers to bring it along; the second
 * attempt sends `includeDependencies`, so a move never leaves a dangling reference behind.
 */
export default function MoveToCompanyDialog({ kind, id, name, onClose }: Props) {
  const user = useAuthStore((s) => s.user);
  const targets = (user?.companies ?? []).filter((c) => !c.current);

  const [target, setTarget] = useState(targets[0]?.id ?? '');
  const [include, setInclude] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [needsDependency, setNeedsDependency] = useState(false);

  const dependencyLabel = kind === 'project'
    ? 'Also move the server this project uses (and its other projects)'
    : 'Also move every project that uses this server';

  const move = async (withDependencies: boolean) => {
    if (!target || busy) return;
    setBusy(true);
    setError('');
    try {
      const result = await api.post<{ message?: string; tailnetCleared?: boolean }>(
        kind === 'project' ? `/api/projects/${id}/move` : `/api/servers/${id}/move`,
        { targetCompanyId: target, includeDependencies: withDependencies },
      );
      toast.success(result.message || `${name} moved.`);
      if (result.tailnetCleared) {
        toast('This server now uses its direct network. Assign a Tailscale network in the new company if you need one.', { icon: 'ℹ️', duration: 7000 });
      }
      window.location.assign(kind === 'project' ? '/projects' : '/servers');
    } catch (caught) {
      const failure = caught as MoveError;
      if (failure.code === 'NeedsServerMove' || failure.code === 'NeedsProjectMove') {
        setNeedsDependency(true);
        setError(failure.message ?? 'This has a dependency that must move too.');
      } else {
        setError(failure.message ?? 'The move could not be completed.');
      }
      setBusy(false);
    }
  };

  return (
    <div className={styles.overlay} role="presentation" onMouseDown={(e) => { if (e.target === e.currentTarget && !busy) onClose(); }}>
      <div className={styles.dialog} role="dialog" aria-modal="true" aria-label={`Move ${kind} to another company`}>
        <div className={styles.head}>
          <div>
            <h2>Move to another company</h2>
            <p>
              <strong>{name}</strong> and everything that belongs to it will leave this company. Nothing is copied and
              nothing is left behind.
            </p>
          </div>
          <button type="button" className={styles.close} onClick={onClose} disabled={busy} aria-label="Close">✕</button>
        </div>

        {targets.length === 0 ? (
          <div className={styles.empty}>
            <p>You need a second company to move this into.</p>
            <p className={styles.hint}>Create one in Settings, then come back here.</p>
          </div>
        ) : (
          <>
            <label className={styles.field}>
              <span>Destination company</span>
              <select value={target} onChange={(e) => setTarget(e.target.value)} disabled={busy}>
                {targets.map((company) => (
                  <option key={company.id} value={company.id}>{company.name} · {company.role}</option>
                ))}
              </select>
            </label>

            <label className={styles.check}>
              <input
                type="checkbox"
                checked={include || needsDependency}
                onChange={(e) => setInclude(e.target.checked)}
                disabled={busy}
              />
              <span>{dependencyLabel}</span>
            </label>

            <p className={styles.hint}>
              You must be an Admin of both companies. Backups, build history and schedules move with a project.
            </p>
          </>
        )}

        {error && <p role="alert" className={styles.error}>{error}</p>}

        <div className={styles.actions}>
          <ZestButton onClick={onClose} disabled={busy} zest={{ buttonStyle: 'text', semanticType: 'cancel' }}>
            Cancel
          </ZestButton>
          <ZestButton
            onClick={() => void move(include || needsDependency)}
            disabled={busy || !target}
            zest={{ semanticType: 'save', visualOptions: { variant: 'standard' } }}
          >
            {busy ? 'Moving…' : 'Move'}
          </ZestButton>
        </div>
      </div>
    </div>
  );
}
