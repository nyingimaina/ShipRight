import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import JitDeployCard from '../JitDeployCard';
import { api } from '@/shared/ApiService';
import { DEFAULT_JIT_CONFIG } from '../jitDeploy';
import type { JitHintsResponse } from '../jitHints';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));
jest.mock('@/modules/FilePicker/FilePicker', () => ({
  __esModule: true,
  default: ({ onSelect, label }: { onSelect: (p: string) => void; label?: string }) =>
    <button type="button" onClick={() => onSelect('D:\\work\\shop\\deploy\\docker-compose.yml')}>{`choose ${label}`}</button>,
}));

const mockApi = api as jest.Mocked<typeof api>;

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  readyState = 1;
  onmessage: ((e: { data: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  constructor(public url: string) { FakeEventSource.instances.push(this); }
  close() { this.readyState = 2; }
}

const hints = (envFile: string): JitHintsResponse => ({
  repos: [], envFile, branch: 'main', composeProject: 'shop', notes: [],
});

const ON = { enabled: true, repoUrl: 'git@github.com:o/shop.git', branch: 'main' };

interface SetupOptions {
  duplicates?: unknown;
  envText?: string;
  envFilePath?: string;
  serverEnv?: string;
}

function setup({ duplicates = { duplicates: [], skipped: false, note: null }, envText = 'DB=project-db\nNEW=x\n', envFilePath = '', serverEnv = 'DB=server-db\nKEEP=1\n' }: SetupOptions = {}) {
  mockApi.get.mockImplementation(async (path: string) => {
    if (path.endsWith('/config')) return { config: { ...DEFAULT_JIT_CONFIG, ...ON, envText, envFile: envFilePath }, services: [], problems: [], serviceHelp: null } as never;
    if (path.endsWith('/env-duplicates')) {
      if (duplicates instanceof Error) throw duplicates;
      return duplicates as never;
    }
    if (path.endsWith('/releases')) return { phase: 'Idle', currentTag: null, previousTag: null, needsAttention: false, attention: null, error: null, releases: [] } as never;
    if (path.endsWith('/dependents')) return { pairs: [], note: null } as never;
    if (path.endsWith('/hints')) return hints(serverEnv) as never;
    if (path.endsWith('/stacks')) return { stacks: [], projectName: 'shop', match: 'none', warnings: [], suggestedName: null, error: null } as never;
    if (path.endsWith('/credentials')) return [] as never;
    throw new Error('unexpected GET ' + path);
  });
  mockApi.post.mockImplementation(async (path: string) => {
    if (path.endsWith('/branches')) return { branches: ['main'], defaultBranch: 'main', error: null } as never;
    if (path.endsWith('/deploy')) return { buildId: 'b1' } as never;
    if (path.endsWith('/env-duplicates/remove')) return { duplicates: [], skipped: false, note: null } as never;
    return {} as never;
  });
  mockApi.put.mockResolvedValue({ config: {} } as never);
  return render(<JitDeployCard projectId="p1" />);
}

const duplicates = {
  skipped: false,
  note: null,
  duplicates: [{ name: 'DB', serverValue: 'server-db', projectValue: 'project-db' }],
};

async function clickDeploy() {
  await screen.findByRole('button', { name: 'Deploy now' });
  await waitFor(() => expect(screen.getByRole('button', { name: 'Deploy now' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Deploy now' }));
}

const deployCalled = () => mockApi.post.mock.calls.some(c => String(c[0]).endsWith('/deploy'));
const removalCalled = () => mockApi.post.mock.calls.find(c => String(c[0]).endsWith('/env-duplicates/remove'));

beforeEach(() => {
  jest.clearAllMocks();
  FakeEventSource.instances = [];
  (global as unknown as { EventSource: unknown }).EventSource = FakeEventSource;
});

describe('deploy-time duplicate variables', () => {
  it('stops and shows both values before deploying', async () => {
    setup({ duplicates });
    await clickDeploy();

    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('DB');
    expect(dialog).toHaveTextContent('server-db');
    expect(dialog).toHaveTextContent('project-db');
    expect(deployCalled()).toBe(false);
  });

  it('removing from the server applies the choice, then deploys', async () => {
    setup({ duplicates });
    await clickDeploy();
    fireEvent.click(await screen.findByRole('button', { name: /remove from server/i }));

    await waitFor(() => expect(removalCalled()).toBeTruthy());
    expect(removalCalled()![1]).toMatchObject({ removeFrom: 'server', names: ['DB'] });
    await waitFor(() => expect(deployCalled()).toBe(true));
  });

  it('removing from the project reloads the settings, then deploys', async () => {
    setup({ duplicates });
    await clickDeploy();
    fireEvent.click(await screen.findByRole('button', { name: /remove from project/i }));

    await waitFor(() => expect(removalCalled()).toBeTruthy());
    expect(removalCalled()![1]).toMatchObject({ removeFrom: 'project', names: ['DB'] });
    await waitFor(() => expect(mockApi.get.mock.calls.some(c => String(c[0]).endsWith('/config'))).toBe(true));
    await waitFor(() => expect(deployCalled()).toBe(true));
  });

  it('keeping both deploys without changing either side', async () => {
    setup({ duplicates });
    await clickDeploy();
    fireEvent.click(await screen.findByRole('button', { name: /keep both/i }));

    await waitFor(() => expect(deployCalled()).toBe(true));
    expect(removalCalled()).toBeFalsy();
  });

  it('deploys straight away when there are no duplicates', async () => {
    setup();
    await clickDeploy();

    await waitFor(() => expect(deployCalled()).toBe(true));
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });

  it('deploys straight away when the project uses its own env file', async () => {
    setup({ duplicates: { duplicates: [], skipped: true, note: 'own file' }, envFilePath: '/etc/shop/.env' });
    await clickDeploy();

    await waitFor(() => expect(deployCalled()).toBe(true));
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });
});
