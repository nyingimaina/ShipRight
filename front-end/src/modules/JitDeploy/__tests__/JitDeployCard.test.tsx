import { render, screen, fireEvent, waitFor, within, act } from '@testing-library/react';
import toast from 'react-hot-toast';
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
  closed = false;
  constructor(public url: string) { FakeEventSource.instances.push(this); }
  close() { this.closed = true; this.readyState = 2; }
  emit(type: string, data: Record<string, unknown>) { act(() => { this.onmessage?.({ data: JSON.stringify({ type, data }) }); }); }
}

const cfgResp = (over: Partial<JitConfig> = {}, problems: string[] = []) => ({
  config: { ...DEFAULT_JIT_CONFIG, ...over },
  services: [{ name: 'api', tagVariable: 'API_TAG', imageRef: 'shop/api', composeImageLine: '${API_TAG:-shop/api:latest}' }],
  problems,
});

const release = (n: number, over = {}) => ({
  tag: `r${n}-${String(n).repeat(7)}`, sha: String(n).repeat(40), createdUtc: '2026-11-13T11:00:00Z', message: null,
  isCurrent: false, isPrevious: false, ...over,
});

const releasesResp = (over = {}) => ({
  phase: 'Idle', currentTag: 'r2-2222222', previousTag: 'r1-1111111', needsAttention: false, attention: null, error: null,
  releases: [release(2, { isCurrent: true }), release(1, { isPrevious: true })],
  ...over,
});

const dependentsResp = (over = {}) => ({ pairs: [{ service: 'web', dependent: 'proxy', restart: true }], note: null, ...over });

const ON = { enabled: true, repoUrl: 'git@github.com:o/r.git', branch: 'master' };

function setup(opts: { config?: Partial<JitConfig>; problems?: string[]; releases?: unknown; dependents?: unknown; build?: unknown } = {}) {
  mockApi.get.mockImplementation(async (path: string) => {
    if (path.endsWith('/config')) return cfgResp(opts.config, opts.problems) as never;
    if (path.endsWith('/releases')) return (opts.releases ?? releasesResp()) as never;
    if (path.endsWith('/dependents')) return (opts.dependents ?? dependentsResp()) as never;
    if (path.startsWith('/api/builds/')) return (opts.build ?? { status: 'Deployed', errorSummary: null }) as never;
    throw new Error('unexpected GET ' + path);
  });
  return render(<JitDeployCard projectId="p1" />);
}

const TOGGLE = 'Build on the server instead of pushing to a registry';

async function ready() { await screen.findByLabelText(TOGGLE); }

describe('JitDeployCard', () => {
  beforeEach(() => {
    jest.resetAllMocks();
    FakeEventSource.instances = [];
    (global as unknown as { EventSource: unknown }).EventSource = FakeEventSource;
    Object.assign(navigator, { clipboard: { writeText: jest.fn().mockResolvedValue(undefined) } });
  });

  // ── servers that do not offer it ──────────────────────────────────────────

  it('stays completely invisible, and silent, when the server does not offer build-on-server (cloud mode, older server)', async () => {
    mockApi.get.mockRejectedValue({ isError: true, message: 'HTTP 404' });
    const { container } = render(<JitDeployCard projectId="p1" />);
    await waitFor(() => expect(mockApi.get).toHaveBeenCalled());
    await waitFor(() => expect(container).toBeEmptyDOMElement());
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('shows nothing while it is still finding out', async () => {
    mockApi.get.mockReturnValue(new Promise(() => {}) as never);
    const { container } = render(<JitDeployCard projectId="p1" />);
    expect(container).toBeEmptyDOMElement();
  });

  // ── switching it on ───────────────────────────────────────────────────────

  it('starts switched off for a project that has never used it, and shows nothing else', async () => {
    setup();
    await ready();
    expect(screen.getByLabelText(TOGGLE)).not.toBeChecked();
    expect(screen.queryByLabelText('Git address')).not.toBeInTheDocument();
    expect(mockApi.get).toHaveBeenCalledTimes(1);   // no call to the server machine while it is off
    expect(mockApi.get).toHaveBeenCalledWith('/api/jit/p1/config');
  });

  it('turning it on reveals the settings', async () => {
    setup();
    await ready();
    fireEvent.click(screen.getByLabelText(TOGGLE));
    expect(screen.getByLabelText('Git address')).toBeInTheDocument();
    expect(screen.getByLabelText('Branch')).toHaveValue('master');
  });

  it('shows the saved settings for a project that uses it, and loads what is on the server', async () => {
    setup({ config: { ...ON, branch: 'release', envFile: '/home/me/.env' } });
    expect(await screen.findByLabelText('Git address')).toHaveValue('git@github.com:o/r.git');
    expect(screen.getByLabelText('Branch')).toHaveValue('release');
    fireEvent.click(screen.getByRole('button', { name: /advanced settings/i }));
    expect(screen.getByLabelText('Env file on the server (optional)')).toHaveValue('/home/me/.env');
    await waitFor(() => expect(mockApi.get).toHaveBeenCalledWith('/api/jit/p1/releases'));
    expect(mockApi.get).toHaveBeenCalledWith('/api/jit/p1/dependents');
  });

  it('gives every field a plain-language tip that says where to find the value', async () => {
    setup({ config: ON });
    await screen.findByLabelText('Git address');
    fireEvent.click(screen.getByRole('button', { name: /advanced settings/i }));
    for (const label of ['Git address', 'Branch', 'Env file on the server', 'Compose project name'])
      expect(screen.getByRole('button', { name: new RegExp(`Help: ${label}`) })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Help: Git address/ }));
    expect(screen.getByRole('tooltip')).toHaveTextContent(/Code/);
  });

  it('keeps the numbers under "advanced" until asked', async () => {
    setup({ config: ON });
    await screen.findByLabelText('Git address');
    expect(screen.queryByLabelText('Releases to keep')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /advanced settings/i }));
    expect(screen.getByLabelText('Releases to keep')).toHaveValue(3);
    expect(screen.getByLabelText('Stop grace period (seconds)')).toHaveValue(30);
    expect(screen.getByLabelText('Health check timeout (seconds)')).toHaveValue(60);
    expect(screen.getByLabelText('Settle time (seconds)')).toHaveValue(10);
    expect(screen.getByLabelText('Minimum free disk space (MB)')).toHaveValue(3000);
  });

  // ── saving ────────────────────────────────────────────────────────────────

  it('saves the settings and says so', async () => {
    setup();
    await ready();
    mockApi.put.mockResolvedValueOnce({ config: {} } as never);

    fireEvent.click(screen.getByLabelText(TOGGLE));
    fireEvent.change(screen.getByLabelText('Git address'), { target: { value: 'git@github.com:me/shop.git' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));

    await waitFor(() => expect(mockApi.put).toHaveBeenCalled());
    const [path, body] = mockApi.put.mock.calls[0] as [string, JitConfig];
    expect(path).toBe('/api/jit/p1/config');
    expect(body).toMatchObject({ enabled: true, repoUrl: 'git@github.com:me/shop.git', branch: 'master' });
    await waitFor(() => expect(toast.success).toHaveBeenCalled());
  });

  it('refuses to save an incomplete form, and shows which field is wrong', async () => {
    setup();
    await ready();
    fireEvent.click(screen.getByLabelText(TOGGLE));
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(await screen.findByText(/Enter the git address/i)).toBeInTheDocument();
    expect(mockApi.put).not.toHaveBeenCalled();
  });

  it('shows the server\'s reason when saving is refused', async () => {
    setup({ config: ON });
    await screen.findByLabelText('Git address');
    mockApi.put.mockRejectedValueOnce({ message: 'The branch name is not valid.' });
    fireEvent.change(screen.getByLabelText('Branch'), { target: { value: 'dev' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('The branch name is not valid.'));
  });

  it('switching it off and saving keeps the other settings', async () => {
    setup({ config: { ...ON, branch: 'release' } });
    await screen.findByLabelText('Git address');
    mockApi.put.mockResolvedValueOnce({ config: {} } as never);
    fireEvent.click(screen.getByLabelText(TOGGLE));
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));
    await waitFor(() => expect(mockApi.put).toHaveBeenCalled());
    expect((mockApi.put.mock.calls[0][1] as JitConfig)).toMatchObject({ enabled: false, branch: 'release', repoUrl: 'git@github.com:o/r.git' });
  });

  // ── what the compose file must contain ────────────────────────────────────

  it('tells a beginner exactly what each image line in their compose file must say, with a copy button', async () => {
    setup({ config: ON });
    expect(await screen.findByText('${API_TAG:-shop/api:latest}')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Copy the image line for api/i }));
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith('image: ${API_TAG:-shop/api:latest}');
  });

  it('shows what is still missing, as reported by the server', async () => {
    setup({ config: ON, problems: ['Choose the server this project deploys to (its address, user, SSH key and folder).'] });
    expect(await screen.findByText(/Choose the server this project deploys to/)).toBeInTheDocument();
  });

  // ── deploying ─────────────────────────────────────────────────────────────

  const deployButton = () => screen.getByRole('button', { name: 'Deploy now' });

  it('cannot deploy while it is off or there are unsaved changes', async () => {
    setup({ config: ON });
    await screen.findByLabelText('Git address');
    expect(deployButton()).toBeEnabled();
    fireEvent.change(screen.getByLabelText('Branch'), { target: { value: 'dev' } });
    expect(deployButton()).toBeDisabled();
    expect(screen.getByText(/Save your settings before deploying/i)).toBeInTheDocument();
  });

  it('deploys, streams the log live, and shows the result', async () => {
    setup({ config: ON });
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b1', status: 'Deploying', message: 'Deploy started' } as never);

    fireEvent.click(deployButton());

    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/deploy', { force: false, commit: null, allowWithoutRollback: false });
    expect(FakeEventSource.instances[0].url).toBe('/api/builds/b1/stream');

    FakeEventSource.instances[0].emit('LogLine', { line: '[deploy] Building api, web' });
    expect(await screen.findByText('[deploy] Building api, web')).toBeInTheDocument();
    expect(deployButton()).toBeDisabled();   // one deploy at a time

    FakeEventSource.instances[0].emit('DeployCompleted', { status: 'Deployed' });
    expect(await screen.findByText(/Deployed/)).toBeInTheDocument();
    await waitFor(() => expect(deployButton()).toBeEnabled());
    expect(FakeEventSource.instances[0].closed).toBe(true);
    expect(mockApi.get.mock.calls.filter(c => c[0] === '/api/jit/p1/releases').length).toBeGreaterThanOrEqual(2);   // refreshed
  });

  it('shows the log in the shared log viewer: follows the output, can be paused, filtered, copied and downloaded', async () => {
    (navigator as unknown as { clipboard: unknown }).clipboard = { writeText: jest.fn().mockResolvedValue(undefined) };
    setup({ config: ON });
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b1' } as never);
    fireEvent.click(deployButton());
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));

    FakeEventSource.instances[0].emit('LogLine', { line: '[deploy] Building api' });
    FakeEventSource.instances[0].emit('LogLine', { line: 'fatal: something broke' });
    const log = await screen.findByRole('log', { name: 'Deploy log' });
    expect(within(log).getByText('[deploy] Building api')).toBeInTheDocument();
    expect(within(log).getByText(/streaming/)).toBeInTheDocument();

    expect(within(log).getByRole('button', { name: 'Pause auto-scroll' })).toBeInTheDocument();
    expect(within(log).getByRole('button', { name: 'Download log' })).toBeInTheDocument();
    expect(within(log).getByRole('button', { name: 'Errors only' })).toBeInTheDocument();
    fireEvent.click(within(log).getByRole('button', { name: 'Copy all' }));
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith('[deploy] Building api\nfatal: something broke'));

    fireEvent.change(within(log).getByLabelText('Filter log'), { target: { value: 'fatal' } });
    expect(within(log).queryByText('[deploy] Building api')).not.toBeInTheDocument();
    expect(within(log).getByText('fatal: something broke')).toBeInTheDocument();
  });

  it('shows why a deploy failed, in the words the server used', async () => {
    setup({ config: ON, build: { status: 'DeployFailed', errorSummary: 'Rolled back: release r1-1111111 is running again.' } });
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b1' } as never);
    fireEvent.click(deployButton());
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));

    FakeEventSource.instances[0].emit('DeployCompleted', { status: 'DeployFailed' });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Rolled back: release r1-1111111 is running again.');
  });

  it('can force a deploy when nothing has changed, or deploy one exact commit', async () => {
    setup({ config: ON });
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b1' } as never);

    fireEvent.click(screen.getByRole('button', { name: /advanced settings/i }));
    fireEvent.click(screen.getByLabelText('Deploy even if nothing has changed'));
    fireEvent.change(screen.getByLabelText('Deploy this exact commit (optional)'), { target: { value: 'ab8b4a9' } });
    fireEvent.click(deployButton());

    await waitFor(() => expect(mockApi.post).toHaveBeenCalled());
    expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/deploy', { force: true, commit: 'ab8b4a9', allowWithoutRollback: false });
  });

  it('shows the reason when a deploy cannot even start, and streams nothing', async () => {
    setup({ config: ON });
    await screen.findByLabelText('Git address');
    mockApi.post.mockRejectedValueOnce({ message: 'Choose the server this project deploys to.' });
    fireEvent.click(deployButton());
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Choose the server this project deploys to.'));
    expect(FakeEventSource.instances).toHaveLength(0);
    expect(deployButton()).toBeEnabled();
  });

  it('says so when the live connection drops', async () => {
    setup({ config: ON });
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b1' } as never);
    fireEvent.click(deployButton());
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));

    const es = FakeEventSource.instances[0];
    es.readyState = 2;
    act(() => { es.onerror?.(); });

    expect(await screen.findByRole('alert')).toHaveTextContent(/Lost the live connection/i);
  });

  it('closes the live stream when the card goes away', async () => {
    const { unmount } = setup({ config: ON });
    await screen.findByLabelText('Git address');
    mockApi.post.mockResolvedValueOnce({ buildId: 'b1' } as never);
    fireEvent.click(deployButton());
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    unmount();
    expect(FakeEventSource.instances[0].closed).toBe(true);
  });

  // ── releases and rolling back ─────────────────────────────────────────────

  it('lists the releases kept on the server, newest first, marking the one running', async () => {
    setup({ config: ON });
    const list = await screen.findByRole('list', { name: 'Releases kept on the server' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('r2-2222222');
    expect(items[0]).toHaveTextContent('2222222');          // short commit
    expect(items[0]).toHaveTextContent(/running now/i);
    expect(items[1]).toHaveTextContent(/previous/i);
    expect(within(items[0]).queryByRole('button', { name: /Roll back/ })).not.toBeInTheDocument();
    expect(within(items[1]).getByRole('button', { name: 'Roll back to r1-1111111' })).toBeInTheDocument();
  });

  it('asks before rolling back, then does it and streams the log', async () => {
    setup({ config: ON });
    fireEvent.click(await screen.findByRole('button', { name: 'Roll back to r1-1111111' }));
    expect(screen.getByText(/Go back to release r1-1111111\?/)).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();

    mockApi.post.mockResolvedValueOnce({ buildId: 'b9' } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Yes, roll back' }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/rollback', { tag: 'r1-1111111' }));
    await waitFor(() => expect(FakeEventSource.instances[0].url).toBe('/api/builds/b9/stream'));
  });

  it('does nothing when the person changes their mind', async () => {
    setup({ config: ON });
    fireEvent.click(await screen.findByRole('button', { name: 'Roll back to r1-1111111' }));
    fireEvent.click(screen.getByRole('button', { name: 'No, keep it as it is' }));
    expect(mockApi.post).not.toHaveBeenCalled();
    expect(screen.queryByText(/Go back to release/)).not.toBeInTheDocument();
  });

  it('warns that a rollback restores the code but not the data', async () => {
    setup({ config: ON });
    fireEvent.click(await screen.findByRole('button', { name: 'Roll back to r1-1111111' }));
    expect(screen.getByText(/database|data/i)).toBeInTheDocument();
  });

  it('offers recovery when the server was left half-way, and runs it', async () => {
    setup({ config: ON, releases: releasesResp({ phase: 'RollbackFailed', needsAttention: true, attention: 'A rollback failed earlier. Run recover to retry it.' }) });
    expect(await screen.findByText(/A rollback failed earlier/)).toBeInTheDocument();
    mockApi.post.mockResolvedValueOnce({ buildId: 'b3' } as never);

    fireEvent.click(screen.getByRole('button', { name: 'Recover' }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/jit/p1/recover', {}));
    await waitFor(() => expect(FakeEventSource.instances[0].url).toBe('/api/builds/b3/stream'));
  });

  it('does not offer to deploy while the server needs attention', async () => {
    setup({ config: ON, releases: releasesResp({ phase: 'RollbackFailed', needsAttention: true, attention: 'Needs attention.' }) });
    await screen.findByText('Needs attention.');
    expect(deployButton()).toBeDisabled();
  });

  it('says when the server cannot be reached, and lets the person try again', async () => {
    let calls = 0;
    mockApi.get.mockImplementation(async (path: string) => {
      if (path.endsWith('/config')) return cfgResp(ON) as never;
      if (path.endsWith('/releases')) {
        calls++;
        return (calls === 1
          ? releasesResp({ error: 'Could not reach the server: no route to host', releases: [] })
          : releasesResp()) as never;
      }
      if (path.endsWith('/dependents')) return dependentsResp() as never;
      return undefined as never;
    });
    render(<JitDeployCard projectId="p1" />);

    expect(await screen.findByText(/no route to host/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('list', { name: 'Releases kept on the server' })).toBeInTheDocument();
  });

  // ── "also restart this one?" ──────────────────────────────────────────────

  it('asks, per case, whether a service that depends on a rebuilt one should be restarted too', async () => {
    setup({ config: ON });
    fireEvent.click(await screen.findByRole('button', { name: /advanced settings/i }));
    const toggle = await screen.findByLabelText('Restart proxy when web is replaced');
    expect(toggle).toBeChecked();
  });

  it('saves the answers with the rest of the settings', async () => {
    setup({ config: ON });
    fireEvent.click(await screen.findByRole('button', { name: /advanced settings/i }));
    fireEvent.click(await screen.findByLabelText('Restart proxy when web is replaced'));
    mockApi.put.mockResolvedValueOnce({ config: {} } as never);
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));
    await waitFor(() => expect(mockApi.put).toHaveBeenCalled());
    expect((mockApi.put.mock.calls[0][1] as JitConfig).dependentChoices).toEqual([{ service: 'web', dependent: 'proxy', restart: false }]);
  });

  it('shows a saved "leave it running" answer as such', async () => {
    setup({ config: { ...ON, dependentChoices: [{ service: 'web', dependent: 'proxy', restart: false }] }, dependents: dependentsResp({ pairs: [{ service: 'web', dependent: 'proxy', restart: false }] }) });
    fireEvent.click(await screen.findByRole('button', { name: /advanced settings/i }));
    expect(await screen.findByLabelText('Restart proxy when web is replaced')).not.toBeChecked();
  });

});
