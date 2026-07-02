import { render, screen, act, fireEvent, waitFor } from '@testing-library/react';
import SshKeySection from '../SshKeySection';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: {
    get: jest.fn(),
    post: jest.fn(),
  },
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

const mockApi = api as jest.Mocked<typeof api>;

describe('SshKeySection', () => {
  beforeEach(() => jest.clearAllMocks());

  it('shows Generate button when no key exists', async () => {
    mockApi.get.mockResolvedValue({ exists: false });
    await act(async () => { render(<SshKeySection projectId="proj1" />); });
    expect(screen.getByText('Generate SSH Key')).toBeInTheDocument();
  });

  it('shows key generated state when key exists', async () => {
    mockApi.get.mockResolvedValue({ exists: true, publicKey: 'ssh-ed25519 AAAA test', managedSshKey: true });
    await act(async () => { render(<SshKeySection projectId="proj1" />); });
    expect(screen.getByText('● Key generated')).toBeInTheDocument();
    expect(screen.getByText('Authorize on Server')).toBeInTheDocument();
  });

  it('shows active indicator when key is managed', async () => {
    mockApi.get.mockResolvedValue({ exists: true, publicKey: 'ssh-ed25519 AAAA test', managedSshKey: true });
    await act(async () => { render(<SshKeySection projectId="proj1" />); });
    expect(screen.getByText('· Active for deployments')).toBeInTheDocument();
  });

  it('shows authorize form when Authorize on Server is clicked', async () => {
    mockApi.get.mockResolvedValue({ exists: true, publicKey: 'ssh-ed25519 AAAA test' });
    await act(async () => { render(<SshKeySection projectId="proj1" />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });
    expect(screen.getByPlaceholderText('Server password')).toBeInTheDocument();
  });

  it('calls generate endpoint when Generate SSH Key is clicked', async () => {
    mockApi.get.mockResolvedValue({ exists: false });
    mockApi.post.mockResolvedValue({});
    mockApi.get.mockResolvedValueOnce({ exists: false })
              .mockResolvedValueOnce({ exists: true, publicKey: 'ssh-ed25519 AAAA test' });

    await act(async () => { render(<SshKeySection projectId="proj1" />); });
    await act(async () => { fireEvent.click(screen.getByText('Generate SSH Key')); });

    expect(mockApi.post).toHaveBeenCalledWith('/api/projects/proj1/ssh-key/generate', {});
  });

  it('calls authorize endpoint with password when Authorize is submitted', async () => {
    mockApi.get.mockResolvedValue({ exists: true, publicKey: 'ssh-ed25519 AAAA test' });
    mockApi.post.mockResolvedValue({ message: 'authorized' });

    await act(async () => { render(<SshKeySection projectId="proj1" />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });

    const passwordInput = screen.getByPlaceholderText('Server password');
    act(() => { fireEvent.change(passwordInput, { target: { value: 'secret' } }); });
    await act(async () => { fireEvent.click(screen.getByText('Authorize')); });

    expect(mockApi.post).toHaveBeenCalledWith(
      '/api/projects/proj1/ssh-key/authorize',
      { password: 'secret', port: 22 }
    );
  });

  it('shows success notice after authorization', async () => {
    mockApi.get.mockResolvedValue({ exists: true, publicKey: 'ssh-ed25519 AAAA test' });
    mockApi.post.mockResolvedValue({ message: 'authorized' });

    await act(async () => { render(<SshKeySection projectId="proj1" />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });

    const passwordInput = screen.getByPlaceholderText('Server password');
    act(() => { fireEvent.change(passwordInput, { target: { value: 'secret' } }); });
    await act(async () => { fireEvent.click(screen.getByText('Authorize')); });

    expect(screen.getByText(/Key authorized/)).toBeInTheDocument();
  });

  it('shows error notice when authorization fails', async () => {
    mockApi.get.mockResolvedValue({ exists: true, publicKey: 'ssh-ed25519 AAAA test' });
    mockApi.post.mockRejectedValue(new Error('Connection refused'));

    await act(async () => { render(<SshKeySection projectId="proj1" />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });

    const passwordInput = screen.getByPlaceholderText('Server password');
    act(() => { fireEvent.change(passwordInput, { target: { value: 'wrong' } }); });
    await act(async () => { fireEvent.click(screen.getByText('Authorize')); });

    expect(screen.getByText(/Authorization failed/)).toBeInTheDocument();
  });
});
