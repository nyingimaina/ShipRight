import { render, screen, act, fireEvent, waitFor, within } from '@testing-library/react';
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
    <span role="button" aria-disabled={disabled} onClick={disabled ? undefined : onClick}>{children}</span>
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

describe('SshKeySection — server authorize modes and the EC2 tip', () => {
  const serverStatus = { exists: true, publicKey: 'ssh-ed25519 AAAA test', managedSshKey: true, authorized: false };

  const withKeyFiles = () =>
    (mockApi.get as jest.Mock).mockImplementation((url: string) =>
      String(url).includes('/key-files')
        ? Promise.resolve({ items: [{ id: 'kf1', name: 'ec2.pem' }] })
        : Promise.resolve(serverStatus));

  beforeEach(() => jest.clearAllMocks());

  it('R2 offers a password or a saved key file on server settings', async () => {
    withKeyFiles();
    await act(async () => { render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" allowSavedKeyFile />); });

    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });

    expect(screen.getByText('Use server password')).toBeInTheDocument();
    expect(screen.getByText('Use a saved key file')).toBeInTheDocument();
  });

  it('R2 authorizes with a saved key file and sends the chosen id', async () => {
    withKeyFiles();
    mockApi.post.mockResolvedValue({ message: 'ok' });
    await act(async () => { render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" allowSavedKeyFile />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });

    fireEvent.change(screen.getByLabelText('Authorize with'), { target: { value: 'keyfile' } });
    const picker = await screen.findByLabelText('Saved key file');
    fireEvent.change(picker, { target: { value: 'kf1' } });
    await act(async () => { fireEvent.click(screen.getByText('Authorize')); });

    expect(mockApi.post).toHaveBeenCalledWith(
      '/api/servers/srv1/ssh-key/authorize',
      expect.objectContaining({ mode: 'keyfile', keyFileResourceId: 'kf1', port: 22 }),
    );
  });

  it('R2 shows a field error without submitting when no password is given', async () => {
    mockApi.get.mockResolvedValue(serverStatus);
    await act(async () => { render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" allowSavedKeyFile />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });

    await act(async () => { fireEvent.click(screen.getByText('Authorize')); });

    expect(screen.getByText('Enter the server password, or choose a saved key file.')).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });

  it('R2 shows a field error without submitting when no key file is chosen', async () => {
    withKeyFiles();
    await act(async () => { render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" allowSavedKeyFile />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });
    fireEvent.change(screen.getByLabelText('Authorize with'), { target: { value: 'keyfile' } });

    await act(async () => { fireEvent.click(screen.getByText('Authorize')); });

    expect(screen.getByText('Choose a saved key file.')).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });

  it('R3 explains the EC2 case, names other providers as coming later and never asks for a password in the tip', async () => {
    withKeyFiles();
    await act(async () => { render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" allowSavedKeyFile />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });

    fireEvent.click(screen.getByLabelText('Help: Using an EC2 server?'));

    const tip = screen.getByRole('region');
    expect(within(tip).getByText(/created without a password/i)).toBeInTheDocument();
    expect(within(tip).getByText(/coming later/i)).toBeInTheDocument();
    expect(within(tip).getByText(/Resources then Key files/i)).toBeInTheDocument();
    expect(tip).not.toHaveTextContent(/enter (a|your) password/i);
  });

  it('R1 regenerating replaces the key and tells the user to authorize again', async () => {
    mockApi.get.mockResolvedValue({ exists: true, publicKey: 'ssh-ed25519 AAAA test', managedSshKey: true, authorized: true });
    mockApi.post.mockResolvedValue({});
    await act(async () => { render(<SshKeySection projectId="proj1" />); });

    await act(async () => { fireEvent.click(screen.getByText('Regenerate Key')); });

    expect(screen.getByText(/Authorize it on the server again/)).toBeInTheDocument();
  });

  it('R1 shows a clear error when key generation is not available', async () => {
    mockApi.get.mockResolvedValue({ exists: false });
    mockApi.post.mockRejectedValue({ message: 'ssh-keygen is not installed' });
    await act(async () => { render(<SshKeySection projectId="proj1" />); });

    await act(async () => { fireEvent.click(screen.getByText('Generate SSH Key')); });

    expect(screen.getByText(/Key generation failed: ssh-keygen is not installed/)).toBeInTheDocument();
  });

  it('R2 pressing Authorize twice quickly authorizes once', async () => {
    mockApi.get.mockResolvedValue(serverStatus);
    mockApi.post.mockReturnValue(new Promise(() => {}));
    await act(async () => { render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" allowSavedKeyFile />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });
    fireEvent.change(screen.getByPlaceholderText('Server password'), { target: { value: 'secret' } });

    const authorize = screen.getByText('Authorize');
    fireEvent.click(authorize);
    fireEvent.click(authorize);

    await waitFor(() => {
      const authorizes = mockApi.post.mock.calls.filter(call => String(call[0]).endsWith('/authorize'));
      expect(authorizes).toHaveLength(1);
    });
  });

  it('R2 gives the port and password fields visible labels in password mode', async () => {
    mockApi.get.mockResolvedValue(serverStatus);
    await act(async () => { render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" allowSavedKeyFile />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });

    expect(screen.getByLabelText('Port')).toBeInTheDocument();
    expect(screen.getByLabelText('Server password')).toBeInTheDocument();
  });

  it('R2 renders the port field in key-file mode and sends the chosen port', async () => {
    withKeyFiles();
    mockApi.post.mockResolvedValue({ message: 'ok' });
    await act(async () => { render(<SshKeySection apiBase="/api/servers/srv1/ssh-key" allowSavedKeyFile />); });
    act(() => { fireEvent.click(screen.getByText('Authorize on Server')); });
    fireEvent.change(screen.getByLabelText('Authorize with'), { target: { value: 'keyfile' } });

    const portInput = screen.getByLabelText('Port');
    expect(portInput).toBeInTheDocument();
    fireEvent.change(portInput, { target: { value: '2222' } });
    const picker = await screen.findByLabelText('Saved key file');
    fireEvent.change(picker, { target: { value: 'kf1' } });
    await act(async () => { fireEvent.click(screen.getByText('Authorize')); });

    expect(mockApi.post).toHaveBeenCalledWith(
      '/api/servers/srv1/ssh-key/authorize',
      expect.objectContaining({ mode: 'keyfile', keyFileResourceId: 'kf1', port: 2222 }),
    );
  });

  it('R2 does not offer Authorize before a managed key exists', async () => {
    mockApi.get.mockResolvedValue({ exists: false });
    await act(async () => { render(<SshKeySection projectId="proj1" />); });

    expect(screen.getByText('Generate SSH Key')).toBeInTheDocument();
    expect(screen.queryByText('Authorize on Server')).not.toBeInTheDocument();
  });
});
