import { useState } from 'react';
import toast from 'react-hot-toast';
import ZestButton from 'jattac.libs.web.zest-button';
import FilePicker from '@/modules/FilePicker/FilePicker';
import HoverTip from '@/shared/components/HoverTip';
import { api } from '@/shared/ApiService';
import { getBrowserTimeZone } from '@/shared/timeZone';
import { IApiError, IProject, IProjectInput } from '@/shared/types/IProject';
import { IApplyResult, ICleanTreeResult, IDirtyEntry, IPlanItem, ISetupPlan } from '@/shared/types/ISetup';
import { buildProjectInput, defaultApprovedIds, describeComponent, describeItem, suggestProjectName } from './projectFromPlan';
import styles from './QuickSetup.module.css';

interface Props {
  onSaved: (project: IProject) => void;
  onCancel: () => void;
  /** Switches to the original step-by-step wizard. */
  onManual: () => void;
}

type Step = 'folder' | 'review' | 'save';

const MAX_LISTED_FILES = 50;

const errorMessage = (e: unknown, fallback: string): string => {
  const list = Array.isArray(e) ? e : [e];
  const messages = (list as IApiError[]).map(x => x?.message).filter(Boolean);
  return messages.length > 0 ? messages.join('\n') : fallback;
};

/** Files in the repo that are not saved yet; an unreadable repo simply means there is nothing to offer. */
async function unsavedFiles(repoPath: string): Promise<IDirtyEntry[]> {
  try {
    const tree = await api.post<ICleanTreeResult>('/api/setup/tree-status', { repoPath });
    return tree.entries;
  } catch {
    return [];
  }
}

export default function QuickSetup({ onSaved, onCancel, onManual }: Props) {
  const [step, setStep] = useState<Step>('folder');
  const [rootPath, setRootPath] = useState('');
  const [plan, setPlan] = useState<ISetupPlan | null>(null);
  const [approved, setApproved] = useState<Set<string>>(new Set());
  const [name, setName] = useState('');
  const [commit, setCommit] = useState(true);
  const [planning, setPlanning] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Set while the person is deciding whether to save their own code in git before the project is created.
  const [unsaved, setUnsaved] = useState<IDirtyEntry[]>([]);
  const [pending, setPending] = useState<IProjectInput | null>(null);

  const look = async () => {
    setPlanning(true);
    setError(null);
    try {
      const { plan: result } = await api.post<{ plan: ISetupPlan }>('/api/setup/plan', { rootPath });
      setPlan(result);
      setApproved(new Set(defaultApprovedIds(result)));
      setName(suggestProjectName(result));
      setStep('review');
    } catch (e) {
      setError(errorMessage(e, 'ShipRight could not look at that folder.'));
    } finally {
      setPlanning(false);
    }
  };

  const toggle = (id: string) =>
    setApproved(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });

  const create = async () => {
    if (!plan) return;
    setSaving(true);
    setError(null);
    try {
      // First, so a machine without a working WSL is told before a single file has been written.
      const { home } = await api.get<{ home: string }>('/api/setup/wsl-home');

      const toCreate = plan.items.filter(i => i.status === 'WillCreate' && approved.has(i.id));

      // A plan with only the approved files in it: a part of the project whose Dockerfile or version file
      // was unticked (and is not already there) simply does not become a service.
      const effective: ISetupPlan = {
        ...plan,
        items: plan.items.filter(i => i.status === 'Exists' || approved.has(i.id)),
      };
      const input = buildProjectInput(effective, name.trim(), getBrowserTimeZone(), home);
      if (input.services.length === 0) {
        setError('ShipRight needs a Dockerfile and a version.txt for at least one part of your project. Tick them above and try again.');
        return;
      }

      if (toCreate.length > 0) {
        const { result } = await api.post<{ result: IApplyResult; plan: ISetupPlan }>('/api/setup/apply', {
          rootPath: plan.rootPath,
          approvedItemIds: toCreate.map(i => i.id),
          commit,
        });
        const failed = result.items.filter(r => r.outcome === 'Failed');
        if (failed.length > 0) {
          const label = (id: string) => plan.items.find(i => i.id === id)?.relativePath ?? id;
          setError(failed.map(f => `${label(f.itemId)}: ${f.message}`).join('\n'));
          return;
        }
        if (commit && !result.committed && result.commitError) {
          toast.error(`Your files were added, but ShipRight could not save them to git yet: ${result.commitError}`);
        }

        // Builds come from saved versions only. In a folder that was never in git, the person's own code is
        // still unsaved at this point, so offer to save it before the first build trips over it.
        if (commit) {
          const own = await unsavedFiles(plan.repoRoot);
          if (own.length > 0) {
            setUnsaved(own);
            setPending(input);
            setStep('save');
            return;
          }
        }
      }

      await finish(input);
    } catch (e) {
      setError(errorMessage(e, 'Something went wrong while creating the project.'));
    } finally {
      setSaving(false);
    }
  };

  const finish = async (input: IProjectInput) => onSaved(await api.post<IProject>('/api/projects', input));

  const saveOwnCode = async () => {
    if (!plan || !pending) return;
    setSaving(true);
    setError(null);
    try {
      await api.post('/api/setup/commit', {
        repoPath: plan.repoRoot,
        message: 'Save project files',
        paths: unsaved.map(e => e.path),
      });
      await finish(pending);
    } catch (e) {
      setError(errorMessage(e, 'ShipRight could not save those files to git.'));
    } finally {
      setSaving(false);
    }
  };

  const skipSavingOwnCode = async () => {
    if (!pending) return;
    setSaving(true);
    setError(null);
    try {
      await finish(pending);
    } catch (e) {
      setError(errorMessage(e, 'Something went wrong while creating the project.'));
    } finally {
      setSaving(false);
    }
  };

  if (step === 'save' && plan) {
    return (
      <div className={styles.wrap}>
        <div>
          <h2 className={styles.title}>Your code is not saved in git yet</h2>
          <p className={styles.sub}>
            ShipRight builds from saved versions of your code, so what you build is exactly what you saved.
            These files are not saved yet. Saving them takes a snapshot you can always come back to.
          </p>
        </div>

        <section className={styles.section}>
          {unsaved.slice(0, MAX_LISTED_FILES).map(e => (
            <div key={e.path} className={styles.doneRow}>
              <div>
                <div className={styles.path}>{e.path}</div>
                <div className={styles.muted}>{e.description}</div>
              </div>
            </div>
          ))}
          {unsaved.length > MAX_LISTED_FILES && (
            <span className={styles.muted}>…and {unsaved.length - MAX_LISTED_FILES} more</span>
          )}
        </section>

        {error && <p className={styles.error}>{error}</p>}

        <div className={styles.footer}>
          <ZestButton onClick={saveOwnCode} disabled={saving} zest={{ visualOptions: { variant: 'standard' } }}>
            {saving ? 'Saving…' : 'Save all of these in git'}
          </ZestButton>
          <ZestButton onClick={skipSavingOwnCode} disabled={saving} zest={{ buttonStyle: 'outline' }}>Skip for now</ZestButton>
        </div>
      </div>
    );
  }

  const manualLink = (
    <button type="button" className={styles.linkButton} onClick={onManual}>Set it up manually instead</button>
  );

  if (step === 'folder' || !plan) {
    return (
      <div className={styles.wrap}>
        <div>
          <h2 className={styles.title}>Where is your project?</h2>
          <p className={styles.sub}>
            Pick the folder with your code. ShipRight works out what it is and sets up anything that is missing.
            You will see everything before it is written.
          </p>
        </div>

        <FilePicker dirsOnly storageKey="project-root" label="Project folder" onSelect={setRootPath} />

        {error && <p className={styles.error}>{error}</p>}

        <div className={styles.footer}>
          <ZestButton onClick={look} disabled={!rootPath || planning}
            zest={{ visualOptions: { variant: 'standard' } }}>
            {planning ? 'Looking…' : 'Look at my project'}
          </ZestButton>
          <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          {manualLink}
        </div>
      </div>
    );
  }

  const willCreate = plan.items.filter(i => i.status === 'WillCreate');
  const inPlace = plan.items.filter(i => i.status === 'Exists');
  const recognised = plan.components.length > 0;

  if (!recognised) {
    return (
      <div className={styles.wrap}>
        <h2 className={styles.title}>ShipRight could not find a project here</h2>
        {plan.warnings.map(w => <p key={w} className={styles.warning}>{w}</p>)}
        <div className={styles.footer}>
          <ZestButton onClick={() => setStep('folder')} zest={{ visualOptions: { variant: 'standard' } }}>
            Choose a different folder
          </ZestButton>
          <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
          {manualLink}
        </div>
      </div>
    );
  }

  return (
    <div className={styles.wrap}>
      <div>
        <h2 className={styles.title}>Here is what ShipRight found</h2>
        <p className={styles.sub}>{plan.rootPath}</p>
      </div>

      <section className={styles.section}>
        <span className={styles.sectionTitle}>Your project</span>
        {plan.components.map(c => (
          <div key={c.directory} className={styles.card}>
            <div className={styles.cardHead}>
              <span className={styles.cardName}>{c.name}</span>
              {c.confidence !== 'High' && <span className={styles.badge}>please double-check</span>}
            </div>
            <span className={styles.muted}>{describeComponent(c)}</span>
          </div>
        ))}
      </section>

      {plan.warnings.length > 0 && (
        <section className={styles.section}>
          {plan.warnings.map(w => <p key={w} className={styles.warning}>{w}</p>)}
        </section>
      )}

      {willCreate.length > 0 && (
        <section className={styles.section}>
          <span className={styles.sectionTitle}>
            Files ShipRight will add
            <HoverTip label="Files ShipRight will add">
              {'These are the small files that let your project be built and shipped. ShipRight only adds files that are missing - it never changes or replaces yours. You can untick anything you do not want, and edit the files afterwards.'}
            </HoverTip>
          </span>
          {willCreate.map(item => (
            <ItemRow key={item.id} item={item} checked={approved.has(item.id)} onToggle={() => toggle(item.id)} />
          ))}
        </section>
      )}

      {inPlace.length > 0 && (
        <section className={styles.section}>
          <span className={styles.sectionTitle}>Already in place</span>
          {inPlace.map(item => (
            <div key={item.id} className={styles.doneRow}>
              <span className={styles.tick}>✓</span>
              <div>
                <div className={styles.itemName}>{describeItem(item)}</div>
                <div className={styles.muted}>{item.reason}</div>
              </div>
            </div>
          ))}
        </section>
      )}

      <section className={styles.section}>
        <span className={styles.sectionTitle}>Name</span>
        <label className={styles.fieldLabel} htmlFor="quick-setup-name">Project name</label>
        <input id="quick-setup-name" className={styles.input} value={name}
          onChange={e => setName(e.target.value)} placeholder="e.g. Online Shop" />
        {willCreate.length > 0 && (
          <label className={styles.checkRow}>
            <input type="checkbox" aria-label="Save the new files in git for me" checked={commit}
              onChange={e => setCommit(e.target.checked)} />
            <span>
              Save the new files in git for me
              <HoverTip label="Save the new files in git">
                {'Git keeps a saved history of your code. ShipRight builds from saved versions, so the new files need to be saved too. ShipRight saves only the files it just added - never anything else.'}
              </HoverTip>
            </span>
          </label>
        )}
      </section>

      {error && <p className={styles.error}>{error}</p>}

      <div className={styles.footer}>
        <ZestButton onClick={() => { setError(null); setStep('folder'); }} zest={{ buttonStyle: 'outline' }}>← Back</ZestButton>
        <ZestButton onClick={create} disabled={saving || !name.trim()}
          zest={{ visualOptions: { variant: 'standard' }, semanticType: 'save' }}>
          {saving ? 'Setting up…' : 'Create project'}
        </ZestButton>
        <ZestButton onClick={onCancel} zest={{ buttonStyle: 'outline', semanticType: 'cancel' }}>Cancel</ZestButton>
      </div>
    </div>
  );
}

function ItemRow({ item, checked, onToggle }: { item: IPlanItem; checked: boolean; onToggle: () => void }) {
  return (
    <div className={styles.itemRow}>
      <input type="checkbox" aria-label={`Add ${item.relativePath}`} checked={checked} onChange={onToggle} />
      <div className={styles.itemBody}>
        <div className={styles.itemName}>{describeItem(item)}</div>
        <div className={styles.muted}>{item.reason}</div>
        <div className={styles.path}>{item.relativePath}</div>
        {item.content && (
          <details className={styles.details}>
            <summary>Show what it will contain</summary>
            <pre className={styles.pre}>{item.content}</pre>
          </details>
        )}
      </div>
    </div>
  );
}
