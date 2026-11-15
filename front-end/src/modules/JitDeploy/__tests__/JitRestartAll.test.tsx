import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import JitDeployCard from '../JitDeployCard';
import { api } from '@/shared/ApiService';
import { DEFAULT_JIT_CONFIG, JitConfig } from '../jitDeploy';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  static CLOSED = 2;
  onmessage: ((e: { data: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  readyState = 1;
  constructor(public url: string) { FakeEventSource.instances.push(this); }
  close() { this.readyState = 2; }
  emit(type: string, data: Record<string, unknown>) { act(() => { this.onmessage?.({ data: JSON.stringify({ type, data }) }); }); }
}

const release = (n: number, over = {}) => ({
  tag: `r${n}-${String(n).repeat(7)}`, sha: String(n).repeat(40), createdUtc: '2026-11-13T11:00:00Z', message: null,
  isCurrent: false, isPrevious: false, ...over,
});
const releasesResp = (over = {}) => ({
  phase: 'Idle', currentTag: 'r2-2222222', previousTag: 'r1-1111111', needsAttention: false, attention: null, error: null,
  releases: [release(2, { isCurrent: true }), release(1, { isPrevious: true })], ...over,
});
const ON = { enabled: true, repoUrl: 'git@github.com:o/r.git', branch: 'master' };

function setup(releases: unknown = releasesResp()) {
  mockApi.get.mockImplementation((async (path: string) => {
    if (path.endsWith('/config')) return { config: { ...DEFAULT_JIT_CONFIG, ...ON } as JitConfig, services: [], problems: [] };
    if (path.endsWith('/releases')) return releases;
    if (path.endsWith('/dependents')) return { pairs: [], note: null };
    if (path.startsWith('/api/builds/')) return { status: 'Deployed', errorSummary: null };
    throw new Error('unexpected GET ' + path);
  }) as never);
  return render(<JitDeployCard projectId="p1" />);
}

const restartButton = () => screen.getByRole('button', { name: /restart all services/i });

beforeEach(() => {
  jest.clearAllMocks();
  FakeEventSource.instances = [];
  (global as unknown as { EventSource: unknown }).EventSource = FakeEventSource;
});

describe('restart all services', () => {
  it('is offered once a release is running, and asks before doing anything', async () => {
    setup();
    await screen.findByLabelText('Git address');
    fireEvent.click(await screen.findByRole('button', { name: /restart all services/i }));
    expect(screen.getByText(/restart every service/i)).toBeInTheDocument();
    expect(screen.getByText(/data volumes are kept/i)).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });

  it('restarts after the person says yes: stops and starts everything, then reports it', async () => {
    setup();
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b9' } as never);
    fireEvent.click(await screen.findByRole('button', { name: /restart all services/i }));
    fireEvent.click(screen.getByRole('button', { name: /yes, restart everything/i }));

    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/restart-all', {});
    FakeEventSource.instances[0].emit('LogLine', { line: '[deploy] Stopping all services' });
    expect(await screen.findByText('[deploy] Stopping all services')).toBeInTheDocument();
    FakeEventSource.instances[0].emit('DeployCompleted', { status: 'Deployed' });
    expect(await screen.findByText('Every service was restarted.')).toBeInTheDocument();
  });

  it('can be called off', async () => {
    setup();
    await screen.findByLabelText('Git address');
    fireEvent.click(await screen.findByRole('button', { name: /restart all services/i }));
    fireEvent.click(screen.getByRole('button', { name: /no, leave them running/i }));
    expect(screen.queryByText(/restart every service/i)).not.toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });

  it('is not offered before there is a release, and not while something is running or needs attention', async () => {
    setup(releasesResp({ currentTag: null, releases: [] }));
    await screen.findByLabelText('Git address');
    await waitFor(() => expect(mockApi.get).toHaveBeenCalledWith('/api/jit/p1/releases'));
    expect(restartButton()).toBeDisabled();
  });

  it('is disabled while a deploy is running', async () => {
    setup();
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b1' } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Deploy now' }));
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    expect(restartButton()).toBeDisabled();
  });

  it('after a successful deploy it offers a fresh start right where the result is shown', async () => {
    setup();
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b1' } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Deploy now' }));
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    FakeEventSource.instances[0].emit('DeployCompleted', { status: 'Deployed' });

    expect(await screen.findByText('Deployed successfully.')).toBeInTheDocument();
    expect(screen.getByText(/want a completely fresh start/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /restart everything now/i }));
    expect(screen.getByText(/restart every service/i)).toBeInTheDocument();
  });

  it('shows the reason when a restart fails', async () => {
    mockApi.get.mockImplementation((async (path: string) => {
      if (path.endsWith('/config')) return { config: { ...DEFAULT_JIT_CONFIG, ...ON }, services: [], problems: [] };
      if (path.endsWith('/releases')) return releasesResp();
      if (path.endsWith('/dependents')) return { pairs: [], note: null };
      if (path.startsWith('/api/builds/')) return { status: 'DeployFailed', errorSummary: 'api did not come back healthy: it exited.' };
      throw new Error('unexpected GET ' + path);
    }) as never);
    render(<JitDeployCard projectId="p1" />);
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b2' } as never);
    fireEvent.click(await screen.findByRole('button', { name: /restart all services/i }));
    fireEvent.click(screen.getByRole('button', { name: /yes, restart everything/i }));
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    FakeEventSource.instances[0].emit('DeployCompleted', { status: 'DeployFailed' });
    expect(await screen.findByRole('alert')).toHaveTextContent('api did not come back healthy: it exited.');
  });
});
