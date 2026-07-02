import { useState, useEffect } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import { api } from '@/shared/ApiService';

interface SshKeyStatus {
  exists: boolean;
  publicKey?: string;
  managedSshKey?: boolean;
}

interface Props {
  projectId: string;
}

export default function SshKeySection({ projectId }: Props) {
  const [status, setStatus] = useState<SshKeyStatus | null>(null);
  const [generating, setGenerating] = useState(false);
  const [showAuthorize, setShowAuthorize] = useState(false);
  const [authorizing, setAuthorizing] = useState(false);
  const [password, setPassword] = useState('');
  const [port, setPort] = useState('22');
  const [notice, setNotice] = useState<{ text: string; isError: boolean } | null>(null);

  const loadStatus = async () => {
    try {
      const s = await api.get<SshKeyStatus>(`/api/projects/${projectId}/ssh-key/status`);
      setStatus(s);
    } catch { /* server not reachable yet */ }
  };

  useEffect(() => { loadStatus(); }, [projectId]);

  const handleGenerate = async () => {
    setGenerating(true);
    setNotice(null);
    try {
      await api.post(`/api/projects/${projectId}/ssh-key/generate`, {});
      await loadStatus();
      setNotice({ text: 'SSH key generated. Click "Authorize on Server" to push it.', isError: false });
    } catch {
      setNotice({ text: 'Key generation failed. Ensure ssh-keygen is available on this machine.', isError: true });
    } finally {
      setGenerating(false);
    }
  };

  const handleAuthorize = async () => {
    if (!password) return;
    setAuthorizing(true);
    setNotice(null);
    try {
      await api.post(`/api/projects/${projectId}/ssh-key/authorize`, {
        password,
        port: parseInt(port) || 22,
      });
      setPassword('');
      setShowAuthorize(false);
      setNotice({ text: 'Key authorized. ShipRight will use it for all future deployments.', isError: false });
    } catch {
      setNotice({ text: 'Authorization failed. Check the password and ensure the server is reachable.', isError: true });
    } finally {
      setAuthorizing(false);
    }
  };

  return (
    <div style={{ marginTop: 24, borderTop: '1px solid rgba(255,255,255,0.08)', paddingTop: 20 }}>
      <p style={{ margin: '0 0 4px', fontWeight: 600, color: 'var(--text-primary)', fontSize: 13 }}>
        Managed SSH Key
      </p>
      <p style={{ margin: '0 0 14px', fontSize: 12, color: 'var(--text-secondary)' }}>
        ShipRight can generate and manage an Ed25519 key for this project.
        Authorize it once with your server password and you&apos;ll never be prompted again.
      </p>

      {status?.exists ? (
        <div>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 12, flexWrap: 'wrap' }}>
            <span style={{ color: '#4CAF50', fontSize: 12, display: 'flex', alignItems: 'center', gap: 4 }}>
              ● Key generated
            </span>
            {status.managedSshKey && (
              <span style={{ color: '#4A7FA8', fontSize: 12 }}>· Active for deployments</span>
            )}
            <ZestButton
              zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline' }}
              onClick={handleGenerate}
              disabled={generating}
            >
              {generating ? 'Regenerating…' : 'Regenerate Key'}
            </ZestButton>
          </div>

          {status.publicKey && (
            <details style={{ marginBottom: 12 }}>
              <summary style={{ fontSize: 12, color: 'var(--text-secondary)', cursor: 'pointer' }}>
                View public key (for manual setup)
              </summary>
              <textarea
                readOnly
                value={status.publicKey}
                rows={3}
                onClick={e => (e.target as HTMLTextAreaElement).select()}
                style={{
                  width: '100%', marginTop: 8, background: '#0D1623', color: '#A8B8CC',
                  border: '1px solid rgba(255,255,255,0.08)', borderRadius: 6, padding: '8px',
                  fontFamily: "'JetBrains Mono', monospace", fontSize: 11,
                  boxSizing: 'border-box', resize: 'none',
                }}
              />
            </details>
          )}

          {showAuthorize ? (
            <div>
              <p style={{ margin: '0 0 8px', fontSize: 12, color: 'var(--text-secondary)' }}>
                Enter your server password once — ShipRight will add its key and discard the password immediately.
              </p>
              <div style={{ display: 'flex', gap: 8, marginBottom: 8, flexWrap: 'wrap' }}>
                <input
                  type="number"
                  value={port}
                  onChange={e => setPort(e.target.value)}
                  placeholder="Port"
                  style={{
                    width: 72, background: '#131D30', color: '#F0F2F5',
                    border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
                    padding: '6px 10px', fontSize: 14,
                  }}
                />
                <input
                  type="password"
                  value={password}
                  onChange={e => setPassword(e.target.value)}
                  onKeyDown={e => e.key === 'Enter' && handleAuthorize()}
                  placeholder="Server password"
                  autoComplete="new-password"
                  style={{
                    flex: 1, minWidth: 180, background: '#131D30', color: '#F0F2F5',
                    border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
                    padding: '6px 10px', fontSize: 14,
                  }}
                />
              </div>
              <div style={{ display: 'flex', gap: 8 }}>
                <ZestButton
                  zest={{ visualOptions: { variant: 'standard', size: 'sm' }, buttonStyle: 'solid', semanticType: 'save' }}
                  onClick={handleAuthorize}
                  disabled={authorizing || !password}
                >
                  {authorizing ? 'Authorizing…' : 'Authorize'}
                </ZestButton>
                <ZestButton
                  zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline', semanticType: 'cancel' }}
                  onClick={() => { setShowAuthorize(false); setPassword(''); }}
                >
                  Cancel
                </ZestButton>
              </div>
            </div>
          ) : (
            <ZestButton
              zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline' }}
              onClick={() => setShowAuthorize(true)}
            >
              Authorize on Server
            </ZestButton>
          )}
        </div>
      ) : (
        <ZestButton
          zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline' }}
          onClick={handleGenerate}
          disabled={generating}
        >
          {generating ? 'Generating…' : 'Generate SSH Key'}
        </ZestButton>
      )}

      {notice && (
        <p style={{ marginTop: 12, fontSize: 12, color: notice.isError ? '#FF6B6B' : '#4CAF50' }}>
          {notice.text}
        </p>
      )}
    </div>
  );
}
