import { useState } from 'react';
import Link from 'next/link';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import FilePicker from '@/modules/FilePicker/FilePicker';
import { IServerConfig } from '@/shared/types/IProject';
import styles from '../Styles/ProjectSetupWizard.module.css';

export interface ServerDraft {
  host: string;
  username: string;
  sshKeyPath: string;
  remoteWorkingDir: string;
  rebuildScript: string;
}

interface Props {
  draft: ServerDraft;
  serverId: string;
  globalServers: IServerConfig[];
  errors: Record<string, string>;
  onDraftChange: (patch: Partial<ServerDraft>) => void;
  onServerIdChange: (id: string) => void;
  onBrowseRemoteDir: (dir: string) => void;
}

export default function ServerAtom({
  draft, serverId, globalServers, errors, onDraftChange, onServerIdChange, onBrowseRemoteDir,
}: Props) {
  const [showSshPicker, setShowSshPicker] = useState(false);
  const [showRemotePicker, setShowRemotePicker] = useState(false);
  const [legacyMode, setLegacyMode] = useState(false);

  const applyGlobalServer = (id: string) => {
    onServerIdChange(id);
    const s = globalServers.find(g => g.id === id);
    if (s) {
      onDraftChange({
        host: s.host,
        username: s.username,
        sshKeyPath: s.sshKeyPath,
        remoteWorkingDir: s.remoteWorkingDir,
        rebuildScript: s.rebuildScript,
      });
      onBrowseRemoteDir(s.remoteWorkingDir);
    }
  };

  return (
    <>
      {globalServers.length > 0 && (
        <div className={styles.section}>
          <span className={styles.sectionTitle}>Linked Server <span style={{ color: '#637389', fontWeight: 400 }}>(optional)</span></span>
          <div className={styles.fieldRow}>
            <label className={styles.fieldLabel}>Choose a global server to pre-fill the fields below.</label>
            <select value={serverId} onChange={e => applyGlobalServer(e.target.value)}
              style={{ background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', width: '100%' }}>
              <option value="">— Manual entry —</option>
              {globalServers.map(s => (
                <option key={s.id} value={s.id!}>{s.name || s.host}</option>
              ))}
            </select>
            <Link href="/servers/" style={{ fontSize: 12, color: '#C9A84C' }}>Manage servers →</Link>
          </div>
        </div>
      )}

      <div className={styles.section}>
        <div className={styles.fieldRow}>
          <label className={styles.fieldLabel}>Host (IP or hostname)</label>
          <ZestTextbox value={draft.host} onChange={e => onDraftChange({ host: e.target.value })}
            placeholder="3.130.65.46" zest={{ stretch: true }} />
          {errors['server.host'] && <p className={styles.errorText}>{errors['server.host']}</p>}
        </div>

        <div className={styles.fieldRow}>
          <label className={styles.fieldLabel}>Username</label>
          <ZestTextbox value={draft.username} onChange={e => onDraftChange({ username: e.target.value })}
            placeholder="ubuntu" zest={{ stretch: true }} />
          {errors['server.username'] && <p className={styles.errorText}>{errors['server.username']}</p>}
        </div>

        <div className={styles.fieldRow}>
          <label className={styles.fieldLabel}>SSH key (.pem)</label>
          {showSshPicker
            ? <FilePicker
                storageKey="ssh-key"
                onSelect={p => { onDraftChange({ sshKeyPath: p }); setShowSshPicker(false); }}
                label="Navigate to your .pem key file"
              />
            : <div className={styles.inputRow}>
                <ZestTextbox value={draft.sshKeyPath} onChange={e => onDraftChange({ sshKeyPath: e.target.value })}
                  placeholder="/home/nyingi/.../.pem" zest={{ stretch: true }} />
                <ZestButton onClick={() => setShowSshPicker(true)} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Browse</ZestButton>
              </div>
          }
          {errors['server.sshKeyPath'] && <p className={styles.errorText}>{errors['server.sshKeyPath']}</p>}
        </div>

        <div className={styles.fieldRow}>
          <label className={styles.fieldLabel}>Remote working directory</label>
          {showRemotePicker
            ? <FilePicker
                dirsOnly
                sshConfig={{ host: draft.host, user: draft.username, keyPath: draft.sshKeyPath }}
                initialPath={draft.remoteWorkingDir || undefined}
                label={`Browsing ${draft.username}@${draft.host}`}
                onSelect={p => { onDraftChange({ remoteWorkingDir: p }); setShowRemotePicker(false); onBrowseRemoteDir(p); }}
              />
            : <div className={styles.inputRow}>
                <ZestTextbox value={draft.remoteWorkingDir} onChange={e => onDraftChange({ remoteWorkingDir: e.target.value })}
                  placeholder="/home/ubuntu/jattac-sms-gateway-docker" zest={{ stretch: true }} />
                <ZestButton
                  onClick={() => setShowRemotePicker(true)}
                  zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                  disabled={!draft.host || !draft.username || !draft.sshKeyPath}>
                  Browse
                </ZestButton>
              </div>
          }
          {errors['server.remoteWorkingDir'] && <p className={styles.errorText}>{errors['server.remoteWorkingDir']}</p>}
        </div>

        <div className={styles.fieldRow} style={{ flexDirection: 'row', alignItems: 'center', gap: 10 }}>
          <label className={styles.fieldLabel} style={{ marginBottom: 0 }}>Legacy project</label>
          <input type="checkbox" checked={legacyMode} onChange={e => setLegacyMode(e.target.checked)}
            style={{ accentColor: '#C9A84C', width: 16, height: 16 }} />
          <span style={{ fontSize: 12, color: '#637389' }}>Show rebuild script field</span>
        </div>
        {legacyMode && (
          <div className={styles.fieldRow}>
            <label className={styles.fieldLabel}>Rebuild script</label>
            <ZestTextbox value={draft.rebuildScript} onChange={e => onDraftChange({ rebuildScript: e.target.value })}
              placeholder="rebuild.sh" zest={{ stretch: true }} />
            {errors['server.rebuildScript'] && <p className={styles.errorText}>{errors['server.rebuildScript']}</p>}
          </div>
        )}
      </div>
    </>
  );
}
