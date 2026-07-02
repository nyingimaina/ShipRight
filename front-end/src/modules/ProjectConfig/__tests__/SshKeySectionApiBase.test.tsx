import { render, screen, act, fireEvent } from '@testing-library/react';
import SshKeySection from '../SshKeySection';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn() },
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

const mockApi = api as jest.Mocked<typeof api>;

describe('SshKeySection — apiBase prop', () => {
  beforeEach(() => jest.clearAllMocks());

  it('uses apiBase for status check when provided', async () => {
    mockApi.get.mockResolvedValue({ exists: false });
    await act(async () => {
      render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" />);
    });
    expect(mockApi.get).toHaveBeenCalledWith('/api/servers/srv1/ssh-key/status');
  });

  it('uses apiBase for generate endpoint when provided', async () => {
    mockApi.get.mockResolvedValue({ exists: false });
    mockApi.post.mockResolvedValue({});
    await act(async () => {
      render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" />);
    });
    await act(async () => { fireEvent.click(screen.getByText('Generate SSH Key')); });
    expect(mockApi.post).toHaveBeenCalledWith('/api/servers/srv1/ssh-key/generate', {});
  });

  it('uses apiBase for authorize endpoint when provided', async () => {
    mockApi.get.mockResolvedValue({ exists: true, publicKey: 'ssh-ed25519 AAAA test' });
    mockApi.post.mockResolvedValue({ message: 'ok' });
    await act(async () => {
      render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" />);
    });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });
    fireEvent.change(screen.getByPlaceholderText('Server password'), { target: { value: 'pw' } });
    await act(async () => { fireEvent.click(screen.getByText('Authorize')); });
    expect(mockApi.post).toHaveBeenCalledWith(
      '/api/servers/srv1/ssh-key/authorize',
      { password: 'pw', port: 22 }
    );
  });

  it('still uses projectId-derived URL when projectId is provided and apiBase is absent', async () => {
    mockApi.get.mockResolvedValue({ exists: false });
    await act(async () => {
      render(<SshKeySection projectId="p99" />);
    });
    expect(mockApi.get).toHaveBeenCalledWith('/api/projects/p99/ssh-key/status');
  });
});
