import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import JitDeployCard from '../JitDeployCard';
import { api } from '@/shared/ApiService';
import { DEFAULT_JIT_CONFIG, JitConfig, JitDryRun } from '../jitDeploy';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;

const ON: Partial<JitConfig> = { enabled: true, repoUrl: 'git@github.com:o/r.git', branch: 'master' };

function setup(config: Partial<JitConfig> = ON) {
  mockApi.get.mockImplementation(async (path: string) => {
    if (path.endsWith('/config')) {
      return {
        config: { ...DEFAULT_JIT_CONFIG, ...config },
        services: [{ name: 'api', tagVariable: 'API_TAG', imageRef: 'shop/api', composeImageLine: '${API_TAG:-shop/api:latest}' }],
        problems: [],
      } as never;
    }
    if (path.endsWith('/releases')) {
      return { phase: 'Idle', currentTag: 'r2-2222222', previousTag: 'r1-1111111', needsAttention: false, attention: null, releases: [], error: null } as never;
    }
    if (path.endsWith('/dependents')) return { pairs: [], note: null } as never;
    throw new Error('unexpected GET ' + path);
  });
  return render(<JitDeployCard projectId="p1" />);
}

const plan = (over: Partial<JitDryRun> = {}): JitDryRun => ({
  ok: true,
  error: null,
  attemptId: 'a1',
  diagnosticsUrl: '/api/jit/p1/deploy-attempts/a1/download',
  branch: 'master',
  commit: '3f2a9c1d0e8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f',
  tag: 'r3-3f2a9c1',
  services: ['api', 'web'],
  commands: [{ step: 'Build the images', command: 'docker compose -p shop build api web' }],
  warnings: ['Untracked files will not be on the server: scratch.sql'],
  problems: [],
  currentTag: 'r2-2222222',
  phase: 'Idle',
  guardSummary: "Allowed: 1 local checkout checked, 'master' is fully pushed.",
  targetChecked: true,
  ...over,
});

describe('JitDeployCard dry run', () => {
  beforeEach(() => jest.resetAllMocks());

  it('shows the plan and a diagnostics link, and changes nothing', async () => {
    setup();
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce(plan() as never);

    fireEvent.click(screen.getByRole('button', { name: 'Dry run' }));

    const region = await screen.findByRole('region', { name: 'Dry run plan' });
    expect(region).toHaveTextContent('this deploy can start');
    expect(region).toHaveTextContent('Nothing on the server was changed');
    expect(region).toHaveTextContent('read-only commands only');
    expect(region).toHaveTextContent('r3-3f2a9c1');
    expect(region).toHaveTextContent('api, web');
    expect(region).toHaveTextContent('scratch.sql');
    expect(screen.getByRole('link', { name: 'Download diagnostics' })).toHaveAttribute(
      'href', '/api/jit/p1/deploy-attempts/a1/download');
    expect(screen.getByText(/What a real deploy would run/)).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/dry-run',
      { force: false, commit: null, allowWithoutRollback: false, rebuildAll: false });
  });

  it('keeps a failed preflight visible, with its diagnostics link, instead of only a toast', async () => {
    setup();
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce(plan({
      ok: false,
      error: 'Commit your local changes before deploying (src/Program.cs).',
      tag: null,
      commands: [],
    }) as never);

    fireEvent.click(screen.getByRole('button', { name: 'Dry run' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Commit your local changes before deploying');
    expect(screen.getByRole('link', { name: 'Download diagnostics' })).toHaveAttribute(
      'href', '/api/jit/p1/deploy-attempts/a1/download');
  });

  it('shows the server’s diagnostics link when even starting the check fails', async () => {
    setup();
    await screen.findByLabelText('Git address');
    mockApi.post.mockRejectedValueOnce({
      message: 'Choose the server this project deploys to.',
      diagnosticsUrl: '/api/jit/p1/deploy-attempts/a9/download',
    });

    fireEvent.click(screen.getByRole('button', { name: 'Dry run' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Choose the server this project deploys to.');
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Choose the server this project deploys to.'));
    expect(screen.getByRole('link', { name: 'Download diagnostics' })).toHaveAttribute(
      'href', '/api/jit/p1/deploy-attempts/a9/download');
  });

  it('cannot dry run with unsaved settings', async () => {
    setup();
    await screen.findByLabelText('Git address');
    fireEvent.change(screen.getByLabelText('Branch'), { target: { value: 'dev' } });

    expect(screen.getByRole('button', { name: 'Dry run' })).toBeDisabled();
  });
});
