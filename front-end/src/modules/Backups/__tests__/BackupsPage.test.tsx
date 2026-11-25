import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import toast from 'react-hot-toast';
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
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn(), delete: jest.fn(), uploadForm: jest.fn() },
  apiUrl: (path: string) => path,
}));

const get = (api as any).get as jest.Mock;
const post = (api as any).post as jest.Mock;
const put = (api as any).put as jest.Mock;
const uploadForm = (api as any).uploadForm as jest.Mock;

const backupFile = {
  fileName: 'shop_2026-11-25_02-00-00_schedule.sql.zst.enc',
  databaseName: 'shop',
  format: 'sql',
  compression: 'zstd',
  compressionLevel: 12,
  encryption: 'aes256gcm',
  keyId: 'key-1',
  originalBytes: 4 * 1024 * 1024,
  storedBytes: 512 * 1024,
  ratio: 8,
  sha256: 'abc123',
  createdAt: '2026-11-25T02:00:00Z',
  source: 'schedule',
  scheduleId: null,
  durationMs: 4200,
  rank: 1,
  projectedDeleteAt: '2026-12-05T02:00:00Z',
};

const project = (over: object = {}) => ({
  id: 'p1',
  name: 'Shop',
  timeZone: 'UTC',
  database: {
    provider: 'MariaDb',
    containerName: 'shop-db',
    databaseName: 'shop',
    rootUser: 'root',
    backupRetainCount: 10,
  },
  ...over,
});

beforeEach(() => {
  jest.clearAllMocks();
  get.mockImplementation((url: string) => {
    if (url === '/api/projects') return Promise.resolve([project()]);
    if (url === '/api/scheduler/schedules') return Promise.resolve({ jobs: [], count: 0, capturedAt: '' });
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
    if (url.startsWith('/api/projects/p1/db/backups')) return Promise.resolve({
      files: [backupFile], keepCount: 10, keepDays: 0, cadenceSeconds: 86400,
    });
    return Promise.resolve(null);
  });
  post.mockResolvedValue({ scheduleId: 's1', message: 'Schedule registered.' });
  put.mockResolvedValue({});
});

describe('BackupsPage', () => {
  it('gives an Admin a visible way to add a backup schedule', async () => {
    render(<BackupsPage />);

    expect(await screen.findByText('No backup schedules configured.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Schedule backup' })).toBeInTheDocument();
  });

  it('opens a friendly schedule form and posts the chosen project and daily cron', async () => {
    render(<BackupsPage />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Schedule backup' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Schedule backup' }));

    const dialog = await screen.findByRole('dialog', { name: 'Schedule a backup' });
    fireEvent.change(within(dialog).getByLabelText('Project'), { target: { value: 'p1' } });
    fireEvent.change(within(dialog).getByLabelText('Schedule'), { target: { value: 'daily-2' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Schedule backup' }));

    await waitFor(() => expect(post).toHaveBeenCalledWith('/api/scheduler/schedules', {
      projectId: 'p1',
      cronExpression: '0 2 * * *',
      timeZone: 'UTC',
    }));
    expect(toast.success).toHaveBeenCalledWith('Backup scheduled.');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Schedule a backup' })).toBeNull());
  });

  it('shows the real scheduling failure as an alert and keeps the form open', async () => {
    post.mockRejectedValueOnce(new Error('That cron expression is not valid.'));
    render(<BackupsPage />);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Schedule backup' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Schedule backup' }));

    const dialog = await screen.findByRole('dialog', { name: 'Schedule a backup' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Schedule backup' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('That cron expression is not valid.');
    expect(screen.getByRole('dialog', { name: 'Schedule a backup' })).toBeInTheDocument();
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('explains when no project has a database instead of offering a broken form', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/projects') return Promise.resolve([project({ database: undefined })]);
      if (url === '/api/scheduler/schedules') return Promise.resolve({ jobs: [], count: 0, capturedAt: '' });
      if (url.startsWith('/api/scheduler/history')) return Promise.resolve({ records: [], count: 0 });
      if (url === '/api/scheduler/inflight') return Promise.resolve({ inflight: null });
      if (url === '/api/scheduler/overflow') return Promise.resolve({ items: [], count: 0 });
      return Promise.resolve(null);
    });

    render(<BackupsPage />);

    expect(await screen.findByText(/Add a database to a project before scheduling backups/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Schedule backup' })).toBeDisabled();
  });

  it('expands a project to show sizes, retention and a download for each backup', async () => {
    render(<BackupsPage />);
    await screen.findByText('Shop');

    fireEvent.click(screen.getByRole('button', { name: 'Expand backups for Shop' }));

    expect(await screen.findByText('shop_2026-11-25_02-00-00_schedule.sql.zst.enc')).toBeInTheDocument();
    expect(screen.getAllByText(/4\.0 MB raw/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/512\.0 KB stored/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/#1 of 10/).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Download' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Download .zst' })).toBeInTheDocument();
  });

  it('saves the retention policy chosen on the project row', async () => {
    render(<BackupsPage />);
    await screen.findByText('Shop');
    fireEvent.click(screen.getByRole('button', { name: 'Expand backups for Shop' }));

    fireEvent.change(screen.getByLabelText('Backups to keep for Shop'), { target: { value: '4' } });
    fireEvent.change(screen.getByLabelText('Age limit in days for Shop'), { target: { value: '30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save policy' }));

    await waitFor(() => expect(put).toHaveBeenCalledWith('/api/projects/p1/db/backup-policy', {
      keepCount: 4,
      keepDays: 30,
      compressionLevel: 12,
      encrypt: true,
    }));
    expect(toast.success).toHaveBeenCalledWith('Backup policy saved.');
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
    uploadForm.mockResolvedValue({ token: 'abc123', fileName: 'shop.sql' });

    render(<BackupsPage />);
    await screen.findByText('Shop');

    const file = new File(['encrypted'], 'shop.sql.zst.enc', { type: 'application/octet-stream' });
    fireEvent.change(screen.getByLabelText('Backup file to decrypt'), { target: { files: [file] } });
    fireEvent.click(screen.getByRole('button', { name: 'Decrypt and download' }));

    await waitFor(() => expect(uploadForm).toHaveBeenCalledWith('/api/backups/decrypt', expect.any(FormData)));
  });

  it('warns when backups sit inside the app data directory', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/projects') return Promise.resolve([project()]);
      if (url === '/api/scheduler/schedules') return Promise.resolve({ jobs: [], count: 0, capturedAt: '' });
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
});
