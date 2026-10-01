import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import React from 'react';
import ComposeRepoCard from '../../modules/ProjectDetail/ComposeRepoCard';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn(), delete: jest.fn() },
  sseUrl: (p: string) => p,
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled, onClickCapture }: any) => (
    <button onClick={onClick} disabled={disabled} data-testid={`btn-${String(children).toLowerCase().replace(/[^a-z]+/g, '-')}`}>
      {children}
    </button>
  ),
}));

jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => (
    <input data-testid="clone-url-input" value={value} onChange={onChange} placeholder={placeholder} />
  ),
}));

const mocked = api as jest.Mocked<typeof api>;

const HEALTHY = {
  wslPath: '/home/nyingi/work/fua/docker/lattice-docker',
  windowsPath: '\\\\wsl.localhost\\Ubuntu\\home\\nyingi\\work\\fua\\docker\\lattice-docker',
  exists: true,
  isGitRepo: true,
  cloneUrl: 'https://nyingi.visualstudio.com/DefaultCollection/lattice-docker/_git/lattice-docker',
  health: { isHealthy: true, summary: 'git fsck found no problems.' },
  appliesTo: true,
};

beforeEach(() => {
  jest.clearAllMocks();
  (global as any).EventSource = class {
    close() {}
    addEventListener() {}
  };
});

describe('ComposeRepoCard', () => {
  it('shows the WSL compose working directory and its health', async () => {
    mocked.get.mockResolvedValue(HEALTHY as any);

    render(<ComposeRepoCard projectId="p1" />);

    expect(await screen.findByText(HEALTHY.wslPath)).toBeTruthy();
    expect(screen.getByText('git fsck found no problems.')).toBeTruthy();
    expect(screen.getByText(/healthy/i)).toBeTruthy();
    expect(mocked.get).toHaveBeenCalledWith('/api/projects/p1/compose-repo');
  });

  it('surfaces a corrupt repo so it can be repaired', async () => {
    mocked.get.mockResolvedValue({ ...HEALTHY, health: { isHealthy: false, summary: 'error: object file .git/objects/7f/030bce is empty' } } as any);

    render(<ComposeRepoCard projectId="p1" />);

    expect(await screen.findByText(/is empty/)).toBeTruthy();
    expect(screen.getByText(/corrupt/i)).toBeTruthy();
  });

  it('offers a delete action for the compose repo', async () => {
    mocked.get.mockResolvedValue(HEALTHY as any);
    mocked.post.mockResolvedValue({ opId: 'op1', message: 'started' } as any);
    const confirmSpy = jest.spyOn(window, 'confirm').mockReturnValue(true);

    render(<ComposeRepoCard projectId="p1" />);
    await screen.findByText(HEALTHY.wslPath);

    fireEvent.click(screen.getByTestId('btn-delete-compose-repo'));

    await waitFor(() =>
      expect(mocked.post).toHaveBeenCalledWith('/api/projects/p1/compose-repo/delete', {}));
    expect(confirmSpy).toHaveBeenCalled();
  });

  it('does not delete when the confirmation is declined', async () => {
    mocked.get.mockResolvedValue(HEALTHY as any);
    jest.spyOn(window, 'confirm').mockReturnValue(false);

    render(<ComposeRepoCard projectId="p1" />);
    await screen.findByText(HEALTHY.wslPath);

    fireEvent.click(screen.getByTestId('btn-delete-compose-repo'));

    expect(mocked.post).not.toHaveBeenCalled();
  });

  it('saves a corrected clone URL so the wrong app repo is never cloned', async () => {
    mocked.get.mockResolvedValue({ ...HEALTHY, exists: false, isGitRepo: false, cloneUrl: null, health: { isHealthy: true, summary: 'The WSL directory does not exist yet.' } } as any);
    mocked.post.mockResolvedValue({ cloneUrl: 'https://correct/lattice-docker' } as any);

    render(<ComposeRepoCard projectId="p1" />);
    await screen.findByText(/does not exist/i);

    fireEvent.change(screen.getByTestId('clone-url-input'), { target: { value: 'https://correct/lattice-docker' } });
    fireEvent.click(screen.getByTestId('btn-save-url'));

    await waitFor(() =>
      expect(mocked.post).toHaveBeenCalledWith('/api/projects/p1/compose-repo/clone-url',
        { cloneUrl: 'https://correct/lattice-docker' }));
  });

  it('clones the compose repo into the WSL working dir', async () => {
    mocked.get.mockResolvedValue({ ...HEALTHY, exists: false, isGitRepo: false, health: { isHealthy: true, summary: 'The WSL directory does not exist yet.' } } as any);
    mocked.post.mockResolvedValue({ opId: 'op1' } as any);

    render(<ComposeRepoCard projectId="p1" />);
    await screen.findByText(/does not exist/i);

    fireEvent.click(screen.getByTestId('btn-clone'));

    await waitFor(() =>
      expect(mocked.post).toHaveBeenCalledWith('/api/projects/p1/compose-repo/clone', {}));
  });

  it('says nothing is needed for EnvCompose projects', async () => {
    mocked.get.mockResolvedValue({ ...HEALTHY, appliesTo: false, health: { isHealthy: true, summary: 'EnvCompose deploy — no compose repo involved.' } } as any);

    render(<ComposeRepoCard projectId="p1" />);

    expect(await screen.findByText(/envcompose/i)).toBeTruthy();
    expect(screen.queryByTestId('btn-delete-compose-repo')).toBeNull();
  });

  it('shows an error when the status call fails', async () => {
    mocked.get.mockRejectedValue({ message: 'boom' });

    render(<ComposeRepoCard projectId="p1" />);

    expect(await screen.findByText(/boom/)).toBeTruthy();
  });
});
