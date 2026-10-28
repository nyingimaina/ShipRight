import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import QuickSetup from '../QuickSetup';
import { api } from '@/shared/ApiService';
import { IApplyResult, ISetupPlan } from '@/shared/types/ISetup';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn() } }));

jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

jest.mock('@/modules/FilePicker/FilePicker', () => ({
  __esModule: true,
  default: ({ onSelect }: any) => <button type="button" onClick={() => onSelect('D:\\work\\online-shop')}>pick folder</button>,
}));

const mockApi = api as jest.Mocked<typeof api>;

const plan = (over: Partial<ISetupPlan> = {}): ISetupPlan => ({
  rootPath: 'D:\\work\\online-shop',
  repoRoot: 'D:\\work\\online-shop',
  repoBranch: 'main',
  warnings: [],
  components: [{
    stackId: 'dotnet', kind: 'aspnet-web', name: 'Api',
    directory: 'D:\\work\\online-shop\\Api', contextDirectory: 'D:\\work\\online-shop\\Api',
    confidence: 'High', port: 8080, version: null, facts: {}, notes: [],
  }],
  items: [
    { id: 'GitRepo:.git', kind: 'GitRepo', status: 'WillCreate', path: 'D:\\work\\online-shop\\.git', relativePath: '.git', reason: 'Sets up git.', content: null, componentName: null },
    { id: 'GitIgnore:.gitignore', kind: 'GitIgnore', status: 'Exists', path: 'D:\\work\\online-shop\\.gitignore', relativePath: '.gitignore', reason: 'You already have a .gitignore, so ShipRight leaves it alone.', content: null, componentName: null },
    { id: 'Dockerfile:Api/Dockerfile', kind: 'Dockerfile', status: 'WillCreate', path: 'D:\\work\\online-shop\\Api\\Dockerfile', relativePath: 'Api/Dockerfile', reason: 'Tells Docker how to build Api.', content: 'FROM scratch\n', componentName: 'Api' },
    { id: 'VersionFile:Api/version.txt', kind: 'VersionFile', status: 'WillCreate', path: 'D:\\work\\online-shop\\Api\\version.txt', relativePath: 'Api/version.txt', reason: 'Version of Api.', content: '0.1.0\n', componentName: 'Api' },
  ],
  ...over,
});

const applied = (over: Partial<IApplyResult> = {}): IApplyResult => ({
  items: [], committed: true, commitError: null, ...over,
});

const clean = { isClean: true, sha: 'abc', entries: [], error: null };

const unsaved = (paths: string[]) => ({
  isClean: false, sha: null, error: 'This project has no commits yet. Save your work as a first commit.',
  entries: paths.map(path => ({ status: '??', path, description: 'New file, not saved to git yet' })),
});

/** Queues the replies for: apply, the git check that follows it, then creating the project. */
function queueCreate(o: { result?: IApplyResult; tree?: unknown; project?: unknown } = {}) {
  queueUntilGitCheck(o.result, o.tree);
  mockApi.post.mockResolvedValueOnce(o.project ?? { id: 'p1', name: 'Online Shop' });
}

/** Queues only apply and the git check, for scenarios that stop to ask before creating the project. */
function queueUntilGitCheck(result?: IApplyResult, tree?: unknown) {
  mockApi.post
    .mockResolvedValueOnce({ result: result ?? applied(), plan: plan() })
    .mockResolvedValueOnce(tree ?? clean);
}

const callOf = (path: string) => mockApi.post.mock.calls.find(c => c[0] === path);

function setup(handlers: { onSaved?: jest.Mock; onCancel?: jest.Mock; onManual?: jest.Mock } = {}) {
  const props = { onSaved: jest.fn(), onCancel: jest.fn(), onManual: jest.fn(), ...handlers };
  render(<QuickSetup {...props} />);
  return props;
}

async function reachReview(p: ISetupPlan = plan()) {
  mockApi.post.mockResolvedValueOnce({ plan: p });
  fireEvent.click(screen.getByText('pick folder'));
  fireEvent.click(screen.getByText('Look at my project'));
  await screen.findByText('Create project');
}

describe('QuickSetup', () => {
  beforeEach(() => {
    jest.resetAllMocks();
    mockApi.get.mockResolvedValue({ home: '/home/dev' });
  });

  it('asks for a folder first and cannot continue without one', () => {
    setup();
    expect(screen.getByText('Look at my project')).toBeDisabled();
  });

  it('plans the chosen folder', async () => {
    setup();
    await reachReview();
    expect(mockApi.post).toHaveBeenCalledWith('/api/setup/plan', { rootPath: 'D:\\work\\online-shop' });
  });

  it('describes what it found in plain language', async () => {
    setup();
    await reachReview();
    expect(screen.getByText('Api')).toBeInTheDocument();
    expect(screen.getByText('ASP.NET website or API')).toBeInTheDocument();
  });

  it('lists files it will add, ticked, and files already in place, without checkboxes', async () => {
    setup();
    await reachReview();
    expect(screen.getByLabelText('Add Api/Dockerfile')).toBeChecked();
    expect(screen.getByLabelText('Add Api/version.txt')).toBeChecked();
    expect(screen.queryByLabelText('Add .gitignore')).not.toBeInTheDocument();
    expect(screen.getByText(/You already have a \.gitignore/)).toBeInTheDocument();
  });

  it('shows warnings from the plan', async () => {
    setup();
    await reachReview(plan({ warnings: ['Your .gitignore does not ignore .env files.'] }));
    expect(screen.getByText('Your .gitignore does not ignore .env files.')).toBeInTheDocument();
  });

  it('prefills the project name from the folder', async () => {
    setup();
    await reachReview();
    expect(screen.getByLabelText('Project name')).toHaveValue('Online Shop');
  });

  it('applies the approved files, then creates the project, then reports it saved', async () => {
    const props = setup();
    await reachReview();
    queueCreate();

    fireEvent.click(screen.getByText('Create project'));

    await waitFor(() => expect(props.onSaved).toHaveBeenCalledWith({ id: 'p1', name: 'Online Shop' }));
    expect(callOf('/api/setup/apply')![1]).toEqual({
      rootPath: 'D:\\work\\online-shop',
      approvedItemIds: ['GitRepo:.git', 'Dockerfile:Api/Dockerfile', 'VersionFile:Api/version.txt'],
      commit: true,
    });
    expect(callOf('/api/projects')![1]).toMatchObject({
      name: 'Online Shop', type: 'Freeform',
      services: [{ name: 'Api', buildContextPath: 'D:\\work\\online-shop\\Api', dockerImageName: 'online-shop/api' }],
      gitRepos: [{ repoPath: 'D:\\work\\online-shop', deployBranch: 'main' }],
    });
  });

  it('gives the project a Linux working folder under the WSL home and skips the compose-repo steps', async () => {
    setup();
    await reachReview();
    queueCreate();

    fireEvent.click(screen.getByText('Create project'));

    await waitFor(() => expect(callOf('/api/projects')).toBeDefined());
    expect(mockApi.get).toHaveBeenCalledWith('/api/setup/wsl-home');
    expect(callOf('/api/projects')![1]).toMatchObject({
      wsl: { workingDir: '/home/dev/.shipright/projects/online-shop' },
      server: { deployMode: 'EnvCompose' },
      buildSource: 'CleanSnapshot',
    });
  });

  it('tells the person when WSL cannot be reached, before writing anything', async () => {
    const props = setup();
    await reachReview();
    mockApi.get.mockRejectedValueOnce({ message: 'ShipRight could not reach WSL (Windows Subsystem for Linux).' });

    fireEvent.click(screen.getByText('Create project'));

    expect(await screen.findByText(/could not reach WSL/)).toBeInTheDocument();
    expect(callOf('/api/setup/apply')).toBeUndefined();
    expect(props.onSaved).not.toHaveBeenCalled();
  });

  it('leaves out optional files the person unticked', async () => {
    setup();
    await reachReview();
    fireEvent.click(screen.getByLabelText('Add .git'));
    queueCreate();

    fireEvent.click(screen.getByText('Create project'));

    await waitFor(() => expect(callOf('/api/projects')).toBeDefined());
    expect((callOf('/api/setup/apply')![1] as any).approvedItemIds)
      .toEqual(['Dockerfile:Api/Dockerfile', 'VersionFile:Api/version.txt']);
  });

  it('refuses to create a project whose only part would have no version file', async () => {
    const props = setup();
    await reachReview();
    fireEvent.click(screen.getByLabelText('Add Api/version.txt'));

    fireEvent.click(screen.getByText('Create project'));

    expect(await screen.findByText(/needs a Dockerfile and a version\.txt/)).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenCalledTimes(1);
    expect(props.onSaved).not.toHaveBeenCalled();
  });

  it('does not ask git to save the files when that option is turned off', async () => {
    setup();
    await reachReview();
    fireEvent.click(screen.getByLabelText('Save the new files in git for me'));
    mockApi.post
      .mockResolvedValueOnce({ result: applied({ committed: false }), plan: plan() })
      .mockResolvedValueOnce({ id: 'p1' });

    fireEvent.click(screen.getByText('Create project'));

    await waitFor(() => expect(callOf('/api/projects')).toBeDefined());
    expect((callOf('/api/setup/apply')![1] as any).commit).toBe(false);
    // They chose not to touch git, so there is no nudge about saving their own code either.
    expect(callOf('/api/setup/tree-status')).toBeUndefined();
  });

  it('uses the name the person typed', async () => {
    setup();
    await reachReview();
    fireEvent.change(screen.getByLabelText('Project name'), { target: { value: 'Corner Store' } });
    queueCreate();

    fireEvent.click(screen.getByText('Create project'));

    await waitFor(() => expect(callOf('/api/projects')).toBeDefined());
    expect((callOf('/api/projects')![1] as any).name).toBe('Corner Store');
    expect((callOf('/api/projects')![1] as any).services[0].dockerImageName).toBe('corner-store/api');
  });

  it('will not create a project without a name', async () => {
    setup();
    await reachReview();
    fireEvent.change(screen.getByLabelText('Project name'), { target: { value: '  ' } });
    expect(screen.getByText('Create project')).toBeDisabled();
  });

  it('stops and explains when a file could not be written, creating no project', async () => {
    const props = setup();
    await reachReview();
    mockApi.post.mockResolvedValueOnce({
      result: applied({ items: [{ itemId: 'Dockerfile:Api/Dockerfile', outcome: 'Failed', message: 'Access to the path is denied.' }] }),
      plan: plan(),
    });

    fireEvent.click(screen.getByText('Create project'));

    expect(await screen.findByText(/Access to the path is denied/)).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenCalledTimes(2);
    expect(props.onSaved).not.toHaveBeenCalled();
  });

  it('still creates the project when only the git commit failed, and says how to finish', async () => {
    const props = setup();
    await reachReview();
    queueCreate({ result: applied({ committed: false, commitError: 'Author identity unknown' }) });

    fireEvent.click(screen.getByText('Create project'));

    await waitFor(() => expect(props.onSaved).toHaveBeenCalled());
    expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('Author identity unknown'));
  });

  it('skips the apply call and the git check when every file is already in place', async () => {
    const props = setup();
    const allThere = plan({ items: plan().items.map(i => ({ ...i, status: 'Exists' as const })) });
    await reachReview(allThere);
    mockApi.post.mockResolvedValueOnce({ id: 'p1' });

    fireEvent.click(screen.getByText('Create project'));

    await waitFor(() => expect(props.onSaved).toHaveBeenCalled());
    expect(mockApi.post).toHaveBeenCalledTimes(2);
    expect(mockApi.post.mock.calls[1][0]).toBe('/api/projects');
    expect(screen.queryByLabelText('Save the new files in git for me')).not.toBeInTheDocument();
  });

  // ── Saving the person's own code ─────────────────────────────────────────

  describe('when their own code is not saved in git yet', () => {
    const SOURCE = ['Api/Api.csproj', 'Api/Program.cs'];

    async function reachSaveStep() {
      const props = setup();
      await reachReview();
      queueUntilGitCheck(undefined, unsaved(SOURCE));
      fireEvent.click(screen.getByText('Create project'));
      await screen.findByText('Save all of these in git');
      return props;
    }

    it('checks the repo after adding files, and pauses to ask before creating the project', async () => {
      const props = await reachSaveStep();
      expect(callOf('/api/setup/tree-status')![1]).toEqual({ repoPath: 'D:\\work\\online-shop' });
      expect(callOf('/api/projects')).toBeUndefined();
      expect(props.onSaved).not.toHaveBeenCalled();
    });

    it('lists the unsaved files in plain language', async () => {
      await reachSaveStep();
      expect(screen.getByText('Api/Api.csproj')).toBeInTheDocument();
      expect(screen.getByText('Api/Program.cs')).toBeInTheDocument();
    });

    it('saves exactly those files, then creates the project', async () => {
      const props = await reachSaveStep();
      mockApi.post.mockResolvedValueOnce({ success: true }).mockResolvedValueOnce({ id: 'p1' });

      fireEvent.click(screen.getByText('Save all of these in git'));

      await waitFor(() => expect(props.onSaved).toHaveBeenCalledWith({ id: 'p1' }));
      expect(callOf('/api/setup/commit')![1]).toEqual({
        repoPath: 'D:\\work\\online-shop', message: 'Save project files', paths: SOURCE,
      });
    });

    it('lets the person skip and still creates the project', async () => {
      const props = await reachSaveStep();
      mockApi.post.mockResolvedValueOnce({ id: 'p1' });

      fireEvent.click(screen.getByText('Skip for now'));

      await waitFor(() => expect(props.onSaved).toHaveBeenCalled());
      expect(callOf('/api/setup/commit')).toBeUndefined();
    });

    it('explains a refused save and leaves the choice to the person', async () => {
      const props = await reachSaveStep();
      mockApi.post.mockRejectedValueOnce({ message: 'Refusing to commit .env: it looks like a secret.' });

      fireEvent.click(screen.getByText('Save all of these in git'));

      expect(await screen.findByText(/Refusing to commit \.env/)).toBeInTheDocument();
      expect(props.onSaved).not.toHaveBeenCalled();
      expect(screen.getByText('Skip for now')).toBeInTheDocument();
    });

    it('caps a very long list but still saves every file', async () => {
      setup();
      await reachReview();
      const many = Array.from({ length: 80 }, (_, i) => `src/file${i}.cs`);
      queueUntilGitCheck(undefined, unsaved(many));
      fireEvent.click(screen.getByText('Create project'));
      await screen.findByText('Save all of these in git');
      expect(screen.getByText(/and 30 more/)).toBeInTheDocument();

      mockApi.post.mockResolvedValueOnce({ success: true }).mockResolvedValueOnce({ id: 'p1' });
      fireEvent.click(screen.getByText('Save all of these in git'));

      await waitFor(() => expect(callOf('/api/setup/commit')).toBeDefined());
      expect((callOf('/api/setup/commit')![1] as any).paths).toHaveLength(80);
    });
  });

  it('carries on to create the project if the git check itself cannot run', async () => {
    const props = setup();
    await reachReview();
    mockApi.post
      .mockResolvedValueOnce({ result: applied(), plan: plan() })
      .mockRejectedValueOnce({ message: 'git is not installed' })
      .mockResolvedValueOnce({ id: 'p1' });

    fireEvent.click(screen.getByText('Create project'));

    await waitFor(() => expect(props.onSaved).toHaveBeenCalled());
  });

  it('explains when nothing was recognised and offers no way to create', async () => {
    setup();
    mockApi.post.mockResolvedValueOnce({
      plan: plan({ components: [], items: [], warnings: ['ShipRight did not recognise a .NET or Node.js project in this folder.'] }),
    });
    fireEvent.click(screen.getByText('pick folder'));
    fireEvent.click(screen.getByText('Look at my project'));

    expect(await screen.findByText(/did not recognise a \.NET or Node\.js project/)).toBeInTheDocument();
    expect(screen.queryByText('Create project')).not.toBeInTheDocument();
    expect(screen.getByText('Choose a different folder')).toBeInTheDocument();
  });

  it('shows the server message when planning fails', async () => {
    setup();
    mockApi.post.mockRejectedValueOnce({ message: 'That folder does not exist.' });
    fireEvent.click(screen.getByText('pick folder'));
    fireEvent.click(screen.getByText('Look at my project'));
    expect(await screen.findByText('That folder does not exist.')).toBeInTheDocument();
  });

  it('lets the person switch to the manual wizard', () => {
    const props = setup();
    fireEvent.click(screen.getByText('Set it up manually instead'));
    expect(props.onManual).toHaveBeenCalled();
  });

  it('lets the person go back to choose another folder', async () => {
    setup();
    await reachReview();
    fireEvent.click(screen.getByText('← Back'));
    expect(screen.getByText('Look at my project')).toBeInTheDocument();
  });
});
