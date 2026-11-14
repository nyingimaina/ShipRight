import { render, screen, fireEvent, act, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import ServerWizardLauncher, { requestConnectionFix } from '../ServerWizardLauncher';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), put: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

const mockApi = api as jest.Mocked<typeof api>;

beforeEach(() => {
  jest.clearAllMocks();
  mockApi.get.mockImplementation(async (path: string) => {
    if (path === '/api/servers/s9') return { id: 's9', name: 'Old box', host: 'old.example', username: 'ubuntu', remoteWorkingDir: '/srv' } as never;
    if (path.endsWith('/host-key')) return { sha256: 'SHA256:zzz', algorithm: 'ssh-ed25519', state: 'trusted', verifyCommand: 'x', message: 'ok' } as never;
    throw new Error('unexpected ' + path);
  });
});

describe('ServerWizardLauncher', () => {
  it('shows a guided setup button, and opens the wizard on a new server from it', () => {
    render(<ServerWizardLauncher />);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /guided setup/i }));
    expect(screen.getByRole('dialog', { name: 'Connect a server' })).toBeInTheDocument();
    expect(screen.getByText(/step 1 of 5/i)).toBeInTheDocument();
  });

  it('closes when the wizard is closed', () => {
    render(<ServerWizardLauncher />);
    fireEvent.click(screen.getByRole('button', { name: /guided setup/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('opens on an existing server when asked to fix its connection', async () => {
    render(<ServerWizardLauncher />);
    act(() => requestConnectionFix('s9'));
    expect(await screen.findByText('SHA256:zzz')).toBeInTheDocument();
    expect(mockApi.get).toHaveBeenCalledWith('/api/servers/s9/host-key');
  });

  it('stops listening when it is removed', () => {
    const { unmount } = render(<ServerWizardLauncher />);
    unmount();
    act(() => requestConnectionFix('s9'));
    expect(mockApi.get).not.toHaveBeenCalled();
  });

  it('tells the page to refresh when the server is connected', async () => {
    const onFinished = jest.fn();
    mockApi.get.mockImplementation(async (path: string) => {
      if (path === '/api/servers/s9') return { id: 's9', name: 'Old box', host: 'old.example', username: 'ubuntu', remoteWorkingDir: '/srv' } as never;
      if (path.endsWith('/host-key')) return { sha256: 'SHA256:zzz', algorithm: 'ssh-ed25519', state: 'trusted', verifyCommand: 'x', message: 'ok' } as never;
      if (path.endsWith('/ssh-key/status')) return { exists: true, publicKey: 'ssh-ed25519 AAAAexample', authorized: true } as never;
      throw new Error('unexpected ' + path);
    });
    mockApi.post.mockResolvedValue({ ready: true, summary: 'The server is ready.', items: [{ id: 'reach', title: 'r', status: 'pass', message: 'ok' }] } as never);
    render(<ServerWizardLauncher onFinished={onFinished} />);
    act(() => requestConnectionFix('s9'));
    fireEvent.click(await screen.findByRole('button', { name: 'Continue' }));
    await screen.findByText(/step 4 of 5/i);
    fireEvent.click(await screen.findByRole('button', { name: 'Next' }));
    await screen.findByText('The server is ready.');
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    await waitFor(() => expect(onFinished).toHaveBeenCalled());
    expect(toast.success).toHaveBeenCalled();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
