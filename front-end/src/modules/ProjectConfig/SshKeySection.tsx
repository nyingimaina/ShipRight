import { useState, useEffect, useRef } from 'react';
import ZestButton from 'jattac.libs.web.zest-button';
import InfoTip from '@/shared/components/InfoTip';
import { api } from '@/shared/ApiService';

interface SshKeyStatus {
  exists: boolean;
  publicKey?: string;
  managedSshKey?: boolean;
  authorized?: boolean;
}

interface SavedKeyFile {
  id: string;
  name: string;
}

interface Props {
  projectId?: string;
  apiBase?: string;
  /** Server settings only: offer a saved key file as well as a password (R2). */
  allowSavedKeyFile?: boolean;
}

type AuthMode = 'password' | 'keyfile';

export default function SshKeySection({ projectId, apiBase: apiBaseProp, allowSavedKeyFile = false }: Props) {
  const apiBase = apiBaseProp ?? `/api/projects/${projectId}/ssh-key`;
  const [status, setStatus] = useState<SshKeyStatus | null>(null);
  const [generating, setGenerating] = useState(false);
  const [showAuthorize, setShowAuthorize] = useState(false);
  const [authorizing, setAuthorizing] = useState(false);
  const [mode, setMode] = useState<AuthMode>('password');
  const [password, setPassword] = useState('');
  const [port, setPort] = useState('22');
  const [keyFiles, setKeyFiles] = useState<SavedKeyFile[]>([]);
  const [selectedKeyFileId, setSelectedKeyFileId] = useState('');
  const [fieldErrors, setFieldErrors] = useState<{ password?: string; keyFileResourceId?: string }>({});
  const [notice, setNotice] = useState<{ text: string; isError: boolean } | null>(null);
  const authorizingRef = useRef(false);

  const loadStatus = async () => {
    try {
      setStatus(await api.get<SshKeyStatus>(`${apiBase}/status`));
    } catch { /* server not reachable yet */ }
  };

  useEffect(() => { loadStatus(); }, [apiBase]);

  // Saved key files are only fetched when the Authorize step is opened, so a closed step does no work (PERF-001).
  const loadKeyFiles = async () => {
    try {
      const res = await api.get<{ items: SavedKeyFile[] }>('/api/resources/key-files?page=1&pageSize=100');
      setKeyFiles(res.items ?? []);
    } catch {
      setKeyFiles([]);
    }
  };

  const openAuthorize = () => {
    setFieldErrors({});
    setShowAuthorize(true);
    if (allowSavedKeyFile) loadKeyFiles();
  };

  const handleGenerate = async () => {
    setGenerating(true);
    setNotice(null);
    try {
      await api.post(`${apiBase}/generate`, {});
      await loadStatus();
      setNotice({
        text: 'Managed key replaced. Authorize it on the server again — the previous authorization no longer applies.',
        isError: false,
      });
    } catch (e: any) {
      setNotice({
        text: e?.message
          ? `Key generation failed: ${e.message}`
          : 'Key generation failed. Ensure ssh-keygen is available on this machine.',
        isError: true,
      });
    } finally {
      setGenerating(false);
    }
  };

  const handleAuthorize = async () => {
    const errors: { password?: string; keyFileResourceId?: string } = {};
    if (mode === 'password' && !password) {
      errors.password = 'Enter the server password, or choose a saved key file.';
    }
    if (mode === 'keyfile' && !selectedKeyFileId) {
      errors.keyFileResourceId = 'Choose a saved key file.';
    }
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) return;
    if (authorizingRef.current) return;

    authorizingRef.current = true;
    setAuthorizing(true);
    setNotice(null);
    try {
      const payload = mode === 'keyfile'
        ? { mode: 'keyfile', keyFileResourceId: selectedKeyFileId, port: parseInt(port) || 22 }
        : { password, port: parseInt(port) || 22 };
      await api.post(`${apiBase}/authorize`, payload);
      setPassword('');
      setSelectedKeyFileId('');
      setShowAuthorize(false);
      setNotice({ text: 'Key authorized. ShipRight will use it for all future deployments.', isError: false });
      await loadStatus();
    } catch (e: any) {
      setNotice({
        text: e?.message
          ? `Authorization failed: ${e.message}`
          : 'Authorization failed. Check the password and ensure the server is reachable.',
        isError: true,
      });
    } finally {
      authorizingRef.current = false;
      setAuthorizing(false);
    }
  };

  const isActive = status?.authorized ?? status?.managedSshKey;

  return (
    <div style={{ marginTop: 24, borderTop: '1px solid rgba(255,255,255,0.08)', paddingTop: 20 }}>
      <p style={{ margin: '0 0 4px', fontWeight: 600, color: 'var(--text-primary)', fontSize: 13 }}>
        Managed SSH Key
      </p>
      <p style={{ margin: '0 0 14px', fontSize: 12, color: 'var(--text-secondary)' }}>
        ShipRight can generate and manage an Ed25519 key for this project.
        Authorize it once and you&apos;ll never be prompted again.
      </p>

      {status?.exists ? (
        <div>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 12, flexWrap: 'wrap' }}>
            <span style={{ color: '#4CAF50', fontSize: 12, display: 'flex', alignItems: 'center', gap: 4 }}>
              ● Key generated
            </span>
            {isActive && (
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
                  fontFamily: "'JetBrains Mono', monospace", fontSize: 12,
                  boxSizing: 'border-box', resize: 'none',
                }}
              />
            </details>
          )}

          {showAuthorize ? (
            <div>
              {allowSavedKeyFile && (
                <div style={{ marginBottom: 8 }}>
                  <label htmlFor="ssh-auth-mode" style={{ display: 'block', fontSize: 12, color: 'var(--text-secondary)', marginBottom: 4 }}>
                    Authorize with
                  </label>
                  <select
                    id="ssh-auth-mode"
                    value={mode}
                    onChange={e => setMode(e.target.value as AuthMode)}
                    style={{
                      width: '100%', background: '#131D30', color: '#F0F2F5',
                      border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
                      padding: '6px 10px', fontSize: 14,
                    }}
                  >
                    <option value="password">Use server password</option>
                    <option value="keyfile">Use a saved key file</option>
                  </select>
                </div>
              )}

              {mode === 'keyfile' ? (
                <div style={{ marginBottom: 8 }}>
                  <label htmlFor="ssh-key-file" style={{ display: 'block', fontSize: 12, color: 'var(--text-secondary)', marginBottom: 4 }}>
                    Saved key file
                  </label>
                  <select
                    id="ssh-key-file"
                    value={selectedKeyFileId}
                    onChange={e => setSelectedKeyFileId(e.target.value)}
                    style={{
                      width: '100%', background: '#131D30', color: '#F0F2F5',
                      border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
                      padding: '6px 10px', fontSize: 14,
                    }}
                  >
                    <option value="">— Choose a saved key file —</option>
                    {keyFiles.map(k => <option key={k.id} value={k.id}>{k.name}</option>)}
                  </select>
                  {fieldErrors.keyFileResourceId && (
                    <p role="alert" style={{ margin: '4px 0 0', fontSize: 12, color: '#FF6B6B' }}>
                      {fieldErrors.keyFileResourceId}
                    </p>
                  )}
                </div>
              ) : (
                <div style={{ marginBottom: 8 }}>
                  <p style={{ margin: '0 0 8px', fontSize: 12, color: 'var(--text-secondary)' }}>
                    Enter your server password once — ShipRight will add its key and discard the password immediately.
                  </p>
                  <label htmlFor="ssh-auth-password" style={{ display: 'block', fontSize: 12, color: 'var(--text-secondary)', marginBottom: 4 }}>
                    Server password
                  </label>
                  <input
                    id="ssh-auth-password"
                    type="password"
                    value={password}
                    onChange={e => setPassword(e.target.value)}
                    onKeyDown={e => e.key === 'Enter' && handleAuthorize()}
                    placeholder="Server password"
                    autoComplete="new-password"
                    style={{
                      width: '100%', background: '#131D30', color: '#F0F2F5',
                      border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
                      padding: '6px 10px', fontSize: 14,
                    }}
                  />
                  {fieldErrors.password && (
                    <p role="alert" style={{ margin: '4px 0 0', fontSize: 12, color: '#FF6B6B' }}>
                      {fieldErrors.password}
                    </p>
                  )}
                </div>
              )}

              <div style={{ marginBottom: 8 }}>
                <label htmlFor="ssh-auth-port" style={{ display: 'block', fontSize: 12, color: 'var(--text-secondary)', marginBottom: 4 }}>
                  Port
                </label>
                <input
                  id="ssh-auth-port"
                  type="number"
                  value={port}
                  onChange={e => setPort(e.target.value)}
                  placeholder="22"
                  style={{
                    width: 88, background: '#131D30', color: '#F0F2F5',
                    border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6,
                    padding: '6px 10px', fontSize: 14,
                  }}
                />
              </div>

              <div style={{ display: 'flex', gap: 8 }}>
                <ZestButton
                  zest={{ visualOptions: { variant: 'standard', size: 'sm' }, buttonStyle: 'solid', semanticType: 'save' }}
                  onClick={handleAuthorize}
                  disabled={authorizing}
                >
                  {authorizing ? 'Authorizing…' : 'Authorize'}
                </ZestButton>
                <ZestButton
                  zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline', semanticType: 'cancel' }}
                  onClick={() => { setShowAuthorize(false); setPassword(''); setSelectedKeyFileId(''); }}
                >
                  Cancel
                </ZestButton>
              </div>

              {allowSavedKeyFile && (
                <InfoTip title="Using an EC2 server?" id="ec2-key-tip">
                  {`1. A new Linux EC2 server is created without a password.\n2. When the instance was created, Amazon gave you a .pem key file that you downloaded once.\n3. No longer have it? Open the AWS Console, go to EC2 then Key Pairs. A key can only be downloaded at creation, so create a new key pair and attach it to the instance.\n4. Add that .pem under Resources then Key files.\n5. Come back here and pick it to authorize.\nOther cloud providers (Azure, GCP, DigitalOcean) are coming later.`}
                </InfoTip>
              )}
            </div>
          ) : (
            <ZestButton
              zest={{ visualOptions: { size: 'sm' }, buttonStyle: 'outline' }}
              onClick={openAuthorize}
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
