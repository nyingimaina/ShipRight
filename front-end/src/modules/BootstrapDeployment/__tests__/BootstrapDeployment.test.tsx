import { render, screen, fireEvent, waitFor } from '@testing-library/react';
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
  deployBlockers: [],
  deployReady: true,
  steps: [{ id: 'save-server', title: 'Save the server', detail: 'ShipRight records where the app will run.' }],
  ok: true,
  ...over,
});

const fillAddress = () => {
  fireEvent.change(screen.getByLabelText(/public address/i), { target: { value: 'staging.example.com' } });
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

    fillAddress();
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));

    expect((await screen.findAllByText('Enter the server address.')).length).toBeGreaterThan(0);
    expect(mockApi.post).toHaveBeenCalledWith('/api/bootstrap/plan', expect.objectContaining({
      preset: 'ShipRightSelf',
      host: 'staging.example.com',
      username: '',
      remoteWorkingDir: '',
      domain: 'staging.shipright.example.com',
    }));
  });

  it('will not write anything before a clean preview', () => {
    render(<BootstrapDeployment cloud={false} />);

    expect(screen.getByRole('button', { name: 'Create deployment' })).toBeDisabled();
    expect(screen.getAllByText(/preview first/i).length).toBeGreaterThan(0);
  });

  it('accepts a public address alone and shows what can be added later', async () => {
    mockApi.post.mockResolvedValue(plan({
      deployReady: false,
      deployBlockers: ['Add the user ShipRight signs in as.'],
    }) as never);
    render(<BootstrapDeployment cloud={false} />);

    fireEvent.change(screen.getByLabelText(/public address/i), { target: { value: '203.0.113.10' } });
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));

    expect(await screen.findByText('Saved now, deploy later')).toBeInTheDocument();
    expect(screen.getByText('Add the user ShipRight signs in as.')).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenCalledWith('/api/bootstrap/plan', expect.objectContaining({
      host: '203.0.113.10',
      username: '',
      remoteWorkingDir: '',
      domain: '',
    }));
  });

  it('writes another app from scratch, not just this product', async () => {
    mockApi.post.mockResolvedValue(plan() as never);
    render(<BootstrapDeployment cloud={false} />);

    fireEvent.change(screen.getByLabelText(/what are you deploying/i), { target: { value: 'custom' } });
    fireEvent.change(screen.getByLabelText(/public address/i), { target: { value: '203.0.113.10' } });
    fireEvent.change(screen.getByLabelText(/project name/i), { target: { value: 'shop' } });
    fireEvent.change(screen.getByLabelText(/^repository/i), { target: { value: 'git@github.com:acme/shop.git' } });
    fireEvent.change(screen.getByLabelText('Service 1 image'), { target: { value: 'acme/shop-api' } });
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/bootstrap/plan', expect.objectContaining({
      preset: 'None',
      projectName: 'shop',
      repoUrl: 'git@github.com:acme/shop.git',
      services: expect.arrayContaining([expect.objectContaining({ name: 'app', image: 'acme/shop-api' })]),
    })));
  });

  it('reads a repository and fills the branch and compose file', async () => {
    mockApi.post
      .mockResolvedValueOnce({
        url: 'https://github.com/acme/shop',
        provider: 'github',
        owner: 'acme',
        name: 'shop',
        defaultBranch: 'main',
        private: false,
        repoId: 123,
        branches: ['main', 'dev'],
        composeFiles: ['deploy/docker-compose.yml'],
        dockerfiles: ['Dockerfile'],
        services: [{ name: 'api', image: 'acme/shop-api', dockerfile: 'Dockerfile', context: '.' }],
        suggestedComposeFile: 'deploy/docker-compose.yml',
        notes: [],
      } as never)
      .mockResolvedValueOnce(plan() as never);
    render(<BootstrapDeployment cloud={false} />);

    fireEvent.change(screen.getByLabelText(/what are you deploying/i), { target: { value: 'custom' } });
    fireEvent.change(screen.getByLabelText(/^repository/i), { target: { value: 'https://github.com/acme/shop' } });
    fireEvent.click(screen.getByRole('button', { name: 'Check repository' }));

    expect(await screen.findByText(/acme\/shop/)).toBeInTheDocument();
    expect(screen.getByLabelText(/^project name/i)).toHaveValue('shop');
    expect(screen.getByLabelText(/^compose project/i)).toHaveValue('shop');
    expect(screen.getByLabelText(/^branch/i)).toHaveValue('main');
    expect(screen.getByLabelText(/^compose file/i)).toHaveValue('deploy/docker-compose.yml');
    expect(screen.getByLabelText('Service 1 name')).toHaveValue('api');
    expect(screen.getByLabelText('Service 1 image')).toHaveValue('acme/shop-api');
    expect(mockApi.post).toHaveBeenCalledWith('/api/git/inspect', { url: 'https://github.com/acme/shop' });

    fireEvent.change(screen.getByLabelText(/path prefix/i), { target: { value: 'apps/shop' } });
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));

    await waitFor(() => expect(mockApi.post).toHaveBeenLastCalledWith('/api/bootstrap/plan', expect.objectContaining({
      providerRepoId: '123',
      pathPrefix: 'apps/shop',
    })));
  });

  it('fills the folder from the user and leaves it editable', () => {
    render(<BootstrapDeployment cloud={false} />);

    fireEvent.change(screen.getByLabelText(/^user/i), { target: { value: 'deploy' } });

    expect(screen.getByLabelText(/folder on the server/i)).toHaveValue('/home/deploy/shipright');
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
      } as never)
      .mockResolvedValueOnce({
        ok: true,
        problems: [],
        warnings: [],
        commands: [{ step: 'fetch', command: 'git fetch' }],
      } as never);
    render(<BootstrapDeployment cloud={false} />);

    fillAddress();
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

    fireEvent.click(screen.getByRole('button', { name: 'Check the server' }));

    expect(await screen.findByText('The server is ready')).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenLastCalledWith('/api/jit/shipright/dry-run', {});
  });

  it('says what happened and what to do when writing fails', async () => {
    mockApi.post
      .mockResolvedValueOnce(plan() as never)
      .mockRejectedValueOnce({ message: 'ShipRight could not write that deployment.' });
    render(<BootstrapDeployment cloud={false} />);

    fillAddress();
    fireEvent.click(screen.getByRole('button', { name: 'Preview' }));
    await screen.findByText('Ready to create');
    fireEvent.click(screen.getByRole('button', { name: 'Create deployment' }));

    expect(await screen.findByText(/could not write that deployment/i)).toBeInTheDocument();
    expect(screen.getByText(/preview again/i)).toBeInTheDocument();
  });
});
