import { render, screen, act, fireEvent, waitFor } from '@testing-library/react';
import FilePicker from '../FilePicker';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn() } }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => <button onClick={onClick} disabled={disabled}>{children}</button>,
}));

const mocked = api as jest.Mocked<typeof api>;
const SSH = { host: '1.2.3.4', user: 'ubuntu', keyPath: '/k.pem' };
const home = { path: '/home/ubuntu', parent: '/home', entries: [{ name: 'old', path: '/home/ubuntu/old', isDirectory: true }] };

beforeEach(() => {
  jest.clearAllMocks();
  localStorage.clear();
  mocked.get.mockResolvedValue(home as any);
});

const open = async () => {
  await act(async () => { render(<FilePicker dirsOnly sshConfig={SSH} onSelect={jest.fn()} />); });
  fireEvent.click(screen.getByRole('button', { name: 'New folder' }));
};

describe('FilePicker — creating a folder on the server', () => {
  it('offers New folder when browsing a server', async () => {
    await act(async () => { render(<FilePicker dirsOnly sshConfig={SSH} onSelect={jest.fn()} />); });
    expect(screen.getByRole('button', { name: 'New folder' })).toBeInTheDocument();
  });

  it('does not offer it when browsing this computer', async () => {
    await act(async () => { render(<FilePicker dirsOnly onSelect={jest.fn()} />); });
    expect(screen.queryByRole('button', { name: 'New folder' })).toBeNull();
  });

  it('creates the folder in the folder being viewed, then opens it', async () => {
    mocked.post.mockResolvedValue({ path: '/home/ubuntu/ship-right' } as any);
    await open();
    fireEvent.change(screen.getByLabelText('New folder name'), { target: { value: 'ship-right' } });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Create' })); });

    expect(mocked.post).toHaveBeenCalledWith('/api/fs/remote/mkdir',
      { host: '1.2.3.4', user: 'ubuntu', keyPath: '/k.pem', parent: '/home/ubuntu', name: 'ship-right' });
    await waitFor(() => expect(mocked.get).toHaveBeenLastCalledWith(expect.stringContaining('path=%2Fhome%2Fubuntu%2Fship-right')));
    expect(screen.queryByLabelText('New folder name')).toBeNull();
  });

  it('Enter creates the folder', async () => {
    mocked.post.mockResolvedValue({ path: '/home/ubuntu/x' } as any);
    await open();
    fireEvent.change(screen.getByLabelText('New folder name'), { target: { value: 'x' } });
    await act(async () => { fireEvent.keyDown(screen.getByLabelText('New folder name'), { key: 'Enter' }); });
    expect(mocked.post).toHaveBeenCalledTimes(1);
  });

  it('Create is disabled until a name is typed', async () => {
    await open();
    expect(screen.getByRole('button', { name: 'Create' })).toBeDisabled();
  });

  it('shows the server’s reason when it cannot create the folder, and keeps the box open', async () => {
    mocked.post.mockRejectedValue(new Error('"old" already exists in /home/ubuntu.'));
    await open();
    fireEvent.change(screen.getByLabelText('New folder name'), { target: { value: 'old' } });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Create' })); });
    expect(await screen.findByText(/already exists/)).toBeInTheDocument();
    expect(screen.getByLabelText('New folder name')).toBeInTheDocument();
  });

  it('cannot be double-submitted while creating', async () => {
    let resolve!: (v: any) => void;
    mocked.post.mockReturnValue(new Promise(r => { resolve = r; }) as any);
    await open();
    fireEvent.change(screen.getByLabelText('New folder name'), { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    fireEvent.keyDown(screen.getByLabelText('New folder name'), { key: 'Enter' });
    expect(mocked.post).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Create' })).toBeDisabled();
    await act(async () => { resolve({ path: '/home/ubuntu/x' }); });
  });

  it('Cancel closes the box without creating anything', async () => {
    await open();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByLabelText('New folder name')).toBeNull();
    expect(mocked.post).not.toHaveBeenCalled();
  });
});
