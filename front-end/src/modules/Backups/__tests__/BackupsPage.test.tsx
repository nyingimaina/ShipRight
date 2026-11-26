import { fireEvent, render, screen } from '@testing-library/react';
import BackupsPage from '@/pages/backups';
import { api } from '@/shared/ApiService';

jest.mock('next/head', () => ({
  __esModule: true,
  default: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));
jest.mock('@/modules/AppShell/AppShell', () => ({
  __esModule: true,
  default: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled, zest }: any) => (
    <button
      type="button"
      data-semantic={zest?.semanticType}
      onClick={(e: any) => { void Promise.resolve(onClick(e)).catch(() => undefined); }}
      disabled={disabled}
    >
      {children}
    </button>
  ),
}));
jest.mock('react-hot-toast', () => ({
  __esModule: true,
  default: { success: jest.fn(), error: jest.fn() },
}));
jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), uploadForm: jest.fn() },
  apiUrl: (path: string) => path,
}));

const get = (api as any).get as jest.Mock;
const uploadForm = (api as any).uploadForm as jest.Mock;

const coverageProject = (over: object = {}) => ({
  projectId: 'p1',
  projectName: 'Shop',
  databaseName: 'shop',
  backupCount: 1,
  lastBackupAt: '2026-11-25T02:00:00Z',
  storedBytes: 512 * 1024,
  originalBytes: 4 * 1024 * 1024,
  keepCount: 10,
  keepDays: 0,
  cron: '0 2 * * *',
  timeZone: 'Africa/Nairobi',
  scheduleId: 's1',
  nextFireAt: '2026-11-26T02:00:00Z',
  lastFiredAt: '2026-11-25T02:00:00Z',
  ...over,
});

beforeEach(() => {
  jest.clearAllMocks();
  get.mockImplementation((url: string) => {
    if (url === '/api/backups/coverage') return Promise.resolve({ projects: [coverageProject()], capturedAt: '' });
    if (url.startsWith('/api/scheduler/history')) return Promise.resolve({ records: [], count: 0 });
    if (url === '/api/scheduler/inflight') return Promise.resolve({ inflight: null });
    if (url === '/api/scheduler/overflow') return Promise.resolve({ items: [], count: 0 });
    if (url === '/api/backups/storage') return Promise.resolve({
      root: '/home/ubuntu/shipright/backups', insideDataDirectory: false,
      availableBytes: 40 * 1024 * 1024 * 1024, usedBytes: 512 * 1024, count: 1,
      oldest: '2026-11-25T02:00:00Z', newest: '2026-11-25T02:00:00Z',
    });
    if (url === '/api/backups/recovery-key') return Promise.resolve({
      keys: [{ id: 'key-1', createdAt: '2026-11-25T02:00:00Z', active: true }],
    });
    return Promise.resolve(null);
  });
  uploadForm.mockResolvedValue({ token: 'abc123', fileName: 'shop.sql' });
});

describe('BackupsPage', () => {
  it('shows coverage per database project and links into that project’s own backups', async () => {
    render(<BackupsPage />);

    expect(await screen.findByText('Shop')).toBeInTheDocument();
    expect(screen.getByText('Protected')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Open backups/i }))
      .toHaveAttribute('href', '/projects?detail=p1&tab=database');
    expect(screen.queryByRole('button', { name: 'Schedule backup' })).toBeNull();
  });

  it('flags a project with no schedule and no backups', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/backups/coverage') return Promise.resolve({
        projects: [coverageProject({ backupCount: 0, lastBackupAt: null, cron: null, scheduleId: null })],
        capturedAt: '',
      });
      if (url.startsWith('/api/scheduler/history')) return Promise.resolve({ records: [], count: 0 });
      if (url === '/api/scheduler/inflight') return Promise.resolve({ inflight: null });
      if (url === '/api/scheduler/overflow') return Promise.resolve({ items: [], count: 0 });
      return Promise.resolve(null);
    });

    render(<BackupsPage />);

    expect(await screen.findByText('Unprotected')).toBeInTheDocument();
    expect(screen.getByText('1 unprotected')).toBeInTheDocument();
  });

  it('keeps history as a cross-project view, linking each run back to its project', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/backups/coverage') return Promise.resolve({ projects: [coverageProject()], capturedAt: '' });
      if (url.startsWith('/api/scheduler/history/summary')) return Promise.resolve(null);
      if (url.startsWith('/api/scheduler/history')) return Promise.resolve({
        records: [{
          id: 'h1', projectId: 'p1', projectName: 'Shop', databaseName: 'shop', status: 'completed',
          startedAt: '2026-11-25T02:00:00Z', completedAt: '2026-11-25T02:00:04Z', durationMs: 4200,
          errorMessage: null, backupSizeBytes: 512 * 1024, backupFileName: 'shop.sql.zst.enc',
          scheduleId: 's1', correlationId: 'c1',
        }],
        count: 1,
      });
      if (url === '/api/scheduler/inflight') return Promise.resolve({ inflight: null });
      if (url === '/api/scheduler/overflow') return Promise.resolve({ items: [], count: 0 });
      return Promise.resolve(null);
    });

    render(<BackupsPage />);

    const link = await screen.findByRole('link', { name: 'Shop' });
    expect(link).toHaveAttribute('href', '/projects?detail=p1&tab=database');
    expect(screen.getByText('shop.sql.zst.enc')).toBeInTheDocument();
  });

  it('offers the recovery key file and lists the active key', async () => {
    render(<BackupsPage />);
    await screen.findByText('Shop');

    fireEvent.click(screen.getByRole('button', { name: /Recovery keys/i }));

    expect(screen.getByRole('link', { name: 'Download recovery file' }))
      .toHaveAttribute('href', '/api/backups/recovery-key/export');
    expect(screen.getByText('active')).toBeInTheDocument();
  });

  it('offers a decrypt tool for backup files copied off the host', async () => {
    render(<BackupsPage />);
    await screen.findByText('Shop');

    const file = new File(['encrypted'], 'shop.sql.zst.enc', { type: 'application/octet-stream' });
    fireEvent.change(screen.getByLabelText('Backup file to decrypt'), { target: { files: [file] } });
    fireEvent.click(screen.getByRole('button', { name: 'Decrypt and download' }));

    expect(uploadForm).toHaveBeenCalledWith('/api/backups/decrypt', expect.any(FormData));
  });

  it('warns when backups sit inside the app data directory', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/backups/coverage') return Promise.resolve({ projects: [coverageProject()], capturedAt: '' });
      if (url.startsWith('/api/scheduler/history')) return Promise.resolve({ records: [], count: 0 });
      if (url === '/api/scheduler/inflight') return Promise.resolve({ inflight: null });
      if (url === '/api/scheduler/overflow') return Promise.resolve({ items: [], count: 0 });
      if (url === '/api/backups/storage') return Promise.resolve({
        root: '/root/.shipright/backups', insideDataDirectory: true,
        availableBytes: 1024, usedBytes: 0, count: 0, oldest: null, newest: null,
      });
      return Promise.resolve(null);
    });

    render(<BackupsPage />);

    expect(await screen.findByText(/would delete them/i)).toBeInTheDocument();
  });

  it('explains when no project has a database yet', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/backups/coverage') return Promise.resolve({ projects: [], capturedAt: '' });
      if (url.startsWith('/api/scheduler/history')) return Promise.resolve({ records: [], count: 0 });
      if (url === '/api/scheduler/inflight') return Promise.resolve({ inflight: null });
      if (url === '/api/scheduler/overflow') return Promise.resolve({ items: [], count: 0 });
      return Promise.resolve(null);
    });

    render(<BackupsPage />);

    expect(await screen.findByText('No database projects yet')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to Projects' })).toHaveAttribute('href', '/projects');
  });
});
