import Head from 'next/head';
import { useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import AppShell from '@/modules/AppShell/AppShell';
import InfoTip from '@/shared/components/InfoTip';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { api } from '@/shared/ApiService';
import styles from './Styles/Tailnets.module.css';

const ADMIN = 'https://console.tailscale.com';
const LINKS = {
  keys: `${ADMIN}/admin/settings/keys`,
  oauth: `${ADMIN}/admin/settings/oauth`,
  acls: `${ADMIN}/admin/acls`,
  machines: `${ADMIN}/admin/machines`,
};

function DeepLink({ href, children }: { href: string; children: React.ReactNode }) {
  return (
    <a className={styles.deepLink} href={href} target="_blank" rel="noreferrer">
      {children} <span aria-hidden="true">↗</span>
    </a>
  );
}

interface Tailnet {
  id: string;
  name: string;
  tailnet: string;
  tags: string;
  hasAuthKey: boolean;
  hasOAuthClient: boolean;
  online: boolean;
  status: string;
  socksPort: number;
  modifiedAt: string;
}

const emptyForm = { name: '', tailnet: '-', authKey: '', oauthClientId: '', oauthClientSecret: '', tags: 'tag:shipright' };

export default function TailnetsPage() {
  const [items, setItems] = useState<Tailnet[]>([]);
  const [form, setForm] = useState(emptyForm);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const load = () => api.get<Tailnet[]>('/api/tailnets')
    .then(setItems)
    .catch(() => toast.error('Could not load Tailscale networks.'));

  useEffect(() => { load(); }, []);

  const set = (key: keyof typeof form, value: string) => setForm(prev => ({ ...prev, [key]: value }));

  const save = async () => {
    setBusy(true); setError('');
    try {
      await api.post('/api/tailnets', form);
      setForm(emptyForm);
      toast.success('Tailscale network saved.');
      await load();
    } catch (e) {
      setError((e as Error).message || 'Could not save the Tailscale network.');
    } finally { setBusy(false); }
  };

  const start = async (item: Tailnet) => {
    setError('');
    try {
      await api.post(`/api/tailnets/${item.id}/start`, {});
      toast.success(`${item.name} is online.`);
      await load();
    } catch (e) {
      setError((e as Error).message || 'Could not bring the Tailscale network online.');
    }
  };

  const stop = async (item: Tailnet) => {
    await api.post(`/api/tailnets/${item.id}/stop`, {}).catch(() => undefined);
    await load();
  };

  const test = async (item: Tailnet) => {
    try {
      const r = await api.post<{ online: boolean }>('/api/tailnets/' + item.id + '/test', {});
      toast[r.online ? 'success' : 'error'](r.online ? `${item.name} is online.` : `${item.name} is offline.`);
      await load();
    } catch (e) {
      toast.error((e as Error).message || 'Could not test the Tailscale network.');
    }
  };

  const remove = async (item: Tailnet) => {
    if (!confirm(`Remove Tailscale network '${item.name}'?`)) return;
    await api.delete(`/api/tailnets/${item.id}`);
    await load();
  };

  return (
    <>
      <Head><title>ShipRight - Tailscale</title></Head>
      <AppShell>
        <header className={styles.header}>
          <div>
            <h1 className={styles.heading}>Tailscale networks</h1>
            <p className={styles.sub}>Give tenants an optional private network for their servers. ShipRight joins as an unprivileged userspace node.</p>
          </div>
          <ZestButton onClick={load} zest={{ buttonStyle: 'outline', semanticType: 'refresh' }}>Refresh</ZestButton>
        </header>

        <div className={styles.grid}>
          {items.length === 0 && <p className={styles.empty}>No Tailscale networks yet.</p>}
          {items.map(item => (
            <article key={item.id} className={styles.card}>
              <div className={styles.cardTop}>
                <div>
                  <h2>{item.name}</h2>
                  <p>{item.tailnet === '-' ? 'Default tailnet' : item.tailnet}</p>
                </div>
                <span className={item.online ? styles.online : styles.offline}>{item.online ? 'Online' : 'Offline'}</span>
              </div>
              <p className={styles.meta}>{item.hasOAuthClient ? 'OAuth client' : 'Auth key'} · {item.tags}</p>
              <p className={styles.meta}>{item.socksPort > 0 ? `SOCKS5 127.0.0.1:${item.socksPort}` : 'Not running'}</p>
              <div className={styles.actions}>
                <ZestButton onClick={() => start(item)} zest={{ semanticType: 'next', visualOptions: { size: 'sm' } }}>Start</ZestButton>
                <ZestButton onClick={() => test(item)} zest={{ buttonStyle: 'outline', semanticType: 'refresh', visualOptions: { size: 'sm' } }}>Test</ZestButton>
                <ZestButton onClick={() => stop(item)} zest={{ buttonStyle: 'text', semanticType: 'close', visualOptions: { size: 'sm' } }}>Stop</ZestButton>
                <ZestButton onClick={() => remove(item)} zest={{ buttonStyle: 'text', semanticType: 'delete', visualOptions: { size: 'sm', variant: 'danger' } }}>Remove</ZestButton>
              </div>
            </article>
          ))}
        </div>

        <section className={styles.formCard}>
          <h2>Connect a tailnet</h2>
          <p className={styles.note}>
            Use an auth key, or a Tailscale OAuth client with device-creation access. If your tailnet signs in with Google,
            authorize the OAuth client in Tailscale Admin; Google sign-in itself is handled by Tailscale.
          </p>

          <InfoTip title="New to Tailscale? Start here" id="tailscale-setup">
            <ol className={styles.steps}>
              <li>
                Sign in at <DeepLink href={ADMIN}>console.tailscale.com</DeepLink>. This creates your tailnet
                (its name is usually your email domain plus <code>.ts.net</code>).
              </li>
              <li>
                Open <DeepLink href={LINKS.keys}>Settings → Keys</DeepLink> and choose <strong>Generate auth key</strong>.
                Tick <strong>Reusable</strong> if you want it to survive restarts, and add the tag below.
                The key starts with <code>tskey-auth-</code>.
              </li>
              <li>
                Prefer no long-lived key? Create an <DeepLink href={LINKS.oauth}>OAuth client</DeepLink> with
                <strong> Auth keys</strong> (read &amp; write). ShipRight then mints a short-lived key each time it joins.
              </li>
              <li>
                Allow the node to reach your servers: add <code>tag:shipright</code> to
                <DeepLink href={LINKS.acls}>Access controls → tagOwners</DeepLink>, then grant it port 22 on the
                servers it should manage.
              </li>
              <li>
                Paste the key (or OAuth client) below, press <strong>Save network</strong>, then <strong>Start</strong>.
                The card turns <strong>Online</strong> when the node has joined.
              </li>
            </ol>
          </InfoTip>

          <div className={styles.fields}>
            <div className={styles.fieldGroup}>
              <label>Name<ZestTextbox value={form.name} onChange={e => set('name', e.target.value)} placeholder="Acme production" zest={{ stretch: true }} /></label>
              <p className={styles.fieldHelp}>Just a label inside ShipRight, for example the company or environment.</p>
            </div>

            <div className={styles.fieldGroup}>
              <label>Tailnet<ZestTextbox value={form.tailnet} onChange={e => set('tailnet', e.target.value)} placeholder="example.com or -" zest={{ stretch: true }} /></label>
              <p className={styles.fieldHelp}>
                Leave as <code>-</code> for your default tailnet. To target another one, use its name from
                {' '}<DeepLink href={`${ADMIN}/admin/settings/general`}>Settings → General</DeepLink>.
              </p>
            </div>

            <div className={styles.fieldGroup}>
              <label>Auth key<ZestTextbox type="password" value={form.authKey} onChange={e => set('authKey', e.target.value)} placeholder="tskey-auth-…" zest={{ stretch: true }} /></label>
              <p className={styles.fieldHelp}>
                <DeepLink href={LINKS.keys}>Open Settings → Keys</DeepLink> → <strong>Generate auth key</strong>.
                Give it the tag <code>tag:shipright</code>, then copy the <code>tskey-auth-…</code> value.
              </p>
              <InfoTip title="Auth key or OAuth client?" id="key-or-oauth">
                An auth key is one value and is the simplest option. An OAuth client is a client ID and secret;
                ShipRight exchanges them for a short-lived key whenever it joins, so there is no long-lived key to rotate.
                Use one or the other, not both.
              </InfoTip>
            </div>

            <div className={styles.fieldGroup}>
              <label>OAuth client ID<ZestTextbox value={form.oauthClientId} onChange={e => set('oauthClientId', e.target.value)} placeholder="Optional" zest={{ stretch: true }} /></label>
              <p className={styles.fieldHelp}>
                <DeepLink href={LINKS.oauth}>Open Settings → OAuth clients</DeepLink> → <strong>Generate OAuth client</strong>,
                scope <strong>Auth keys</strong> (read &amp; write). Leave blank if you pasted an auth key.
              </p>
            </div>

            <div className={styles.fieldGroup}>
              <label>OAuth client secret<ZestTextbox type="password" value={form.oauthClientSecret} onChange={e => set('oauthClientSecret', e.target.value)} placeholder="Optional" zest={{ stretch: true }} /></label>
              <p className={styles.fieldHelp}>
                Shown once, next to the client ID. It is stored encrypted and never shown again.
              </p>
            </div>

            <div className={styles.fieldGroup}>
              <label>Tags<ZestTextbox value={form.tags} onChange={e => set('tags', e.target.value)} placeholder="tag:shipright" zest={{ stretch: true }} /></label>
              <p className={styles.fieldHelp}>
                The tag the node joins with. It must be listed under
                {' '}<DeepLink href={LINKS.acls}>Access controls → tagOwners</DeepLink>.
              </p>
              <InfoTip title="Why does the tag matter?" id="tailscale-tags">
                Tailscale uses tags to decide what a machine may reach. Add <code>tag:shipright</code> to
                <strong> tagOwners</strong>, then grant that tag access to your servers (port 22) in the same file.
                If the tag is missing, the node joins but every connection is refused.
              </InfoTip>
            </div>
          </div>

          {error && <p role="alert" className={styles.error}>{error}</p>}
          <ZestButton onClick={save} disabled={busy} zest={{ semanticType: 'save', visualOptions: { variant: 'standard' } }}>
            {busy ? 'Saving.' : 'Save network'}
          </ZestButton>
        </section>
      </AppShell>
    </>
  );
}
