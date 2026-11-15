import { render, screen, fireEvent, waitFor, act, within } from '@testing-library/react';
import toast from 'react-hot-toast';
import JitDeployCard from '../JitDeployCard';
import { api } from '@/shared/ApiService';
import { DEFAULT_JIT_CONFIG } from '../jitDeploy';
import { stepText } from '../jitSteps';

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

function setup(build: unknown = { status: 'Deployed', errorSummary: null }) {
  mockApi.get.mockImplementation((async (path: string) => {
    if (path.endsWith('/config')) return { config: { ...DEFAULT_JIT_CONFIG, enabled: true, repoUrl: 'git@github.com:o/r.git', branch: 'master' }, services: [], problems: [] };
    if (path.endsWith('/releases')) return { phase: 'Idle', currentTag: 'r2-2222222', previousTag: null, needsAttention: false, attention: null, error: null, releases: [release(2, { isCurrent: true })] };
    if (path.endsWith('/dependents')) return { pairs: [], note: null };
    if (path.startsWith('/api/builds/')) return build;
    throw new Error('unexpected GET ' + path);
  }) as never);
  return render(<JitDeployCard projectId="p1" />);
}

async function startDeploy() {
  await screen.findByLabelText('Git address');
  mockApi.post.mockResolvedValueOnce({ buildId: 'b1' } as never);
  fireEvent.click(screen.getByRole('button', { name: 'Deploy now' }));
  await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
  return FakeEventSource.instances[0];
}

beforeEach(() => {
  jest.clearAllMocks();
  FakeEventSource.instances = [];
  (global as unknown as { EventSource: unknown }).EventSource = FakeEventSource;
});

describe('stepText', () => {
  it('recognises a finished-step line and returns just the words', () => {
    expect(stepText('[step] ✓ Code fetched from git (2.1 s)')).toBe('Code fetched from git (2.1 s)');
  });
  it('ignores every other line', () => {
    expect(stepText('[deploy] Fetching the code from git…')).toBeNull();
    expect(stepText('[step] something else')).toBeNull();
    expect(stepText('')).toBeNull();
  });
});

describe('progress while a deploy runs', () => {
  it('lists each finished step as it arrives, with a tick', async () => {
    setup();
    const es = await startDeploy();
    es.emit('LogLine', { line: '[deploy] Checking the server…' });
    es.emit('LogLine', { line: '[step] ✓ Server checked: Docker, disk space and git access (0.4 s)' });
    es.emit('LogLine', { line: '[step] ✓ Code fetched from git (2.1 s)' });

    const list = await screen.findByRole('list', { name: 'Progress' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('Server checked: Docker, disk space and git access (0.4 s)');
    expect(items[1]).toHaveTextContent('Code fetched from git (2.1 s)');
  });

  it('tells the person with one notification that always shows the latest finished step', async () => {
    setup();
    const es = await startDeploy();
    es.emit('LogLine', { line: '[step] ✓ Code fetched from git (2.1 s)' });
    es.emit('LogLine', { line: '[step] ✓ Built api, web (38.2 s)' });

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Built api, web (38.2 s)', expect.objectContaining({ id: 'jit-step' })));
    expect(toast.success).toHaveBeenCalledWith('Code fetched from git (2.1 s)', expect.objectContaining({ id: 'jit-step' }));
  });

  it('ordinary log lines do not become steps or notifications', async () => {
    setup();
    const es = await startDeploy();
    es.emit('LogLine', { line: '[deploy] Building api, web' });
    expect(await screen.findByText('[deploy] Building api, web')).toBeInTheDocument();
    expect(screen.queryByRole('list', { name: 'Progress' })).not.toBeInTheDocument();
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('finishes the same notification with the result', async () => {
    setup();
    const es = await startDeploy();
    es.emit('LogLine', { line: '[step] ✓ Built api, web (38.2 s)' });
    es.emit('DeployCompleted', { status: 'Deployed' });
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Deployed successfully.', expect.objectContaining({ id: 'jit-step' })));
  });

  it('shows a failure in the same notification, in the words the server used', async () => {
    setup({ status: 'DeployFailed', errorSummary: 'The build failed, so nothing was changed.' });
    const es = await startDeploy();
    es.emit('DeployCompleted', { status: 'DeployFailed' });
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('The build failed, so nothing was changed.', expect.objectContaining({ id: 'jit-step' })));
  });

  it('starts a fresh list for the next operation', async () => {
    setup();
    const es = await startDeploy();
    es.emit('LogLine', { line: '[step] ✓ Code fetched from git (2.1 s)' });
    expect(await screen.findByRole('list', { name: 'Progress' })).toBeInTheDocument();
    es.emit('DeployCompleted', { status: 'Deployed' });
    await screen.findByText('Deployed successfully.');

    mockApi.post.mockResolvedValueOnce({ buildId: 'b2' } as never);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Deploy now' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Deploy now' }));
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(2));
    expect(screen.queryByRole('list', { name: 'Progress' })).not.toBeInTheDocument();
  });
});
