import { useCallback, useEffect, useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import OverflowMenu from 'jattac.libs.web.overflow-menu';
import { api } from '@/shared/ApiService';
import { IKeyFilePage, IKeyFileResource } from '@/shared/types/IProject';
import KeyFileForm, { KeyFileFormErrors, KeyFileFormValues } from './KeyFileForm';
import styles from '@/pages/Styles/Resources.module.css';

const PAGE_SIZE = 20;

/**
 * The Key files tab (R4). It owns its own fetch so the tab does no work until the user opens it,
 * and it only ever loads the current page (R9). Loading, empty, error-with-retry and success are
 * distinct states (UX-001).
 */
export default function KeyFilesPanel() {
  const [items, setItems] = useState<IKeyFileResource[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<IKeyFileResource | 'new' | null>(null);
  const [formErrors, setFormErrors] = useState<KeyFileFormErrors>({});

  const load = useCallback(async (targetPage: number) => {
    setLoading(true);
    setError(null);
    try {
      const res = await api.get<IKeyFilePage>(`/api/resources/key-files?page=${targetPage}&pageSize=${PAGE_SIZE}`);
      setItems(res.items ?? []);
      setTotal(res.total ?? 0);
      setPage(res.page ?? targetPage);
    } catch (e: any) {
      setError(e?.message ?? 'Failed to load key files.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(1); }, [load]);

  const openNew = () => { setFormErrors({}); setEditing('new'); };
  const openEdit = (r: IKeyFileResource) => { setFormErrors({}); setEditing(r); };
  const closePane = () => { setEditing(null); setFormErrors({}); };

  const handleSave = async (values: KeyFileFormValues) => {
    const isEdit = editing !== null && editing !== 'new';
    try {
      if (isEdit) {
        await api.put(`/api/resources/key-files/${(editing as IKeyFileResource).id}`, values);
        toast.success('Key file updated.');
      } else {
        await api.post('/api/resources/key-files', values);
        toast.success('Key file saved.');
      }
      closePane();
      await load(isEdit ? page : 1);
    } catch (e: any) {
      const errors = e?.errors ?? {};
      const fieldErrors: KeyFileFormErrors = {};
      if (errors.name?.[0]) fieldErrors.name = errors.name[0];
      if (errors.content?.[0]) fieldErrors.content = errors.content[0];
      setFormErrors(fieldErrors);
      if (!fieldErrors.name && !fieldErrors.content) toast.error(e?.message ?? 'Save failed.');
    }
  };

  const handleDelete = async (r: IKeyFileResource) => {
    if (!window.confirm(`Delete key file '${r.name}'? This cannot be undone.`)) return;
    try {
      await api.delete(`/api/resources/key-files/${r.id}`);
      toast.success(`'${r.name}' deleted.`);
      setItems(prev => prev.filter(x => x.id !== r.id));
      setTotal(t => Math.max(0, t - 1));
    } catch {
      toast.error('Failed to delete key file.');
    }
  };

  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const paneOpen = editing !== null;

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 16 }}>
        <ZestButton onClick={openNew} zest={{ visualOptions: { variant: 'standard' }, semanticType: 'add' }}>
          New Key File
        </ZestButton>
      </div>

      {paneOpen && (
        <div className={styles.card} style={{ marginBottom: 20 }}>
          <KeyFileForm
            initial={editing !== 'new' ? { name: (editing as IKeyFileResource).name, tags: (editing as IKeyFileResource).tags } : undefined}
            isEdit={editing !== 'new'}
            externalErrors={formErrors}
            onSave={handleSave}
            onCancel={closePane}
          />
        </div>
      )}

      {loading && (
        <p className={styles.empty}>Loading key files…</p>
      )}

      {!loading && error && (
        <div style={{ textAlign: 'center', padding: '24px 0' }}>
          <p role="alert" style={{ color: '#FF6B6B', marginBottom: 12 }}>{error}</p>
          <ZestButton onClick={() => load(page)} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
            Retry
          </ZestButton>
        </div>
      )}

      {!loading && !error && items.length === 0 && (
        <p className={styles.empty}>
          No key files yet. A key file is the .pem private key Amazon gave you when your server was created —
          save it here once and re-use it whenever you authorize a server.{' '}
          <ZestButton onClick={openNew} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>
            Add one
          </ZestButton>.
        </p>
      )}

      {!loading && !error && items.length > 0 && (
        <div className={styles.grid}>
          {items.map(r => (
            <div key={r.id} className={styles.card}>
              <div className={styles.cardTop}>
                <div style={{ minWidth: 0, flex: 1 }}>
                  <h3 className={styles.cardTitle} style={{ overflowWrap: 'anywhere', wordBreak: 'break-word' }}>
                    {r.name}
                  </h3>
                  <p className={styles.cardDetail}>Created: {formatDate(r.createdAt)}</p>
                  <p className={styles.cardDetail}>Modified: {formatDate(r.modifiedAt)}</p>
                  {r.tags && r.tags.length > 0 && (
                    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginTop: 6 }}>
                      {r.tags.map(tag => (
                        <span key={tag} style={{ fontSize: 12, padding: '2px 6px', borderRadius: 4, background: 'rgba(201,168,76,0.15)', color: '#C9A84C' }}>
                          {tag}
                        </span>
                      ))}
                    </div>
                  )}
                </div>
                <OverflowMenu items={[
                  { content: 'Edit', onClick: () => openEdit(r) },
                  { content: 'Delete', onClick: () => handleDelete(r) },
                ]} />
              </div>
            </div>
          ))}
        </div>
      )}

      {!loading && !error && pageCount > 1 && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 12, justifyContent: 'center', marginTop: 16 }}>
          <ZestButton
            onClick={() => load(page - 1)}
            disabled={page <= 1}
            zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
          >
            Previous
          </ZestButton>
          <span className={styles.cardDetail}>Page {page} of {pageCount}</span>
          <ZestButton
            onClick={() => load(page + 1)}
            disabled={page >= pageCount}
            zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}
          >
            Next
          </ZestButton>
        </div>
      )}
    </div>
  );
}

function formatDate(value: string): string {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleDateString();
}
