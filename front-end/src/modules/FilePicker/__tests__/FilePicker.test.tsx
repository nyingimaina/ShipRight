import { render, screen, act, fireEvent } from '@testing-library/react';
import FilePicker from '../FilePicker';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: {
    get: jest.fn(),
  },
}));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

const mockApi = api as jest.Mocked<typeof api>;

describe('FilePicker', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    localStorage.clear();
  });

  it('renders Quick Access, Drives, and WSL groups from shortcuts', async () => {
    mockApi.get
      .mockResolvedValueOnce({ path: 'C:\\Users\\nyingi', parent: 'C:\\Users', entries: [] })
      .mockResolvedValueOnce({
        commonFolders: [{ label: 'Home', path: 'C:\\Users\\nyingi' }],
        drives: [{ label: 'C:', path: 'C:\\' }],
        wsl: [{ label: 'Ubuntu', path: '\\\\wsl.localhost\\Ubuntu' }],
      });

    await act(async () => { render(<FilePicker onSelect={jest.fn()} />); });

    expect(screen.getByText('Quick Access')).toBeInTheDocument();
    expect(screen.getByText('Drives')).toBeInTheDocument();
    expect(screen.getByText('WSL')).toBeInTheDocument();
    expect(screen.getByText('Ubuntu')).toBeInTheDocument();
  });

  it('navigates into the WSL distro path when clicked', async () => {
    mockApi.get
      .mockResolvedValueOnce({ path: 'C:\\Users\\nyingi', parent: 'C:\\Users', entries: [] })
      .mockResolvedValueOnce({
        commonFolders: [],
        drives: [],
        wsl: [{ label: 'Ubuntu', path: '\\\\wsl.localhost\\Ubuntu' }],
      })
      .mockResolvedValueOnce({
        path: '\\\\wsl.localhost\\Ubuntu',
        parent: '\\\\wsl.localhost',
        entries: [{ name: 'home', path: '\\\\wsl.localhost\\Ubuntu\\home', isDirectory: true }],
      });

    await act(async () => { render(<FilePicker onSelect={jest.fn()} />); });
    await act(async () => { fireEvent.click(screen.getByText('Ubuntu')); });

    expect(mockApi.get).toHaveBeenCalledWith('/api/fs/list?path=%5C%5Cwsl.localhost%5CUbuntu');
    expect(await screen.findByText('home')).toBeInTheDocument();
  });

  it('calls onSelect with the selected file path', async () => {
    const onSelect = jest.fn();
    mockApi.get
      .mockResolvedValueOnce({
        path: 'C:\\Users\\nyingi',
        parent: 'C:\\Users',
        entries: [{ name: 'key.pem', path: 'C:\\Users\\nyingi\\key.pem', isDirectory: false }],
      })
      .mockResolvedValueOnce({ commonFolders: [], drives: [], wsl: [] });

    await act(async () => { render(<FilePicker onSelect={onSelect} />); });
    await act(async () => { fireEvent.click(screen.getByText('key.pem')); });
    await act(async () => { fireEvent.click(screen.getByText('Select')); });

    expect(onSelect).toHaveBeenCalledWith('C:\\Users\\nyingi\\key.pem');
  });

  it('restores the last directory from its per-instance key when storageKey is provided', async () => {
    localStorage.setItem('filePicker.lastLocalPath:sql', 'C:\\Users\\nyingi\\sql');
    mockApi.get
      .mockResolvedValueOnce({ path: 'C:\\Users\\nyingi\\sql', parent: 'C:\\Users', entries: [] })
      .mockResolvedValueOnce({ commonFolders: [], drives: [], wsl: [] });

    await act(async () => { render(<FilePicker onSelect={jest.fn()} storageKey="sql" />); });

    expect(mockApi.get).toHaveBeenCalledWith('/api/fs/list?path=C%3A%5CUsers%5Cnyingi%5Csql');
  });

  it('persists the current directory under the per-instance key when navigating', async () => {
    mockApi.get
      .mockResolvedValueOnce({
        path: 'C:\\Users\\nyingi',
        parent: 'C:\\Users',
        entries: [{ name: 'sql', path: 'C:\\Users\\nyingi\\sql', isDirectory: true }],
      })
      .mockResolvedValueOnce({ commonFolders: [], drives: [], wsl: [] })
      .mockResolvedValueOnce({ path: 'C:\\Users\\nyingi\\sql', parent: 'C:\\Users\\nyingi', entries: [] });

    await act(async () => { render(<FilePicker onSelect={jest.fn()} storageKey="sql" />); });
    await act(async () => { fireEvent.click(screen.getByText('sql')); });

    expect(localStorage.getItem('filePicker.lastLocalPath:sql')).toBe('C:\\Users\\nyingi\\sql');
  });

  it('falls back to the shared last-path key when no storageKey is provided', async () => {
    localStorage.setItem('filePicker.lastLocalPath', 'C:\\Users\\nyingi\\shared');
    mockApi.get
      .mockResolvedValueOnce({ path: 'C:\\Users\\nyingi\\shared', parent: 'C:\\Users', entries: [] })
      .mockResolvedValueOnce({ commonFolders: [], drives: [], wsl: [] });

    await act(async () => { render(<FilePicker onSelect={jest.fn()} />); });

    expect(mockApi.get).toHaveBeenCalledWith('/api/fs/list?path=C%3A%5CUsers%5Cnyingi%5Cshared');
  });
});
