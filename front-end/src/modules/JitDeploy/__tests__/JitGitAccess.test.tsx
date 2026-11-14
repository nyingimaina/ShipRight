import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import JitGitAccess from '../JitGitAccess';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;
const URL_ = 'git@github.com:o/shop.git';
const KEY = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIexample deploy_shop@box';

let access: unknown = { ok: true, usesDeployKey: false, message: 'The server can read your repository.' };
let key: unknown = { publicKey: KEY, created: true, keyName: 'deploy_shop', hostLabel: 'GitHub', pageUrl: 'https://github.com/o/shop/settings/keys', error: null };

beforeEach(() => {
  jest.clearAllMocks();
  access = { ok: true, usesDeployKey: false, message: 'The server can read your repository.' };
  key = { publicKey: KEY, created: true, keyName: 'deploy_shop', hostLabel: 'GitHub', pageUrl: 'https://github.com/o/shop/settings/keys', error: null };
  (navigator as unknown as { clipboard: unknown }).clipboard = { writeText: jest.fn().mockResolvedValue(undefined) };
  mockApi.post.mockImplementation(async (path: string) => {
    if (path.endsWith('/git-access')) { if (access instanceof Error) throw access; return access as never; }
    if (path.endsWith('/deploy-key')) { if (key instanceof Error) throw key; return key as never; }
    throw new Error('unexpected POST ' + path);
  });
});

const setup = (props: Partial<React.ComponentProps<typeof JitGitAccess>> = {}) => {
  const onUseDeployKey = jest.fn();
  const utils = render(<JitGitAccess base="/api/jit/p1" repoUrl={URL_} useDeployKey={false} onUseDeployKey={onUseDeployKey} {...props} />);
  return { onUseDeployKey, ...utils };
};

describe('checking that the server can read the code', () => {
  it('cannot check without an address', () => {
    setup({ repoUrl: '  ' });
    expect(screen.getByRole('button', { name: /check access/i })).toBeDisabled();
  });

  it('asks the server, and says plainly that it can read the repository', async () => {
    setup();
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    expect(await screen.findByText(/the server can read your repository/i)).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/git-access', { repoUrl: URL_, useDeployKey: false });
    expect(screen.queryByRole('button', { name: /create a deploy key/i })).not.toBeInTheDocument();
  });

  it('says why it cannot, and offers to make a deploy key', async () => {
    access = { ok: false, usesDeployKey: false, message: 'Cannot reach the git repository. Check the address, the network, and that this server has access.' };
    setup();
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    expect(await screen.findByText(/cannot reach the git repository/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /create a deploy key/i })).toBeInTheDocument();
  });

  it('shows the server\'s refusal as plain words', async () => {
    access = Object.assign(new Error('x'), { isError: true, message: 'Choose the server this project deploys to first.' });
    setup();
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    expect(await screen.findByText(/choose the server this project deploys to first/i)).toBeInTheDocument();
  });

  it('forgets an old answer when the address changes', async () => {
    const { rerender, onUseDeployKey } = setup();
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    await screen.findByText(/the server can read your repository/i);
    rerender(<JitGitAccess base="/api/jit/p1" repoUrl="git@github.com:o/other.git" useDeployKey={false} onUseDeployKey={onUseDeployKey} />);
    expect(screen.queryByText(/the server can read your repository/i)).not.toBeInTheDocument();
  });
});

describe('using a saved credential', () => {
  it('sends the chosen credential with the check', async () => {
    setup({ credentialId: 'c1' });
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    await screen.findByText(/the server can read your repository/i);
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/git-access', { repoUrl: URL_, useDeployKey: false, credentialId: 'c1' });
  });

  it('sends nothing extra when there is none', async () => {
    setup({ credentialId: null });
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    await screen.findByText(/the server can read your repository/i);
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/git-access', { repoUrl: URL_, useDeployKey: false });
  });
});

describe('making a deploy key', () => {
  async function failThenCreate() {
    access = { ok: false, usesDeployKey: false, message: 'Cannot reach the git repository.' };
    const utils = setup();
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    fireEvent.click(await screen.findByRole('button', { name: /create a deploy key/i }));
    await screen.findByText(KEY);
    return utils;
  }

  it('shows the public key to copy, where to paste it, and turns the setting on', async () => {
    const { onUseDeployKey } = await failThenCreate();
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/deploy-key', { repoUrl: URL_ });
    expect(onUseDeployKey).toHaveBeenCalledWith(true);

    const link = screen.getByRole('link', { name: /open github's deploy keys page/i });
    expect(link).toHaveAttribute('href', 'https://github.com/o/shop/settings/keys');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', expect.stringContaining('noreferrer'));
    expect(screen.getByText(/leave write access off/i)).toBeInTheDocument();
    expect(screen.getByText(/this key only lets the server read/i)).toBeInTheDocument();
  });

  it('copies the key', async () => {
    await failThenCreate();
    fireEvent.click(screen.getByRole('button', { name: /copy the deploy key/i }));
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith(KEY));
  });

  it('checks again with the key once the person has pasted it', async () => {
    const { rerender, onUseDeployKey } = await failThenCreate();
    access = { ok: true, usesDeployKey: true, message: 'The server can read your repository.' };
    rerender(<JitGitAccess base="/api/jit/p1" repoUrl={URL_} useDeployKey onUseDeployKey={onUseDeployKey} />);
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    expect(await screen.findByText(/the server can read your repository/i)).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenLastCalledWith('/api/jit/p1/git-access', { repoUrl: URL_, useDeployKey: true });
  });

  it('gives general directions for a git host without a known page', async () => {
    key = { publicKey: KEY, created: true, keyName: 'deploy_shop', hostLabel: 'git.example.com', pageUrl: null, error: null };
    await failThenCreate();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.getByText(/in git\.example\.com, open the repository's settings and add a deploy key/i)).toBeInTheDocument();
  });

  it('says so when the key could not be made', async () => {
    key = { publicKey: null, created: false, keyName: null, hostLabel: '', pageUrl: null, error: 'Could not make a key on the server. Check that ssh-keygen is installed.' };
    access = { ok: false, usesDeployKey: false, message: 'Cannot reach the git repository.' };
    const { onUseDeployKey } = setup();
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    fireEvent.click(await screen.findByRole('button', { name: /create a deploy key/i }));
    expect(await screen.findByText(/could not make a key on the server/i)).toBeInTheDocument();
    expect(onUseDeployKey).not.toHaveBeenCalled();
  });

  it('says so when the server cannot be reached for the key', async () => {
    key = Object.assign(new Error('x'), { isError: true, message: 'Choose the server this project deploys to first.' });
    access = { ok: false, usesDeployKey: false, message: 'no' };
    setup();
    fireEvent.click(screen.getByRole('button', { name: /check access/i }));
    fireEvent.click(await screen.findByRole('button', { name: /create a deploy key/i }));
    expect(await screen.findByText(/choose the server this project deploys to first/i)).toBeInTheDocument();
  });
});

describe('a project that already uses a deploy key', () => {
  it('says so, and can show the key again without making a new one', async () => {
    key = { ...(key as object), created: false };
    setup({ useDeployKey: true });
    expect(screen.getByText(/reads your code with a deploy key made on the server/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /show the deploy key/i }));
    expect(await screen.findByText(KEY)).toBeInTheDocument();
  });

  it('can switch back to whatever access the server already has', () => {
    const { onUseDeployKey } = setup({ useDeployKey: true });
    fireEvent.click(screen.getByRole('button', { name: /stop using the deploy key/i }));
    expect(onUseDeployKey).toHaveBeenCalledWith(false);
  });
});
