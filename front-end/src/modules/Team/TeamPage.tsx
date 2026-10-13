import { useCallback, useEffect, useState, FormEvent } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import OverflowMenu from 'jattac.libs.web.overflow-menu';
import ResponsiveTable, { type ColumnDefinition } from 'jattac.libs.web.responsive-table';
import { api } from '@/shared/ApiService';
import { useAuthStore, type Role } from '@/shared/auth';
import type { ApiError } from '@/shared/auth';
import styles from './TeamPage.module.css';

export interface TeamMember {
  id: string;
  email: string;
  name: string;
  role: Role;
  isActive: boolean;
  created: string;
}

export interface TeamPageResponse {
  items: TeamMember[];
  total: number;
  page: number;
  pageSize: number;
}

const DEFAULT_PAGE_SIZE = 20;
const ROLES: Role[] = ['Admin', 'Operator', 'Viewer'];

function changeRoleLabel(role: Role): string {
  return `Make ${role}`;
}

export default function TeamPage() {
  const user = useAuthStore((s) => s.user);
  const isAdmin = user !== null && user.role === 'Admin';

  const [items, setItems] = useState<TeamMember[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize] = useState(DEFAULT_PAGE_SIZE);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');

  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  const [temporaryPassword, setTemporaryPassword] = useState('');
  const [role, setRole] = useState<Role>('Operator');
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [actionError, setActionError] = useState('');

  const load = useCallback(async (targetPage: number) => {
    setLoading(true);
    setLoadError('');
    try {
      const data = await api.get<TeamPageResponse>(`/api/team?page=${targetPage}&pageSize=${DEFAULT_PAGE_SIZE}`);
      setItems(data.items);
      setTotal(data.total);
      setPage(targetPage);
    } catch {
      setLoadError('Could not load the team. Try again.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (isAdmin) load(1);
  }, [isAdmin, load]);

  if (!isAdmin) {
    return <p className={styles.notAllowed}>You do not have permission to manage the team.</p>;
  }

  const resetForm = () => {
    setEmail('');
    setName('');
    setTemporaryPassword('');
    setRole('Operator');
    setFieldErrors({});
    setFormError('');
  };

  const handleCreate = async (e: FormEvent) => {
    e.preventDefault();
    setFormError('');
    setFieldErrors({});
    setSaving(true);
    try {
      await api.post('/api/team', { email, name, temporaryPassword, role });
      toast.success(`${name} was added to the team.`);
      resetForm();
      await load(Math.max(1, page));
    } catch (err) {
      const apiError = err as ApiError;
      if (apiError?.errors) setFieldErrors(apiError.errors);
      setFormError(apiError?.message ?? 'Could not add this teammate.');
    } finally {
      setSaving(false);
    }
  };

  const changeRole = async (member: TeamMember, nextRole: Role) => {
    setActionError('');
    try {
      await api.put(`/api/team/${member.id}/role`, { role: nextRole, expectedRole: member.role });
      toast.success(`${member.name} is now a ${nextRole}.`);
      await load(page);
    } catch (err) {
      const apiError = err as ApiError;
      setActionError(apiError?.message ?? 'Could not change the role.');
    }
  };

  const setActive = async (member: TeamMember, active: boolean) => {
    setActionError('');
    try {
      await api.post(`/api/team/${member.id}/${active ? 'activate' : 'deactivate'}`, {});
      toast.success(active ? `${member.name} was switched on.` : `${member.name} was switched off.`);
      await load(page);
    } catch (err) {
      const apiError = err as ApiError;
      setActionError(apiError?.message ?? 'Could not switch this person.');
    }
  };

  const confirmAndDeactivate = (member: TeamMember) => {
    setActionError('');
    const confirmed = window.confirm(`Switch off ${member.name}? They will not be able to sign in until you switch them back on.`);
    if (confirmed) setActive(member, false);
  };

  const columns: ColumnDefinition<TeamMember>[] = [
    {
      columnId: 'name',
      displayLabel: 'Name',
      cellRenderer: (row) => <span className={styles.cellText}>{row.name}</span>,
    },
    {
      columnId: 'email',
      displayLabel: 'Email',
      cellRenderer: (row) => <span className={styles.cellText}>{row.email}</span>,
    },
    {
      columnId: 'role',
      displayLabel: 'Role',
      cellRenderer: (row) => <span className={styles.roleBadge}>{row.role}</span>,
    },
    {
      columnId: 'status',
      displayLabel: 'Status',
      cellRenderer: (row) => (
        <span className={row.isActive ? styles.active : styles.inactive}>
          {row.isActive ? 'Active' : 'Switched off'}
        </span>
      ),
    },
    {
      columnId: 'actions',
      displayLabel: 'Actions',
      cellRenderer: (row) => (
        <OverflowMenu
          items={[
            ...ROLES.filter((r) => r !== row.role).map((r) => ({
              content: changeRoleLabel(r),
              onClick: () => changeRole(row, r),
            })),
            {
              content: row.isActive ? 'Switch off' : 'Switch on',
              onClick: () => (row.isActive ? confirmAndDeactivate(row) : setActive(row, true)),
            },
          ]}
        />
      ),
    },
  ];

  const pageStart = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const pageEnd = Math.min(page * pageSize, total);

  return (
    <div className={styles.page}>
      <div className={styles.headingRow}>
        <h1 className={styles.heading}>Team</h1>
      </div>

      {actionError && <div className={styles.errorBanner} role="alert">{actionError}</div>}

      <form className={styles.addCard} onSubmit={handleCreate} noValidate>
        <div className={styles.addTitle}>Add a teammate</div>
        {formError && <div className={styles.formError} role="alert">{formError}</div>}

        <div className={styles.addFields}>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="team-email">Email</label>
            <ZestTextbox
              id="team-email"
              type="email"
              placeholder="teammate@example.com"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
              zest={{ stretch: true }}
            />
            {fieldErrors.email && <div className={styles.fieldError} role="alert">{fieldErrors.email[0]}</div>}
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="team-name">Name</label>
            <ZestTextbox
              id="team-name"
              type="text"
              placeholder="Teammate name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              required
              zest={{ stretch: true }}
            />
            {fieldErrors.name && <div className={styles.fieldError} role="alert">{fieldErrors.name[0]}</div>}
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="team-password">Temporary password</label>
            <ZestTextbox
              id="team-password"
              type="password"
              placeholder="At least 8 characters"
              value={temporaryPassword}
              onChange={(e) => setTemporaryPassword(e.target.value)}
              required
              zest={{ stretch: true }}
            />
            {fieldErrors.temporaryPassword && <div className={styles.fieldError} role="alert">{fieldErrors.temporaryPassword[0]}</div>}
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="team-role">Role</label>
            <select
              id="team-role"
              className={styles.select}
              value={role}
              onChange={(e) => setRole(e.target.value as Role)}
            >
              {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
            </select>
            {fieldErrors.role && <div className={styles.fieldError} role="alert">{fieldErrors.role[0]}</div>}
          </div>

          <div className={styles.addActions}>
            <ZestButton
              type="submit"
              disabled={saving}
              zest={{ semanticType: 'add', visualOptions: { variant: 'standard', size: 'md' } }}
            >
              {saving ? 'Adding…' : 'Add teammate'}
            </ZestButton>
          </div>
        </div>
      </form>

      <div className={styles.listCard}>
        {loading && <div className={styles.stateText}>Loading your team…</div>}

        {!loading && loadError && (
          <div className={styles.stateText}>
            {loadError}{' '}
            <ZestButton
              type="button"
              onClick={() => load(page)}
              zest={{ semanticType: 'view', visualOptions: { variant: 'standard', size: 'sm' } }}
            >
              Try again
            </ZestButton>
          </div>
        )}

        {!loading && !loadError && total === 0 && (
          <div className={styles.stateText}>No one is in the team yet. Add the first teammate above.</div>
        )}

        {!loading && !loadError && total > 0 && (
          <>
            <ResponsiveTable data={items} columnDefinitions={columns} />
            <div className={styles.paging}>
              <span>{total === 0 ? '0 people' : `${pageStart}–${pageEnd} of ${total} people`}</span>
              <div className={styles.pagingButtons}>
                <ZestButton
                  type="button"
                  disabled={page <= 1 || loading}
                  onClick={() => load(page - 1)}
                  zest={{ semanticType: 'view', visualOptions: { variant: 'standard', size: 'sm' } }}
                >
                  Previous
                </ZestButton>
                <ZestButton
                  type="button"
                  disabled={pageEnd >= total || loading}
                  onClick={() => load(page + 1)}
                  zest={{ semanticType: 'view', visualOptions: { variant: 'standard', size: 'sm' } }}
                >
                  Next
                </ZestButton>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}