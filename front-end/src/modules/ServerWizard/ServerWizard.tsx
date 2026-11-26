import { useCallback, useEffect, useRef, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';
import CopyBox from './CopyBox';
import Switch from './Switch';
import {
  authorizedKeysCommand, canFinish, ConnectionTestResponse, defaultFolder, HostKeyResponse, isValidPublicKey,
  LoginRoute, looksLikeTailnet, nextStep, prevStep, ROUTES, SshKeyStatus, statusSymbol, STEP_ORDER, STEP_TITLES,
  StepId, stepNumber, validateWhere, WhereForm, withTimeout,
} from './wizardLogic';

interface Props {
  /** Open on an existing server (to fix or re-test its connection) instead of creating a new one. */
  existingServerId?: string;
  onClose: () => void;
  onFinished: (serverId: string) => void;
}

interface SavedKeyFile { id: string; name: string }
interface ServerRecord { id?: string; host: string; username: string; [k: string]: unknown }

const overlay: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.6)', zIndex: 1000,
  display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16,
};
const dialog: React.CSSProperties = {
  width: 640, maxWidth: '100%', maxHeight: '92vh', overflowY: 'auto', borderRadius: 10,
  background: 'var(--bg-card, #16203A)', border: '1px solid var(--border-subtle)', padding: '18px 22px', fontSize: 13,
};
const muted: React.CSSProperties = { margin: '6px 0 0', fontSize: 12, color: 'var(--text-muted)' };
const label: React.CSSProperties = { display: 'block', fontSize: 12, color: 'var(--text-secondary)', marginBottom: 4 };
const input: React.CSSProperties = {
  padding: '7px 9px', borderRadius: 6, border: '1px solid var(--border-subtle)', width: '100%', boxSizing: 'border-box',
  background: 'var(--bg-input, transparent)', color: 'inherit', fontSize: 13,
};
const err: React.CSSProperties = { color: '#E08A8A', fontSize: 12, marginTop: 4 };

const KEY_SLOW = 'ShipRight did not answer in time while preparing its key. The computer may be busy; try again.';

/** These screens track their own busy state, so ZestButton must not also lock itself after a click. */
const NOBUSY = { busyOptions: { handleInternally: false } };

const messageOf = (e: unknown, fallback: string) =>
  (e && typeof e === 'object' && 'message' in e && typeof (e as { message: unknown }).message === 'string'
    ? (e as { message: string }).message : fallback);

export default function ServerWizard({ existingServerId, onClose, onFinished }: Props) {
  const [step, setStep] = useState<StepId>(existingServerId ? 'identity' : 'route');
  const [route, setRoute] = useState<LoginRoute | null>(existingServerId ? 'manual' : null);
  const [form, setForm] = useState<WhereForm>({ name: '', host: '', username: 'ubuntu', remoteWorkingDir: '', tailnetId: '' });
  const [tailnets, setTailnets] = useState<{ id: string; name: string; online: boolean }[]>([]);
  const [errors, setErrors] = useState<Partial<Record<keyof WhereForm, string>>>({});
  const [serverId, setServerId] = useState<string | null>(existingServerId ?? null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  // identity
  const [hostKey, setHostKey] = useState<HostKeyResponse | null>(null);
  const [hostKeyError, setHostKeyError] = useState<string | null>(null);
  const [confirmChange, setConfirmChange] = useState(false);
  const [scanNonce, setScanNonce] = useState(0);

  // access
  const [keyStatus, setKeyStatus] = useState<SshKeyStatus | null>(null);
  const [keyError, setKeyError] = useState<string | null>(null);
  const [password, setPassword] = useState('');
  const [keyFiles, setKeyFiles] = useState<SavedKeyFile[]>([]);
  const [keyFileId, setKeyFileId] = useState('');
  const [accessDone, setAccessDone] = useState(false);
  const [byHand, setByHand] = useState(false);
  const [keyNonce, setKeyNonce] = useState(0);
  const keyStarted = useRef(false);
  const keyFilesStarted = useRef(false);

  // test
  const [result, setResult] = useState<ConnectionTestResponse | null>(null);
  const [testing, setTesting] = useState(false);
  const [testNonce, setTestNonce] = useState(0);
  const [testCancelled, setTestCancelled] = useState(false);
  const cancelTestRef = useRef<(() => void) | null>(null);

  const set = (field: keyof WhereForm, value: string) => setForm(prev => ({ ...prev, [field]: value }));

  // An existing server: fill the form from what is saved.
  useEffect(() => {
    if (!existingServerId) return;
    api.get<ServerRecord & { name?: string; remoteWorkingDir?: string; tailnetId?: string }>(`/api/servers/${existingServerId}`)
      .then(s => setForm({ name: s.name ?? '', host: s.host, username: s.username, remoteWorkingDir: s.remoteWorkingDir ?? '', tailnetId: s.tailnetId ?? '' }))
      .catch(() => setNotice('Could not load this server.'));
  }, [existingServerId]);

  // Optional tenant networks are only offered when the tenant has configured one.
  useEffect(() => {
    if (step !== 'where') return;
    api.get<{ id: string; name: string; online: boolean }[]>('/api/tailnets')
      .then(setTailnets)
      .catch(() => setTailnets([]));
  }, [step]);

  // Step 3: ask the server who it is.
  useEffect(() => {
    if (step !== 'identity' || !serverId) return;
    let cancelled = false;
    setHostKey(null); setHostKeyError(null); setConfirmChange(false);
    withTimeout(api.get<HostKeyResponse>(`/api/servers/${serverId}/host-key`), 30000, 'The server did not answer in time. Check the address and try again.')
      .then(r => { if (!cancelled) setHostKey(r); })
      .catch(e => { if (!cancelled) setHostKeyError(messageOf(e, 'ShipRight could not read the server\'s identity.')); });
    return () => { cancelled = true; };
  }, [step, serverId, scanNonce]);

  // Step 4: make sure ShipRight has a key (once; generating twice would replace it).
  useEffect(() => {
    if (step !== 'access' || !serverId || keyStarted.current) return;
    keyStarted.current = true;
    (async () => {
      try {
        let st = await withTimeout(api.get<SshKeyStatus>(`/api/servers/${serverId}/ssh-key/status`), 20000, KEY_SLOW);
        if (!st.exists) {
          await withTimeout(api.post(`/api/servers/${serverId}/ssh-key/generate`, {}), 30000, KEY_SLOW);
          st = await withTimeout(api.get<SshKeyStatus>(`/api/servers/${serverId}/ssh-key/status`), 20000, KEY_SLOW);
        }
        setKeyStatus(st);
        if (st.authorized) setAccessDone(true);
      } catch (e) {
        setKeyError(messageOf(e, 'ShipRight could not create its key on this computer. Make sure ssh-keygen is installed, then try again.'));
      }
    })();
  }, [step, serverId, keyNonce]);

  useEffect(() => {
    if (step !== 'access' || route !== 'pemfile' || keyFilesStarted.current) return;
    keyFilesStarted.current = true;
    api.get<{ items: SavedKeyFile[] }>('/api/resources/key-files?page=1&pageSize=100')
      .then(r => setKeyFiles(r.items ?? []))
      .catch(() => setKeyFiles([]));
  }, [step, route]);

  // Step 5: test.
  useEffect(() => {
    if (step !== 'test' || !serverId) return;
    let cancelled = false;
    setTesting(true); setResult(null); setTestCancelled(false);
    cancelTestRef.current = () => { cancelled = true; setTesting(false); setResult(null); setTestCancelled(true); };
    withTimeout(api.post<ConnectionTestResponse>(`/api/servers/${serverId}/connection-test`, {}), 90000,
      'The connection test took too long and was stopped. Try again.')
      .then(r => { if (!cancelled) { setResult(r); setTesting(false); } })
      .catch(e => {
        if (!cancelled) {
          setResult({
            ready: false, summary: 'The test could not run.',
            items: [{ id: 'internal', title: 'Connection test', status: 'fail', message: messageOf(e, 'The test could not run.'), fix: 'Try again.' }],
          });
          setTesting(false);
        }
      });
    return () => { cancelled = true; cancelTestRef.current = null; };
  }, [step, serverId, testNonce]);

  // ── actions ────────────────────────────────────────────────────────────────

  const saveWhere = useCallback(async () => {
    const e = validateWhere(form);
    setErrors(e);
    if (Object.keys(e).length > 0) return;
    setBusy(true); setNotice(null);
    const body = {
      name: form.name.trim(), host: form.host.trim(), username: form.username.trim(),
      tailnetId: form.tailnetId || '',
      remoteWorkingDir: form.remoteWorkingDir.trim() || defaultFolder(form.username),
    };
    try {
      if (serverId) {
        const current = await api.get<ServerRecord>(`/api/servers/${serverId}`);
        await api.put(`/api/servers/${serverId}`, { ...current, ...body });
      } else {
        const created = await api.post<ServerRecord>('/api/servers', {
          ...body, sshKeyPath: '', rebuildScript: 'rebuild.sh', deployMode: 'GitScript',
        });
        setServerId(created.id ?? null);
      }
      setStep('identity');
    } catch (ex) {
      setNotice(messageOf(ex, 'Could not save the server.'));
    } finally { setBusy(false); }
  }, [form, serverId]);

  const trust = async () => {
    if (!serverId || !hostKey) return;
    setBusy(true); setNotice(null);
    try {
      await api.post(`/api/servers/${serverId}/host-key/trust`, { sha256: hostKey.sha256, acknowledgeChange: confirmChange });
      setStep('access');
    } catch (ex) {
      setNotice(messageOf(ex, 'Could not trust this server.'));
    } finally { setBusy(false); }
  };

  const authorize = async (payload: Record<string, unknown>) => {
    if (!serverId) return;
    setBusy(true); setNotice(null);
    try {
      await api.post(`/api/servers/${serverId}/ssh-key/authorize`, { ...payload, port: 22 });
      setPassword('');
      setAccessDone(true);
      toast.success('Key added to the server.');
    } catch (ex) {
      setNotice(messageOf(ex, 'Authorization failed. Check the password and that the server allows it.'));
    } finally { setBusy(false); }
  };

  const pubKey = keyStatus?.publicKey ?? '';
  const pubKeyOk = isValidPublicKey(pubKey);
  const handCommand = pubKeyOk ? authorizedKeysCommand(pubKey) : '';
  const manual = route === 'manual' || route === 'tailscale' || byHand;

  const goBack = () => { setNotice(null); setStep(prevStep(step)); };

  // ── screens ────────────────────────────────────────────────────────────────

  const routeScreen = (
    <div>
      <p style={muted}>Pick the one that matches how you can get into this server today. ShipRight uses this to show only the steps you need.</p>
      <div role="radiogroup" aria-label="How do you log in?" style={{ display: 'grid', gap: 8, marginTop: 12 }}>
        {ROUTES.map(r => (
          <label key={r.id} style={{
            display: 'block', padding: '10px 12px', borderRadius: 8, cursor: 'pointer',
            border: `1px solid ${route === r.id ? '#C9A84C' : 'var(--border-subtle)'}`,
          }}>
            <input type="radio" name="route" checked={route === r.id} onChange={() => setRoute(r.id)} style={{ marginRight: 8 }} />
            <strong>{r.title}</strong>
            <span style={{ display: 'block', marginLeft: 22, marginTop: 4, fontSize: 12, color: 'var(--text-muted)' }}>{r.blurb}</span>
          </label>
        ))}
      </div>
    </div>
  );

  const field = (id: keyof WhereForm, text: string, tip: string, placeholder: string) => (
    <div style={{ marginTop: 12 }}>
      <div style={{ display: 'flex', alignItems: 'center', marginBottom: 4 }}>
        <label htmlFor={`wiz-${id}`} style={{ ...label, marginBottom: 0 }}>{text}</label>
        <HoverTip label={text}>{tip}</HoverTip>
      </div>
      <input id={`wiz-${id}`} style={input} value={form[id]} placeholder={placeholder}
        onChange={e => set(id, e.target.value)} aria-invalid={!!errors[id]} />
      {errors[id] && <div role="alert" style={err}>{errors[id]}</div>}
    </div>
  );

  const whereScreen = (
    <div>
      {field('name', 'Name', 'Any name that helps you recognise this server, for example "Production web".', 'Production web')}
      {field('host', 'Address', 'Where this computer finds the server.\nA cloud server: its public IP address (the cloud console shows it).\nTailscale: its Tailscale name or 100.x address (run "tailscale status").\nLeave out ssh:// and the port.', route === 'tailscale' ? 'myserver.tailnet-name.ts.net or 100.x.x.x' : '203.0.113.10')}
      {route === 'tailscale' && !looksLikeTailnet(form.host) && form.host.trim() !== '' && (
        <p style={muted}>That does not look like a Tailscale address. That is fine if it works, but Tailscale names end in .ts.net and addresses start with 100.</p>
      )}
      <div style={{ marginTop: 12 }}>
        <label htmlFor="wiz-tailnet" style={{ ...label, marginBottom: 4 }}>Network</label>
        {tailnets.length > 0 ? (
          <>
            <select id="wiz-tailnet" style={input} value={form.tailnetId ?? ''}
              onChange={e => set('tailnetId', e.target.value)}>
              <option value="">Direct network</option>
              {tailnets.map(t => <option key={t.id} value={t.id}>{t.name}{t.online ? '' : ' (offline)'}</option>)}
            </select>
            <p style={muted}>
              Choose a tenant Tailscale network when this server is only reachable through it.
              {' '}<a href="/tailnets#tailscale-setup" target="_blank" rel="noreferrer">Setup help ↗</a>
            </p>
          </>
        ) : (
          <p style={muted}>
            Direct network. If this server is private,
            {' '}<a href="/tailnets#tailscale-setup" target="_blank" rel="noreferrer">set up Tailscale ↗</a>.
          </p>
        )}
      </div>
      {field('username', 'Username', 'The name you log in to the server with. Ubuntu servers usually use "ubuntu", Amazon Linux uses "ec2-user", and Debian uses "admin" or "debian".', 'ubuntu')}
      {field('remoteWorkingDir', 'Folder for your projects (optional)', 'The folder on the server where ShipRight keeps your project. Leave it empty and ShipRight suggests a folder in the user\'s home directory.', defaultFolder(form.username) || '/home/ubuntu/apps')}
    </div>
  );

  const identityScreen = (
    <div>
      <p style={muted}>
        Before ShipRight trusts this server, check it really is yours. The server shows a fingerprint, a short code that identifies it.
      </p>
      {!hostKey && !hostKeyError && <p role="status">Asking the server to identify itself…</p>}
      {hostKeyError && (
        <div>
          <p role="alert" style={err}>{hostKeyError}</p>
          <ZestButton onClick={() => setScanNonce(n => n + 1)} zest={{ ...NOBUSY, semanticType: 'refresh', buttonStyle: 'outline' }}>Try again</ZestButton>
        </div>
      )}
      {hostKey && (
        <div>
          <p style={{ margin: '12px 0 4px', fontWeight: 600 }}>The server says it is:</p>
          <code style={{ display: 'block', padding: '8px 10px', borderRadius: 6, background: 'rgba(255,255,255,0.05)', wordBreak: 'break-all' }}>{hostKey.sha256}</code>
          <p style={muted}>{hostKey.message}</p>
          {hostKey.state === 'changed' && (
            <label style={{ display: 'block', marginTop: 10, color: '#E08A8A' }}>
              <input type="checkbox" checked={confirmChange} onChange={e => setConfirmChange(e.target.checked)} style={{ marginRight: 6 }} />
              I know why this changed and want to trust the new identity
            </label>
          )}
          {hostKey.state !== 'trusted' && (
            <>
              <p style={{ ...muted, marginTop: 12 }}>
                To compare: run this on the server and check that its SHA256 line matches the code above. If you cannot check right now, it is reasonable to continue when you typed the address yourself.
              </p>
              <CopyBox label="the fingerprint check command" text={hostKey.verifyCommand} />
            </>
          )}
        </div>
      )}
    </div>
  );

  const accessScreen = (
    <div>
      {keyError && (
        <div>
          <p role="alert" style={err}>{keyError}</p>
          <ZestButton onClick={() => { keyStarted.current = false; setKeyError(null); setKeyNonce(n => n + 1); }} zest={{ ...NOBUSY, semanticType: 'refresh', buttonStyle: 'outline' }}>Try again</ZestButton>
        </div>
      )}
      {!keyStatus && !keyError && <p role="status">Preparing the key for this server…</p>}
      {keyStatus && accessDone && (
        <p role="status" style={{ color: '#4CAF50' }}>ShipRight can use its key on this server. Continue to test the connection.</p>
      )}
      {keyStatus && !accessDone && !manual && route === 'password' && (
        <div>
          <p style={muted}>Enter the server password once. ShipRight adds its own key to the server and does not keep the password.</p>
          <label htmlFor="wiz-password" style={{ ...label, marginTop: 12 }}>Server password</label>
          <input id="wiz-password" type="password" style={input} value={password} onChange={e => setPassword(e.target.value)} autoComplete="off" />
          <div style={{ marginTop: 10, display: 'flex', gap: 8, alignItems: 'center' }}>
            <ZestButton disabled={busy || !password} onClick={() => authorize({ password })} zest={{ ...NOBUSY, semanticType: 'add' }}>
              {busy ? 'Adding the key…' : 'Add the key'}
            </ZestButton>
            <ZestButton onClick={() => setByHand(true)} zest={{ ...NOBUSY, semanticType: 'details', buttonStyle: 'text' }}>My server does not allow password login</ZestButton>
          </div>
        </div>
      )}
      {keyStatus && !accessDone && !manual && route === 'pemfile' && (
        <div>
          <p style={muted}>Choose the key file you saved in ShipRight. It is used once to add a key that belongs to ShipRight.</p>
          <label htmlFor="wiz-keyfile" style={{ ...label, marginTop: 12 }}>Saved key file</label>
          <select id="wiz-keyfile" style={input} value={keyFileId} onChange={e => setKeyFileId(e.target.value)}>
            <option value="">Choose a key file…</option>
            {keyFiles.map(k => <option key={k.id} value={k.id}>{k.name}</option>)}
          </select>
          {keyFiles.length === 0 && <p style={muted}>No key files saved yet. Add your .pem file under Resources, then Key files, and come back.</p>}
          <div style={{ marginTop: 10, display: 'flex', gap: 8 }}>
            <ZestButton disabled={busy || !keyFileId} onClick={() => authorize({ mode: 'keyfile', keyFileResourceId: keyFileId })} zest={{ ...NOBUSY, semanticType: 'add' }}>
              {busy ? 'Adding the key…' : 'Add the key'}
            </ZestButton>
            <ZestButton onClick={() => setByHand(true)} zest={{ ...NOBUSY, semanticType: 'details', buttonStyle: 'text' }}>I will add it by hand</ZestButton>
          </div>
        </div>
      )}
      {keyStatus && manual && !keyStatus.authorized && (
        <div>
          <p style={muted}>
            ShipRight made its own key for this server. Add it to the server by running the line below{route === 'tailscale'
              ? ', in any terminal that can reach the server over Tailscale.'
              : ', in any terminal on the server (your cloud provider\'s web console works too).'}
          </p>
          {pubKeyOk
            ? <CopyBox label="the command that adds the key" text={handCommand}
                tip="This adds ShipRight's public key to the list of keys allowed to log in (a file called authorized_keys). It is safe to run more than once. It only adds a key; it does not change anything else." />
            : <p role="alert" style={err}>The key ShipRight made could not be read. Go back and try again.</p>}
          <Switch checked={accessDone} onChange={setAccessDone} label="I have added the key to the server" />
          {!accessDone && <p style={muted}>Turn on the switch once you have run the command on the server. Next unlocks after that.</p>}
        </div>
      )}
      {notice && <p role="alert" style={err}>{notice}</p>}
    </div>
  );

  const testScreen = (
    <div>
      {testing && (
        <div>
          <p role="status">Testing the connection… this takes a few seconds.</p>
          <ZestButton onClick={() => cancelTestRef.current?.()} aria-label="Cancel the test" zest={{ ...NOBUSY, semanticType: 'cancel' }}>Cancel</ZestButton>
        </div>
      )}
      {testCancelled && <p role="status">The test was cancelled. Press Test again when you are ready.</p>}
      {result && (
        <div>
          <p role="status" style={{ fontWeight: 600, color: canFinish(result) ? '#4CAF50' : '#E08A8A' }}>{result.summary}</p>
          <ul style={{ listStyle: 'none', padding: 0, margin: '8px 0 0' }}>
            {result.items.map(item => {
              const s = statusSymbol(item.status);
              return (
                <li key={item.id} style={{ padding: '8px 0', borderTop: '1px solid var(--border-subtle)' }}>
                  <div style={{ display: 'flex', gap: 8 }}>
                    <span role="img" aria-label={s.label} style={{ color: s.color, fontWeight: 700, width: 16 }}>{s.symbol}</span>
                    <div style={{ flex: 1 }}>
                      <strong>{item.title}</strong>
                      <div style={{ fontSize: 12, color: 'var(--text-secondary)' }}>{item.message}</div>
                      {item.fix && item.status !== 'pass' && <div style={{ fontSize: 12, marginTop: 4 }}>{item.fix}</div>}
                      {item.command && <CopyBox label={`the command for ${item.title}`} text={item.command} />}
                      {item.status === 'fail' && item.id === 'reach' && (
                        <ZestButton onClick={() => setStep('where')} zest={{ ...NOBUSY, semanticType: 'edit', buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Change the address</ZestButton>
                      )}
                      {item.status === 'fail' && item.id === 'login' && (
                        <ZestButton onClick={() => { setAccessDone(false); setStep('access'); }} zest={{ ...NOBUSY, semanticType: 'back', buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Go back to adding the key</ZestButton>
                      )}
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </div>
  );

  const screens: Record<StepId, React.ReactNode> = {
    route: routeScreen, where: whereScreen, identity: identityScreen, access: accessScreen, test: testScreen,
  };

  // ── footer ─────────────────────────────────────────────────────────────────

  const footer = () => {
    const back = step !== 'route' && !(existingServerId && step === 'identity')
      ? <ZestButton onClick={goBack} disabled={busy} zest={{ ...NOBUSY, semanticType: 'back', buttonStyle: 'outline' }}>Back</ZestButton> : <span />;
    let next: React.ReactNode = null;
    if (step === 'route') next = <ZestButton disabled={!route} onClick={() => setStep(nextStep(step))} zest={{ ...NOBUSY, semanticType: 'next' }}>Next</ZestButton>;
    if (step === 'where') next = <ZestButton disabled={busy} onClick={saveWhere} zest={{ ...NOBUSY, semanticType: 'next' }}>{busy ? 'Saving…' : 'Next'}</ZestButton>;
    if (step === 'identity') {
      const ok = !!hostKey && (hostKey.state !== 'changed' || confirmChange);
      next = hostKey?.state === 'trusted'
        ? <ZestButton onClick={() => setStep('access')} zest={{ ...NOBUSY, semanticType: 'next' }}>Continue</ZestButton>
        : <ZestButton disabled={busy || !ok} onClick={trust} zest={{ ...NOBUSY, semanticType: 'approve' }}>{busy ? 'Trusting…' : 'Yes, trust this server'}</ZestButton>;
    }
    if (step === 'access') next = <ZestButton disabled={!accessDone} onClick={() => setStep('test')} zest={{ ...NOBUSY, semanticType: 'next' }}>Next</ZestButton>;
    if (step === 'test') next = (
      <span style={{ display: 'flex', gap: 8 }}>
        <ZestButton disabled={testing} onClick={() => setTestNonce(n => n + 1)} zest={{ ...NOBUSY, semanticType: 'refresh', buttonStyle: 'outline' }}>Test again</ZestButton>
        <ZestButton disabled={!canFinish(result) || testing} onClick={() => serverId && onFinished(serverId)} zest={{ ...NOBUSY, semanticType: 'approve' }}>Finish</ZestButton>
      </span>
    );
    return <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 18 }}>{back}{next}</div>;
  };

  return (
    <div style={overlay}>
      <div role="dialog" aria-modal="true" aria-label="Connect a server" style={dialog}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
          <h2 style={{ margin: 0, fontSize: 16 }}>Connect a server</h2>
          <ZestButton onClick={onClose} aria-label="Close" zest={{ ...NOBUSY, semanticType: 'close', buttonStyle: 'text', visualOptions: { size: 'sm' } }} />
        </div>
        <p style={{ ...muted, marginTop: 4 }}>
          Step {stepNumber(step)} of {STEP_ORDER.length}: {STEP_TITLES[step]}
        </p>
        <div style={{ marginTop: 8 }}>{screens[step]}</div>
        {step !== 'access' && notice && <p role="alert" style={err}>{notice}</p>}
        {footer()}
      </div>
    </div>
  );
}
