import { useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import ZestTextbox from 'jattac.libs.web.zest-textbox';
import { api } from '@/shared/ApiService';
import { ICredentialResource } from '@/shared/types/IProject';
import styles from '../Styles/ProjectSetupWizard.module.css';
import { GitRepoDraft, emptyGitRepoDraft } from './types';

interface Props {
  repos: GitRepoDraft[];
  credentials: ICredentialResource[];
  errors: Record<string, string>;
  onReposChange: (repos: GitRepoDraft[]) => void;
}

export default function GitReposAtom({ repos, credentials, errors, onReposChange }: Props) {
  const [creatingFor, setCreatingFor] = useState<number | null>(null);
  const [newName, setNewName] = useState('');
  const [newValue, setNewValue] = useState('');
  const [filterTag, setFilterTag] = useState<string>('');

  const addRepo = () => {
    if (repos.length < 10) onReposChange([...repos, emptyGitRepoDraft()]);
  };
  const removeRepo = (i: number) => onReposChange(repos.filter((_, idx) => idx !== i));
  const updateRepo = (i: number, patch: Partial<GitRepoDraft>) =>
    onReposChange(repos.map((r, j) => j === i ? { ...r, ...patch } : r));

  const createCredential = async (repoIndex: number) => {
    if (!newName.trim() || !newValue.trim()) return;
    try {
      const created = await api.post<ICredentialResource>('/api/resources/credentials', { name: newName.trim(), value: newValue.trim() });
      updateRepo(repoIndex, { credentialResourceId: created.id });
      setCreatingFor(null);
      setNewName('');
      setNewValue('');
    } catch {
      toast.error('Failed to create credential');
    }
  };

  const allTags = Array.from(new Set(credentials.flatMap(c => c.tags ?? []))).sort();
  const visibleCredentials = filterTag
    ? credentials.filter(c => (c.tags ?? []).includes(filterTag))
    : credentials;

  return (
    <div className={styles.section}>
      <span className={styles.sectionTitle}>Git Repositories</span>
      {errors['gitRepos'] && <p className={styles.errorText}>{errors['gitRepos']}</p>}
      {repos.length === 0 && !errors['gitRepos'] && (
        <div className={styles.warningBox}>No git repositories detected.</div>
      )}
      {repos.map((repo, i) => (
        <div key={i} className={styles.serviceCard}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 8 }}>
            <span style={{ fontSize: 13, fontWeight: 600, color: '#A8B8CC' }}>Repository {i + 1}</span>
            <ZestButton onClick={() => removeRepo(i)} zest={{ visualOptions: { variant: 'danger', size: 'sm' } }}>Remove</ZestButton>
          </div>
          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Repository path</span>
            <ZestTextbox
              value={repo.repoPath}
              onChange={e => updateRepo(i, { repoPath: e.target.value })}
              placeholder="/mnt/d/work/... or \\\\wsl.localhost\\..."
              zest={{ stretch: true, zSize: 'sm' }}
            />
            {errors[`gitRepos[${i}].repoPath`] && <p className={styles.errorText}>{errors[`gitRepos[${i}].repoPath`]}</p>}
          </div>
          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Deploy branch</span>
            <ZestTextbox
              value={repo.deployBranch}
              onChange={e => updateRepo(i, { deployBranch: e.target.value })}
              placeholder="e.g. master"
              zest={{ stretch: true, zSize: 'sm' }}
            />
            {errors[`gitRepos[${i}].deployBranch`] && <p className={styles.errorText}>{errors[`gitRepos[${i}].deployBranch`]}</p>}
          </div>
          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Push args</span>
            <ZestTextbox
              value={repo.pushArgs ?? ''}
              onChange={e => updateRepo(i, { pushArgs: e.target.value })}
              placeholder="--no-verify"
              zest={{ stretch: true, zSize: 'sm' }}
            />
            {repo.pushArgs && /--force(?:-with-lease)?/i.test(repo.pushArgs) && (
              <p style={{ margin: '4px 0 0', fontSize: 11, color: '#e8a838' }}>
                ⚠  &quot;--force&quot; will overwrite remote history.
              </p>
            )}
          </div>
          <div className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Credential Resource</span>
            {creatingFor === i ? (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6, width: '100%' }}>
                <ZestTextbox value={newName} onChange={e => setNewName(e.target.value)}
                  placeholder="Credential name (e.g. github-pat)" zest={{ stretch: true, zSize: 'sm' }} />
                <ZestTextbox value={newValue} onChange={e => setNewValue(e.target.value)}
                  placeholder="PAT / token value" type="password" zest={{ stretch: true, zSize: 'sm' }} />
                <div style={{ display: 'flex', gap: 6 }}>
                  <ZestButton onClick={() => createCredential(i)}
                    zest={{ visualOptions: { variant: 'standard', size: 'sm' } }}>Save</ZestButton>
                  <ZestButton onClick={() => { setCreatingFor(null); setNewName(''); setNewValue(''); }}
                    zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>Cancel</ZestButton>
                </div>
              </div>
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 4, width: '100%' }}>
                <div style={{ display: 'flex', gap: 4, width: '100%' }}>
                  <select value={repo.credentialResourceId ?? ''}
                    onChange={e => updateRepo(i, { credentialResourceId: e.target.value || undefined })}
                    style={{ flex: 1, background: '#131D30', color: '#F0F2F5', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '6px 10px', fontSize: 13 }}>
                    <option value="">— None —</option>
                    {visibleCredentials.map(c => (
                      <option key={c.id} value={c.id}>{c.name}</option>
                    ))}
                  </select>
                  <ZestButton onClick={() => setCreatingFor(i)}
                    zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>+ New</ZestButton>
                </div>
                {allTags.length > 0 && (
                  <select value={filterTag} onChange={e => setFilterTag(e.target.value)}
                    style={{ background: '#131D30', color: '#A8B8CC', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 6, padding: '3px 8px', fontSize: 12 }}>
                    <option value="">All credentials</option>
                    {allTags.map(t => <option key={t} value={t}>{t}</option>)}
                  </select>
                )}
              </div>
            )}
          </div>
        </div>
      ))}
      {repos.length < 10 && (
        <ZestButton onClick={addRepo} zest={{ buttonStyle: 'outline', visualOptions: { size: 'sm' } }}>+ Add Repository</ZestButton>
      )}
    </div>
  );
}
