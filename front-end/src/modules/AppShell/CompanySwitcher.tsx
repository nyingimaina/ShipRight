import { useEffect, useRef, useState } from 'react';
import { useRouter } from 'next/router';
import toast from 'react-hot-toast';
import { RiAddLine, RiArrowDownSLine, RiBuilding2Line, RiCheckLine, RiSettings3Line } from 'react-icons/ri';
import { authApi, useAuthStore, type CompanyChoice } from '@/shared/auth';
import styles from './CompanySwitcher.module.css';

const roleTone: Record<string, string> = {
  Admin: styles.roleAdmin,
  Operator: styles.roleOperator,
  Viewer: styles.roleViewer,
};

export default function CompanySwitcher() {
  const router = useRouter();
  const user = useAuthStore((s) => s.user);
  const setUser = useAuthStore((s) => s.setUser);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [companyName, setCompanyName] = useState('');
  const panelRef = useRef<HTMLDivElement>(null);

  const companies: CompanyChoice[] = user?.companies?.length
    ? user.companies
    : user
      ? [{ id: user.companyId, name: user.companyName || 'This company', role: user.role, isActive: true, current: true }]
      : [];

  const current = companies.find((c) => c.current) ?? companies[0];

  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    window.addEventListener('keydown', onKey);
    const focus = window.setTimeout(() => panelRef.current?.focus(), 30);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.clearTimeout(focus);
    };
  }, [open]);

  const switchTo = async (company: CompanyChoice) => {
    if (company.current || busy) return;
    setBusy(company.id);
    try {
      const session = await authApi.switchCompany(company.id);
      setUser(session.user);
      toast.success(`Now working in ${company.name}.`);
      // Data for the new company is scoped by the server; a clean reload is the honest way to refetch every screen.
      window.location.assign('/projects');
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not switch company.');
      setBusy(null);
    }
  };

  const createCompany = async () => {
    const name = companyName.trim();
    if (!name || busy) return;
    setBusy('create');
    try {
      const session = await authApi.createCompany(name);
      setUser(session.user);
      toast.success(`${name} is ready.`);
      window.location.assign('/projects');
    } catch (error) {
      toast.error((error as { message?: string })?.message ?? 'Could not create the company.');
      setBusy(null);
    }
  };

  if (!user) return null;

  return (
    <>
      <button
        type="button"
        className={styles.control}
        onClick={() => setOpen(true)}
        aria-haspopup="dialog"
        aria-expanded={open}
        title="Switch company"
      >
        <span className={styles.building} aria-hidden="true"><RiBuilding2Line size={14} /></span>
        <span className={styles.name}>{current?.name || 'Company'}</span>
        <span className={`${styles.role} ${roleTone[current?.role ?? 'Viewer']}`}>{current?.role ?? 'Viewer'}</span>
        <RiArrowDownSLine size={16} className={styles.chevron} aria-hidden="true" />
      </button>

      {open && (
        <div className={styles.backdrop} onMouseDown={(e) => { if (e.target === e.currentTarget) setOpen(false); }}>
          <div className={styles.sheet} role="dialog" aria-modal="true" aria-label="Switch company" tabIndex={-1} ref={panelRef}>
            <div className={styles.grabber} aria-hidden="true" />
            <div className={styles.sheetHead}>
              <div>
                <h2>Your companies</h2>
                <p>Each one keeps its own servers, projects and people.</p>
              </div>
              <button type="button" className={styles.close} onClick={() => setOpen(false)} aria-label="Close">✕</button>
            </div>

            <ul className={styles.list}>
              {companies.map((company) => (
                <li key={company.id}>
                  <button
                    type="button"
                    className={`${styles.choice} ${company.current ? styles.choiceCurrent : ''}`}
                    onClick={() => switchTo(company)}
                    disabled={busy !== null}
                    aria-current={company.current ? 'true' : undefined}
                  >
                    <span className={styles.choiceIcon} aria-hidden="true">
                      {company.current ? <RiCheckLine size={17} /> : <RiBuilding2Line size={17} />}
                    </span>
                    <span className={styles.choiceText}>
                      <span className={styles.choiceName}>{company.name}</span>
                      <span className={styles.choiceMeta}>
                        {company.current ? 'You are here' : busy === company.id ? 'Switching…' : 'Tap to switch'}
                      </span>
                    </span>
                    <span className={`${styles.role} ${roleTone[company.role]}`}>{company.role}</span>
                  </button>
                </li>
              ))}
            </ul>

            {creating ? (
              <form
                className={styles.createForm}
                onSubmit={(event) => {
                  event.preventDefault();
                  void createCompany();
                }}
              >
                <label htmlFor="new-company-name">New company name</label>
                <input
                  id="new-company-name"
                  value={companyName}
                  onChange={(event) => setCompanyName(event.target.value)}
                  placeholder="e.g. Acme Studio"
                  maxLength={200}
                  autoFocus
                />
                <div className={styles.createActions}>
                  <button type="button" className={styles.quiet} onClick={() => setCreating(false)}>Cancel</button>
                  <button type="submit" className={styles.primary} disabled={!companyName.trim() || busy !== null}>
                    {busy === 'create' ? 'Creating…' : 'Create & switch'}
                  </button>
                </div>
              </form>
            ) : (
              <button type="button" className={styles.add} onClick={() => setCreating(true)}>
                <RiAddLine size={17} /> Create another company
              </button>
            )}

            <button
              type="button"
              className={styles.manage}
              onClick={() => { setOpen(false); void router.push('/settings'); }}
            >
              <RiSettings3Line size={15} /> Manage companies, time zone and appearance
            </button>
          </div>
        </div>
      )}
    </>
  );
}
