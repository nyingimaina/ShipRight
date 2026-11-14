import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import JitDeployCard from '../JitDeployCard';
import { api } from '@/shared/ApiService';
import { DEFAULT_JIT_CONFIG, JitConfig } from '../jitDeploy';
import { JitHintsResponse, JitStacksResponse } from '../jitHints';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

jest.mock('@/modules/FilePicker/FilePicker', () => ({
  __esModule: true,
  default: ({ onSelect }: { onSelect: (p: string) => void }) => <button type="button" onClick={() => onSelect('D:\\work\\shop\\deploy\\docker-compose.yml')}>choose file</button>,
}));

const mockApi = api as jest.Mocked<typeof api>;

const SSH = 'git@github.com:o/shop.git';
const HTTPS = 'https://github.com/o/shop.git';

const hintsResp = (over: Partial<JitHintsResponse> = {}): JitHintsResponse => ({
  repos: [{ id: 'r1', label: 'Source code (shop)', url: SSH, ssh: SSH, https: HTTPS, source: 'project-repo', branch: 'main', hadCredentials: false }],
  envFile: '/home/u/shop/.env', branch: 'main', composeProject: 'shop', notes: [], ...over,
});

const stacksResp = (over: Partial<JitStacksResponse> = {}): JitStacksResponse => ({
  stacks: [], projectName: 'shop', match: 'none', warnings: [], suggestedName: null, error: null, ...over,
});

let hints: JitHintsResponse | Error = hintsResp();
let stacks: JitStacksResponse | Error = stacksResp();
let credentialList: unknown[] = [];
let serviceHelpResp: unknown = null;
let branches: unknown = { branches: ['main', 'dev'], defaultBranch: 'main', error: null };

function setup(config: Partial<JitConfig> = {}) {
  mockApi.get.mockImplementation(async (path: string) => {
    if (path.endsWith('/config')) return { config: { ...DEFAULT_JIT_CONFIG, ...config }, services: [], problems: [], serviceHelp: serviceHelpResp } as never;
    if (path.endsWith('/releases')) return { phase: 'Idle', currentTag: null, previousTag: null, needsAttention: false, attention: null, error: null, releases: [] } as never;
    if (path.endsWith('/dependents')) return { pairs: [], note: null } as never;
    if (path.endsWith('/hints')) { if (hints instanceof Error) throw hints; return hints as never; }
    if (path.endsWith('/stacks')) { if (stacks instanceof Error) throw stacks; return stacks as never; }
    if (path.endsWith('/credentials')) return credentialList as never;
    throw new Error('unexpected GET ' + path);
  });
  mockApi.post.mockImplementation(async (path: string) => {
    if (path.endsWith('/branches')) return branches as never;
    return {} as never;
  });
  return render(<JitDeployCard projectId="p1" />);
}

beforeEach(() => {
  jest.clearAllMocks();
  hints = hintsResp(); stacks = stacksResp(); credentialList = []; serviceHelpResp = null;
  branches = { branches: ['main', 'dev'], defaultBranch: 'main', error: null };
});

const TOGGLE = 'Build on the server instead of pushing to a registry';
const ON = { enabled: true, repoUrl: 'git@github.com:o/shop.git', branch: 'main' };

const value = (label: RegExp) => (screen.getByLabelText(label) as HTMLInputElement).value;

describe('turning it on for a project that was never set up', () => {
  it('fills in the address (SSH), the branch and the env file from what ShipRight knows', async () => {
    setup();
    fireEvent.click(await screen.findByLabelText(TOGGLE));
    await waitFor(() => expect(value(/^Git address/)).toBe(SSH));
    expect(value(/^Branch/)).toBe('main');
    expect(value(/^Env file on the server/)).toBe('/home/u/shop/.env');
    expect(screen.getByText(/you have unsaved changes/i)).toBeInTheDocument();
  });

  it('does not guess an address when the project has several repositories, and explains why', async () => {
    hints = hintsResp({
      repos: [
        { id: 'a', label: 'Source code (shop)', url: SSH, ssh: SSH, https: HTTPS, source: 'project-repo', branch: 'main', hadCredentials: false },
        { id: 'b', label: 'Compose repository', url: 'git@github.com:o/infra.git', ssh: 'git@github.com:o/infra.git', https: 'https://github.com/o/infra.git', source: 'compose-repo', branch: null, hadCredentials: false },
      ],
      notes: ['This project has more than one repository. Pick that one.'],
    });
    setup();
    fireEvent.click(await screen.findByLabelText(TOGGLE));
    await screen.findByText(/more than one repository/i);
    expect(value(/^Git address/)).toBe('');
    const picker = screen.getByLabelText('Addresses ShipRight already knows');
    expect(within(picker).getAllByRole('option').length).toBeGreaterThan(2);
  });

  it('leaves a project that is already configured alone', async () => {
    setup({ ...ON, repoUrl: 'git@x:y/z.git', branch: 'release', enabled: false });
    fireEvent.click(await screen.findByLabelText(TOGGLE));
    await waitFor(() => expect(screen.getByLabelText(/^Git address/)).toBeInTheDocument());
    expect(value(/^Git address/)).toBe('git@x:y/z.git');
    expect(value(/^Branch/)).toBe('release');
  });
});

describe('the git address', () => {
  it('lists both forms of every known address, saying what each needs', async () => {
    setup({ enabled: true });
    const picker = await screen.findByLabelText('Addresses ShipRight already knows');
    expect(within(picker).getByRole('option', { name: /SSH \(needs a deploy key\)/ })).toBeInTheDocument();
    expect(within(picker).getByRole('option', { name: /HTTPS \(needs a token\)/ })).toBeInTheDocument();
  });

  it('picking one fills the address and brings its branch along', async () => {
    setup({ enabled: true });
    const picker = await screen.findByLabelText('Addresses ShipRight already knows');
    fireEvent.change(picker, { target: { value: HTTPS } });
    expect(value(/^Git address/)).toBe(HTTPS);
    expect(value(/^Branch/)).toBe('main');
  });

  it('picking one never replaces a branch the person chose', async () => {
    setup({ enabled: true, branch: 'release' });
    fireEvent.change(await screen.findByLabelText('Addresses ShipRight already knows'), { target: { value: SSH } });
    expect(value(/^Branch/)).toBe('release');
  });

  it('can still be typed by hand', async () => {
    setup({ enabled: true });
    fireEvent.change(await screen.findByLabelText(/^Git address/), { target: { value: 'git@host:me/app.git' } });
    expect(value(/^Git address/)).toBe('git@host:me/app.git');
  });

  it('warns, without repeating it, when a password or token is typed into an address', async () => {
    setup({ enabled: true });
    fireEvent.change(await screen.findByLabelText(/^Git address/), { target: { value: 'https://me:ghp_secret@github.com/o/r.git' } });
    expect(screen.getByText(/contains a password or token/i)).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('ghp_secret@github.com');
  });

  it('shows what it found out about the repositories', async () => {
    hints = hintsResp({ notes: ['The saved address of Source code contained a password or token. ShipRight removed it.'] });
    setup({ enabled: true });
    expect(await screen.findByText(/ShipRight removed it/)).toBeInTheDocument();
  });

  it('shows no picker when nothing is known, and the card still works', async () => {
    hints = hintsResp({ repos: [] });
    setup({ enabled: true });
    await screen.findByLabelText(/^Git address/);
    expect(screen.queryByLabelText('Addresses ShipRight already knows')).not.toBeInTheDocument();
  });

  it('works silently when the hints cannot be loaded', async () => {
    hints = new Error('nope');
    setup({ enabled: true });
    await screen.findByLabelText(/^Git address/);
    expect(screen.queryByLabelText('Addresses ShipRight already knows')).not.toBeInTheDocument();
  });
});

describe('the branch', () => {
  it('cannot look for branches until there is an address', async () => {
    setup({ enabled: true });
    expect(await screen.findByRole('button', { name: /find branches on the server/i })).toBeDisabled();
  });

  it('asks the server and offers the real branches while typing', async () => {
    setup({ ...ON });
    fireEvent.click(await screen.findByRole('button', { name: /find branches on the server/i }));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/branches', { url: SSH }));
    await waitFor(() => expect(document.querySelectorAll('#jit-branches option')).toHaveLength(2));
    expect(screen.getByLabelText(/^Branch/)).toHaveAttribute('list', 'jit-branches');
  });

  it('suggests the real default branch when the typed one does not exist', async () => {
    setup({ ...ON, branch: 'master' });
    fireEvent.click(await screen.findByRole('button', { name: /find branches on the server/i }));
    fireEvent.click(await screen.findByRole('button', { name: /use main/i }));
    expect(value(/^Branch/)).toBe('main');
  });

  it('says why when the server could not read the branches', async () => {
    branches = { branches: [], defaultBranch: null, error: 'the server cannot reach that repository yet. It may need a deploy key.' };
    setup({ ...ON });
    fireEvent.click(await screen.findByRole('button', { name: /find branches on the server/i }));
    expect(await screen.findByText(/may need a deploy key/i)).toBeInTheDocument();
  });

  it('shows the server\'s refusal as plain words', async () => {
    mockApi.post.mockRejectedValueOnce({ isError: true, message: 'Choose the server this project deploys to first.' });
    setup({ ...ON });
    fireEvent.click(await screen.findByRole('button', { name: /find branches on the server/i }));
    expect(await screen.findByText(/choose the server this project deploys to first/i)).toBeInTheDocument();
  });
});

describe('the env file', () => {
  it('offers the one from the server settings when empty, and fills it on request', async () => {
    setup({ ...ON });
    fireEvent.click(await screen.findByRole('button', { name: /use \/home\/u\/shop\/\.env/i }));
    expect(value(/^Env file on the server/)).toBe('/home/u/shop/.env');
    expect(screen.queryByRole('button', { name: /use \/home\/u\/shop\/\.env/i })).not.toBeInTheDocument();
  });

  it('offers nothing when it is already filled', async () => {
    setup({ ...ON, envFile: '/mine/.env' });
    await screen.findByLabelText(/^Env file on the server/);
    expect(screen.queryByRole('button', { name: /use \/home\/u\/shop\/\.env/i })).not.toBeInTheDocument();
  });
});

describe('the stack name', () => {
  it('lists the stacks that already run on the server', async () => {
    stacks = stacksResp({ stacks: [{ name: 'blog', status: 'running(1)', configFiles: ['/srv/blog/c.yml'] }] });
    setup({ ...ON });
    await waitFor(() => expect(document.querySelectorAll('#jit-stacks option')).toHaveLength(1));
    expect(screen.getByLabelText(/^Compose project name/)).toHaveAttribute('list', 'jit-stacks');
    expect(screen.getByText(/already on the server: blog/i)).toBeInTheDocument();
  });

  it('warns about a second copy, and offers to take the running stack over', async () => {
    stacks = stacksResp({
      stacks: [{ name: 'shop-old', status: 'running(2)', configFiles: ['/home/u/shop/docker-compose.yml'] }],
      match: 'different', suggestedName: 'shop-old',
      warnings: ["A stack named 'shop-old' already runs from your project folder. That would start a second copy of everything."],
    });
    setup({ ...ON });
    expect(await screen.findByText(/second copy of everything/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /take over 'shop-old'/i }));
    expect(value(/^Compose project name/)).toBe('shop-old');
  });

  it('shows the warning about relative paths when adopting a stack from another folder', async () => {
    stacks = stacksResp({ match: 'same', warnings: ['relative paths in your compose file will point at the new folder'] });
    setup({ ...ON });
    expect(await screen.findByText(/relative paths/i)).toBeInTheDocument();
  });

  it('says so quietly when the stacks could not be listed', async () => {
    stacks = stacksResp({ error: 'Could not list the stacks on the server. Docker may not be installed yet.' });
    setup({ ...ON });
    expect(await screen.findByText(/could not list the stacks/i)).toBeInTheDocument();
  });

  it('suggests the default name as a placeholder', async () => {
    setup({ ...ON });
    expect(await screen.findByPlaceholderText('shop')).toBeInTheDocument();
  });

  it('does not look at the server before building on the server is switched on and saved', async () => {
    setup({ enabled: false });
    await screen.findByLabelText(TOGGLE);
    expect(mockApi.get.mock.calls.some(c => String(c[0]).endsWith('/stacks'))).toBe(false);
  });
});

describe('the compose file and a saved credential', () => {
  const credentials = [{ id: 'c1', name: 'GitHub PAT', hostPattern: 'github.com' }];

  const withCredentials = () => { credentialList = credentials; };

  it('has a compose file field, kept in the settings', async () => {
    setup({ ...ON });
    const field = await screen.findByLabelText(/^Compose file in your repository/);
    expect(field).toHaveValue('');
    fireEvent.change(field, { target: { value: 'deploy/docker-compose.yml' } });
    expect(screen.getByText(/you have unsaved changes/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /save settings/i }));
    await waitFor(() => expect(mockApi.put).toHaveBeenCalledWith('/api/jit/p1/config', expect.objectContaining({ composeFile: 'deploy/docker-compose.yml' })));
  });

  it('refuses a compose file that is not inside the repository, before saving', async () => {
    setup({ ...ON });
    fireEvent.change(await screen.findByLabelText(/^Compose file in your repository/), { target: { value: '../x.yml' } });
    fireEvent.click(screen.getByRole('button', { name: /save settings/i }));
    expect(await screen.findByText(/inside your repository/i)).toBeInTheDocument();
    expect(mockApi.put).not.toHaveBeenCalled();
  });

  it('offers the saved credentials for an https address, and choosing one is an unsaved change', async () => {
    withCredentials();
    setup({ enabled: true, repoUrl: 'https://github.com/o/shop.git', branch: 'main' });
    const select = await screen.findByLabelText('Saved credential');
    await screen.findByRole('option', { name: /GitHub PAT/ });
    fireEvent.change(select, { target: { value: 'c1' } });
    expect(screen.getByText(/you have unsaved changes/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /save settings/i }));
    await waitFor(() => expect(mockApi.put).toHaveBeenCalledWith('/api/jit/p1/config', expect.objectContaining({ credentialResourceId: 'c1' })));
  });

  it('does not offer them for an SSH address', async () => {
    withCredentials();
    setup({ ...ON });
    await screen.findByLabelText(/^Git address/);
    expect(screen.queryByLabelText('Saved credential')).not.toBeInTheDocument();
  });

  it('forgets the chosen credential when the address stops being https', async () => {
    withCredentials();
    setup({ enabled: true, repoUrl: 'https://github.com/o/shop.git', branch: 'main', credentialResourceId: 'c1' });
    await screen.findByLabelText('Saved credential');
    fireEvent.change(screen.getByLabelText(/^Git address/), { target: { value: 'git@github.com:o/shop.git' } });
    fireEvent.click(screen.getByRole('button', { name: /save settings/i }));
    await waitFor(() => expect(mockApi.put).toHaveBeenCalledWith('/api/jit/p1/config', expect.objectContaining({ repoUrl: 'git@github.com:o/shop.git', credentialResourceId: null })));
  });

  it('chooses either a credential or a deploy key, never both', async () => {
    withCredentials();
    mockApi.post.mockImplementation(async (path: string) => {
      if (path.endsWith('/git-access')) return { ok: false, usesDeployKey: false, message: 'Cannot reach the git repository.' } as never;
      if (path.endsWith('/deploy-key')) return { publicKey: 'ssh-ed25519 AAAAexample deploy_shop@box', created: true, keyName: 'deploy_shop', hostLabel: 'GitHub', pageUrl: null, error: null } as never;
      return {} as never;
    });
    setup({ enabled: true, repoUrl: 'https://github.com/o/shop.git', branch: 'main', credentialResourceId: 'c1' });
    await screen.findByLabelText('Saved credential');
    mockApi.post.mockImplementation(async (path: string) => {
      if (path.endsWith('/git-access')) return { ok: false, usesDeployKey: false, message: 'Cannot reach the git repository.' } as never;
      return { publicKey: 'ssh-ed25519 AAAAexample deploy_shop@box', created: true, keyName: 'deploy_shop', hostLabel: 'GitHub', pageUrl: null, error: null } as never;
    });
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    fireEvent.click(await screen.findByRole('button', { name: /create a deploy key/i }));
    await screen.findByText('ssh-ed25519 AAAAexample deploy_shop@box');
    expect(screen.getByLabelText('Saved credential')).toHaveValue('');
  });

  it('uses the chosen credential when checking access and when looking for branches', async () => {
    withCredentials();
    setup({ enabled: true, repoUrl: 'https://github.com/o/shop.git', branch: 'main', credentialResourceId: 'c1' });
    await screen.findByLabelText('Saved credential');
    mockApi.post.mockImplementation(async (path: string) => {
      if (path.endsWith('/git-access')) return { ok: true, usesDeployKey: false, message: 'The server can read your repository.' } as never;
      return { branches: ['main'], defaultBranch: 'main', error: null } as never;
    });
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/git-access', { repoUrl: 'https://github.com/o/shop.git', useDeployKey: false, credentialId: 'c1' }));
    fireEvent.click(screen.getByRole('button', { name: /find branches on the server/i }));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/branches', { url: 'https://github.com/o/shop.git', credentialId: 'c1' }));
  });
});

describe('services from the compose file', () => {
  const found = {
    relativePath: 'deploy/docker-compose.yml', repoRoot: 'D:\\work\\shop',
    services: [{ name: 'api', imageRef: 'shop/api', tagVariable: 'API_TAG', built: true, suggested: true, note: null }], leftAlone: ['db'], notes: [],
  };

  it('explains why there are no services, with the fix, and uses the services found in the project folder', async () => {
    serviceHelpResp = {
      explanation: 'Building on the server needs to know which parts of your compose file to build and tag.',
      steps: ['ShipRight read deploy/docker-compose.yml in your project folder and found these services it can build: api.'], found,
    };
    setup({ ...ON });
    expect(await screen.findByText(/needs to know which parts of your compose file/i)).toBeInTheDocument();
    expect(screen.getByText(/found these services it can build: api/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /^use these services$/i }));
    expect(screen.getByLabelText(/^Compose file in your repository/)).toHaveValue('deploy/docker-compose.yml');
    expect(screen.getByText(/you have unsaved changes/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /save settings/i }));
    await waitFor(() => expect(mockApi.put).toHaveBeenCalledWith('/api/jit/p1/config', expect.objectContaining({
      composeFile: 'deploy/docker-compose.yml', composeServices: [{ name: 'api', imageRef: 'shop/api', tagVariable: 'API_TAG' }],
    })));
  });

  it('picks the compose file with the file picker, and takes its services when the project has none', async () => {
    serviceHelpResp = { explanation: 'Building on the server needs to know which parts of your compose file to build and tag.', steps: ['Pick your compose file with Browse.'], found: null };
    setup({ ...ON });
    await screen.findByText(/needs to know which parts of your compose file/i);
    mockApi.post.mockImplementation(async (path: string) => (path.endsWith('/compose-file') ? found as never : {} as never));

    fireEvent.click(screen.getByRole('button', { name: /browse for the compose file/i }));
    fireEvent.click(screen.getByRole('button', { name: 'choose file' }));
    await screen.findByRole('checkbox', { name: 'Build api' });
    fireEvent.click(screen.getByRole('button', { name: /use this compose file and these services/i }));
    expect(screen.getByLabelText(/^Compose file in your repository/)).toHaveValue('deploy/docker-compose.yml');
    expect(screen.getByText(/services to build, from your compose file: api/i)).toBeInTheDocument();
  });

  it('only sets the path when the project has services of its own', async () => {
    setup({ ...ON });
    await screen.findByLabelText(/^Compose file in your repository/);
    expect(screen.queryByText(/needs to know which parts of your compose file/i)).not.toBeInTheDocument();
    mockApi.post.mockImplementation(async (path: string) => (path.endsWith('/compose-file') ? found as never : {} as never));

    fireEvent.click(screen.getByRole('button', { name: /browse for the compose file/i }));
    fireEvent.click(screen.getByRole('button', { name: 'choose file' }));
    fireEvent.click(await screen.findByRole('button', { name: /use this compose file$/i }));
    expect(screen.getByLabelText(/^Compose file in your repository/)).toHaveValue('deploy/docker-compose.yml');
    expect(screen.queryByText(/services to build, from your compose file/i)).not.toBeInTheDocument();
  });

  it('clears the services taken from the compose file', async () => {
    setup({ ...ON, composeServices: [{ name: 'api', imageRef: 'shop/api', tagVariable: 'API_TAG' }] });
    fireEvent.click(await screen.findByRole('button', { name: /clear these services/i }));
    expect(screen.queryByText(/services to build, from your compose file/i)).not.toBeInTheDocument();
    expect(screen.getByText(/you have unsaved changes/i)).toBeInTheDocument();
  });
});

describe('giving the server access to the code', () => {
  it('shows the access panel under the address, and a created key turns the setting on as an unsaved change', async () => {
    setup({ ...ON });
    mockApi.post.mockImplementation(async (path: string) => {
      if (path.endsWith('/git-access')) return { ok: false, usesDeployKey: false, message: 'Cannot reach the git repository.' } as never;
      if (path.endsWith('/deploy-key')) return { publicKey: 'ssh-ed25519 AAAAexample deploy_shop@box', created: true, keyName: 'deploy_shop', hostLabel: 'GitHub', pageUrl: 'https://github.com/o/shop/settings/keys', error: null } as never;
      return {} as never;
    });
    expect(await screen.findByText(/can the server read your code/i)).toBeInTheDocument();
    expect(screen.queryByText(/you have unsaved changes/i)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    fireEvent.click(await screen.findByRole('button', { name: /create a deploy key/i }));
    await screen.findByText('ssh-ed25519 AAAAexample deploy_shop@box');
    expect(screen.getByText(/you have unsaved changes/i)).toBeInTheDocument();
  });
});
