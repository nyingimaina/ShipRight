import Head from 'next/head';
import Link from 'next/link';
import { useEffect, useMemo, useState } from 'react';
import toast from 'react-hot-toast';
import { useRouter } from 'next/router';
import {
  RiCheckLine,
  RiEditLine,
  RiLogoutCircleLine,
  RiMapPinTimeLine,
  RiMoonLine,
  RiPaletteLine,
  RiTeamLine,
  RiUserLine,
} from 'react-icons/ri';
import ZestButton from 'jattac.libs.web.zest-button';
import AppShell from '@/modules/AppShell/AppShell';
import ThemeToggle from '@/shared/theme/ThemeToggle';
import { authApi, useAuthStore, type CompanyChoice } from '@/shared/auth';
import { DEFAULT_TIME_ZONE, localTimeIn, timeZoneChoices, timeZoneLabel } from '@/shared/time';
import styles from './Styles/Settings.module.css';

const ROLE_COPY: Record<string, string> = {
  Admin: 'Full control of this company',
  Operator: 'Can build, deploy and back up',
  Viewer: 'Read-only access',
};

export default function SettingsPage() {
  const router = useRouter();
  const user = useAuthStore((s) => s.user);
  const setUser = useAuthStore((s) => s.setUser);
  const clearAuth = useAuthStore((s) => s.clearAuth);

  const zones = useMemo(() => timeZoneChoices(), []);
  const [savingZone, setSavingZone] = useState(false);
  const [zone, setZone] = useState(user?.timeZone || DEFAULT_TIME_ZONE);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const [newCompany, setNewCompany] = useState('');
  const [busyCompany, setBusyCompany] = useState<string | null>(null);
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), 30_000);
    return () => window.clearInterval(timer);
  }, []);

  const companies: CompanyChoice[] = user?.companies?.length
    ? user.companies
    : user
      ? [{ id: user.companyId, name: user.companyName || 'This company', role: user.role, isActive: true, current: true }]
      : [];

  const saveZone = async (next: string) => {
    const previous = zone;
    setZone(next);
    setSavingZone(true);
    try {
      const session = await authApi.updatePreferences({ timeZone: next });
      setUser(session.user);
      toast.success(`Dates now use ${timeZoneLabel(next)} time.`);
    } catch (error) {
      setZone(previous);
      toast.error((error as { message?: string })?.message ?? 'Could not save your time zone.');
    } finally {
      setSavingZone(false);
    }
  };

  const useDeviceZone = () => {
    const device = Intl.DateTimeFormat().resolvedOptions().timeZone;
    if (device) void saveZone(device);
  };

  const switchTo = async (company: CompanyChoice) => {
    if (company.current || busyCompany) return;
    setBusyCompany(company.id);
    try {
      const session = await authApi.switchCompany(company.id);
      setUser(session.user);
      toast.success(`Now working in ${company.name}.`);
      window.location.assign('/projects');
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not switch company.');
      setBusyCompany(null);
    }
  };

  const rename = async (company: CompanyChoice) => {
    const name = renameValue.trim();
    if (!name || busyCompany) return;
    setBusyCompany(company.id);
    try {
      await authApi.renameCompany(company.id, name);
      const nextCompanies = companies.map((c) => (c.id === company.id ? { ...c, name } : c));
      if (user) {
        setUser({
          ...user,
          companyName: company.current ? name : user.companyName,
          companies: nextCompanies,
        });
      }
      setRenaming(null);
      toast.success('Company renamed.');
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not rename the company.');
    } finally {
      setBusyCompany(null);
    }
  };

  const createCompany = async () => {
    const name = newCompany.trim();
    if (!name || busyCompany) return;
    setBusyCompany('create');
    try {
      const session = await authApi.createCompany(name);
      setUser(session.user);
      setNewCompany('');
      toast.success(`${name} is ready. You are its Admin.`);
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not create the company.');
    } finally {
      setBusyCompany(null);
    }
  };

  const signOut = async () => {
    try { await authApi.logout(); } catch { /* the cookie may already be gone */ }
    clearAuth();
    void router.push('/login');
  };

  if (!user) return null;

  const current = companies.find((c) => c.current) ?? companies[0];

  return (
    <>
      <Head><title>Settings · ShipRight</title></Head>
      <AppShell>
        <div className={styles.page}>
          <header className={styles.hero}>
            <div className={styles.heroIcon} aria-hidden="true"><RiPaletteLine size={20} /></div>
            <div>
              <h1>Settings</h1>
              <p>Your time zone, the look of ShipRight, and the companies you can work in.</p>
            </div>
          </header>

          <div className={styles.grid}>
            <section className={`${styles.card} ${styles.timeCard}`} aria-labelledby="time-heading">
              <div className={styles.cardHead}>
                <span className={styles.cardIcon} aria-hidden="true"><RiMapPinTimeLine size={18} /></span>
                <div>
                  <h2 id="time-heading">Time zone</h2>
                  <p>Every date, backup and schedule uses this.</p>
                </div>
              </div>

              <div className={styles.timeNow}>
                <span className={styles.clock}>{localTimeIn(zone, now)}</span>
                <span className={styles.timeNowLabel}>in {timeZoneLabel(zone)} right now</span>
              </div>

              <label className={styles.field}>
                <span>Your time zone</span>
                <select
                  value={zone}
                  onChange={(event) => void saveZone(event.target.value)}
                  disabled={savingZone}
                >
                  {zones.map((id) => (
                    <option key={id} value={id}>{id.replace(/_/g, ' ')}</option>
                  ))}
                </select>
              </label>

              <div className={styles.inlineActions}>
                <button type="button" className={styles.quiet} onClick={useDeviceZone} disabled={savingZone}>
                  Use this device’s time zone
                </button>
                {zone !== DEFAULT_TIME_ZONE && (
                  <button type="button" className={styles.quiet} onClick={() => void saveZone(DEFAULT_TIME_ZONE)} disabled={savingZone}>
                    Reset to {timeZoneLabel(DEFAULT_TIME_ZONE)}
                  </button>
                )}
              </div>
            </section>

            <section className={styles.card} aria-labelledby="appearance-heading">
              <div className={styles.cardHead}>
                <span className={styles.cardIcon} aria-hidden="true"><RiMoonLine size={18} /></span>
                <div>
                  <h2 id="appearance-heading">Appearance</h2>
                  <p>Follow the system, or choose a side.</p>
                </div>
              </div>
              <div className={styles.themeRow}>
                <ThemeToggle />
              </div>
              <p className={styles.hint}>Saved in this browser, so each device can look different.</p>
            </section>

            <section className={`${styles.card} ${styles.companyCard}`} aria-labelledby="companies-heading">
              <div className={styles.cardHead}>
                <span className={styles.cardIcon} aria-hidden="true"><RiTeamLine size={18} /></span>
                <div>
                  <h2 id="companies-heading">Companies</h2>
                  <p>Servers, projects and people stay inside their company.</p>
                </div>
              </div>

              <ul className={styles.companyList}>
                {companies.map((company) => (
                  <li key={company.id} className={company.current ? styles.companyCurrent : styles.companyRow}>
                    <div className={styles.companyMain}>
                      <div className={styles.companyNameRow}>
                        <span className={styles.companyName}>{company.name}</span>
                        {company.current && <span className={styles.currentPill}><RiCheckLine size={11} /> Current</span>}
                      </div>
                      <span className={styles.companyMeta}>{company.role} · {ROLE_COPY[company.role] ?? 'Member'}</span>
                    </div>

                    {renaming === company.id ? (
                      <form
                        className={styles.renameForm}
                        onSubmit={(event) => { event.preventDefault(); void rename(company); }}
                      >
                        <input
                          value={renameValue}
                          onChange={(event) => setRenameValue(event.target.value)}
                          maxLength={200}
                          aria-label={`New name for ${company.name}`}
                          autoFocus
                        />
                        <button type="submit" className={styles.miniPrimary} disabled={!renameValue.trim() || busyCompany !== null}>
                          Save
                        </button>
                        <button type="button" className={styles.miniQuiet} onClick={() => setRenaming(null)}>Cancel</button>
                      </form>
                    ) : (
                      <div className={styles.companyActions}>
                        {company.current ? (
                          <span className={styles.here}>You are here</span>
                        ) : (
                          <ZestButton
                            onClick={() => void switchTo(company)}
                            disabled={busyCompany !== null}
                            zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
                          >
                            {busyCompany === company.id ? 'Switching…' : 'Switch'}
                          </ZestButton>
                        )}
                        {company.role === 'Admin' && (
                          <button
                            type="button"
                            className={styles.iconButton}
                            title={`Rename ${company.name}`}
                            aria-label={`Rename ${company.name}`}
                            onClick={() => { setRenaming(company.id); setRenameValue(company.name); }}
                          >
                            <RiEditLine size={15} />
                          </button>
                        )}
                      </div>
                    )}
                  </li>
                ))}
              </ul>

              <form
                className={styles.createRow}
                onSubmit={(event) => { event.preventDefault(); void createCompany(); }}
              >
                <input
                  value={newCompany}
                  onChange={(event) => setNewCompany(event.target.value)}
                  placeholder="New company name"
                  maxLength={200}
                  aria-label="New company name"
                />
                <ZestButton
                  onClick={() => void createCompany()}
                  disabled={!newCompany.trim() || busyCompany !== null}
                  zest={{ semanticType: 'add', visualOptions: { variant: 'standard', size: 'sm' } }}
                >
                  {busyCompany === 'create' ? 'Creating…' : 'Create'}
                </ZestButton>
              </form>
              <p className={styles.hint}>
                You become the Admin of a company you create. Moving servers and projects between companies lives on their own screens.
              </p>
            </section>

            <section className={styles.card} aria-labelledby="account-heading">
              <div className={styles.cardHead}>
                <span className={styles.cardIcon} aria-hidden="true"><RiUserLine size={18} /></span>
                <div>
                  <h2 id="account-heading">Account</h2>
                  <p>Your sign-in, shared across every company.</p>
                </div>
              </div>

              <dl className={styles.accountList}>
                <div><dt>Name</dt><dd>{user.name}</dd></div>
                <div><dt>Email</dt><dd>{user.email}</dd></div>
                <div><dt>Current company</dt><dd>{current?.name ?? user.companyName ?? '—'}</dd></div>
                <div><dt>Role here</dt><dd>{current?.role ?? user.role}</dd></div>
              </dl>

              <div className={styles.inlineActions}>
                <Link href="/change-password" className={styles.linkButton}>Change password</Link>
                <button type="button" className={styles.dangerQuiet} onClick={() => void signOut()}>
                  <RiLogoutCircleLine size={15} /> Sign out
                </button>
              </div>
            </section>
          </div>

          <p className={styles.footer}>
            Tip: changing your time zone does not move existing schedules. Open a schedule and edit it if you want it to follow the new zone.
          </p>
        </div>
      </AppShell>
    </>
  );
}
