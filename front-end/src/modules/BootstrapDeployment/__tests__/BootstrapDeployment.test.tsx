import { render, screen, fireEvent } from '@testing-library/react';
import BootstrapDeployment from '../BootstrapDeployment';
import { api } from '@/shared/ApiService';

const push = jest.fn();

jest.mock('@/shared/ApiService', () => ({ api: { post: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { success: jest.fn(), error: jest.fn() } }));
jest.mock('next/router', () => ({ useRouter: () => ({ push }) }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

const mockApi = api as jest.Mocked<typeof api>;

const plan = (over: Record<string, unknown> = {}) => ({
  server: {},
  project: {},
  jit: {},
  problems: [],
  warnings: [],
  steps: [{ id: 'save-server', title: 'Save the server', detail: 'ShipRight records where the app will run.' }],
  ok: true,
  ...over,
});

const fillTarget = () => {
  fireEvent.change(screen.getByLabelText(/server address/i), { target: { value: 'staging.example.com' } });
  fireEvent.change(screen.getByLabelText(/domain/i), { target: { value: 'staging.shipright.example.com' } });
};

describe('BootstrapDeployment', () => {
  beforeEach(() => jest.resetAllMocks());

  it('explains that bootstrap only runs in the desktop app', () => {
    render(<BootstrapDeployment cloud />);

    expect(screen.getByText(/bootstrap runs in the desktop app/i)).toBeInTheDocument();
  });

  it('previews the self preset and shows what still needs attention', async () => {
    mockApi.post.mockResolvedValue(plan({ ok: false, problems: ['Enter the server address.'] }) as never);
    render(<BootstrapDeployment cloud={false} />);

    fillTarget();
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));

    expect((await screen.findAllByText('Enter the server address.')).length).toBeGreaterThan(0);
    expect(mockApi.post).toHaveBeenCalledWith('/api/bootstrap/plan', expect.objectContaining({
      preset: 'ShipRightSelf',
      host: 'staging.example.com',
      username: 'ubuntu',
      remoteWorkingDir: '/home/ubuntu/shipright',
      domain: 'staging.shipright.example.com',
    }));
  });

  it('will not write anything before a clean preview', () => {
    render(<BootstrapDeployment cloud={false} />);

    expect(screen.getByRole('button', { name: 'Create deployment' })).toBeDisabled();
    expect(screen.getAllByText(/preview first/i).length).toBeGreaterThan(0);
  });

  it('creates the deployment after a clean preview and shows the key and the next step', async () => {
    mockApi.post
      .mockResolvedValueOnce(plan() as never)
      .mockResolvedValueOnce({
        plan: plan(),
        result: {
          ok: true,
          projectId: 'shipright',
          serverId: 'srv-1',
          publicKey: 'ssh-ed25519 AAAA test',
          problems: [],
          nextSteps: ['Run a dry run to check the server without changing it.'],
        },
      } as never);
    render(<BootstrapDeployment cloud={false} />);

    fillTarget();
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));
    await screen.findByText('Ready to create');
    fireEvent.click(screen.getByRole('button', { name: 'Create deployment' }));

    expect(await screen.findByText('Deployment written')).toBeInTheDocument();
    expect(screen.getByText('ssh-ed25519 AAAA test')).toBeInTheDocument();
    expect(screen.getByText(/check the server without changing it/i)).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenLastCalledWith('/api/bootstrap/apply', expect.objectContaining({
      preset: 'ShipRightSelf',
      host: 'staging.example.com',
    }));
  });

  it('says what happened and what to do when writing fails', async () => {
    mockApi.post
      .mockResolvedValueOnce(plan() as never)
      .mockRejectedValueOnce({ message: 'ShipRight could not write that deployment.' });
    render(<BootstrapDeployment cloud={false} />);

    fillTarget();
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));
    await screen.findByText('Ready to create');
    fireEvent.click(screen.getByRole('button', { name: 'Create deployment' }));

    expect(await screen.findByText(/could not write that deployment/i)).toBeInTheDocument();
    expect(screen.getByText(/preview again/i)).toBeInTheDocument();
  });
});
