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
  api: { get: jest.fn(), post: jest.fn(), delete: jest.fn() },
}));

const get = (api as any).get as jest.Mock;
const post = (api as any).post as jest.Mock;

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
    return Promise.resolve(null);
  });
  post.mockResolvedValue({ scheduleId: 's1', message: 'Schedule registered.' });
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
});
