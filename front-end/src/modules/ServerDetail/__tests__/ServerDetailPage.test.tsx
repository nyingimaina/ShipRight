import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import React from 'react';
import ServerDetailPage from '../../../pages/servers/detail';
import { api } from '@/shared/ApiService';

const router: any = { query: { id: 's1' }, isReady: true, replace: jest.fn(), push: jest.fn() };
jest.mock('next/router', () => ({ useRouter: () => router }));
jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('next/head', () => ({ __esModule: true, default: ({ children }: any) => <>{children}</> }));
jest.mock('next/link', () => ({ __esModule: true, default: ({ children, href }: any) => <a href={href}>{children}</a> }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { success: jest.fn(), error: jest.fn() } }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => <button onClick={onClick} disabled={disabled}>{children}</button>,
}));
jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => <input value={value} onChange={onChange} placeholder={placeholder} />,
}));
jest.mock('jattac.libs.web.zest-tabs', () => ({
  __esModule: true,
  default: ({ items, activeValue, onChange }: any) => (
    <div>{items.map((i: any) => (
      <button key={i.value} aria-pressed={i.value === activeValue} onClick={() => onChange(i.value)}>{i.label}</button>
    ))}</div>
  ),
}));
jest.mock('jattac.libs.web.overflow-menu', () => ({ __esModule: true, default: () => <div /> }));
jest.mock('jattac.libs.web.zest-responsive-layout', () => ({ ZestResponsiveLayout: ({ children }: any) => <div>{children}</div> }));
jest.mock('../../../modules/AppShell/AppShell', () => ({ __esModule: true, default: ({ children }: any) => <div>{children}</div> }));
jest.mock('../../../modules/ProjectConfig/SshKeySection', () => ({ __esModule: true, default: () => <div /> }));
jest.mock('../../../modules/FilePicker/BrowsePathField', () => ({ __esModule: true, default: () => <div /> }));
jest.mock('jattac-web-poller', () => ({
  __esModule: true,
  default: class { start() {} stop() {} },
}));

const mocked = api as jest.Mocked<typeof api>;
const SERVER = {
  id: 's1', name: 'Prod', host: '1.2.3.4', username: 'ec2-user', sshKeyPath: '/k.pem',
  remoteWorkingDir: '/srv/app', composeRepoUrl: 'https://x/y.git',
};

beforeEach(() => {
  jest.clearAllMocks();
  router.query = { id: 's1' };
  mocked.get.mockImplementation(async (url: string) => {
    if (url === '/api/servers/s1') return SERVER as any;
    return {} as any;
  });
});

describe('Server detail page', () => {
  it('Files tab lists the server files', async () => {
    mocked.get.mockImplementation(async (url: string) => {
      if (url === '/api/servers/s1') return SERVER as any;
      if (url.startsWith('/api/servers/s1/files'))
        return { path: '/srv/app', parent: '/srv', entries: [{ name: 'compose.yml', path: '/srv/app/compose.yml', isDirectory: false, size: 10, modified: null }] } as any;
      return {} as any;
    });
    router.query = { id: 's1', tab: 'files' };
    render(<ServerDetailPage />);
    expect(await screen.findByText('compose.yml')).toBeInTheDocument();
  });

  it('loads the server and shows Actions, Stats, Files, Settings tabs', async () => {
    render(<ServerDetailPage />);
    expect(await screen.findByRole('heading', { name: 'Prod' })).toBeInTheDocument();
    ['Actions', 'Stats', 'Files', 'Settings'].forEach(t => expect(screen.getByRole('button', { name: t })).toBeInTheDocument());
  });

  it('Actions tab is the default and offers Prepare and Deploy', async () => {
    render(<ServerDetailPage />);
    expect(await screen.findByRole('button', { name: 'Prepare server' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Deploy now' })).toBeInTheDocument();
  });

  it('Actions tab offers a download link for the log kept on the server', async () => {
    render(<ServerDetailPage />);
    const link = await screen.findByRole('link', { name: /Download server log/ });
    expect(link.getAttribute('href')).toBe('/api/servers/s1/checkpoint-log');
    expect(link.hasAttribute('download')).toBe(true);
  });

  it('Settings tab shows the form but not a second set of Prepare/Deploy buttons', async () => {
    render(<ServerDetailPage />);
    await screen.findByRole('heading', { name: 'Prod' });
    fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
    expect(await screen.findByText('Update Server')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Prepare server' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Deploy now' })).toBeNull();
  });

  it('honours ?tab=settings', async () => {
    router.query = { id: 's1', tab: 'settings' };
    render(<ServerDetailPage />);
    expect(await screen.findByText('Update Server')).toBeInTheDocument();
  });

  it('shows a busy indicator while the server loads', async () => {
    mocked.get.mockReturnValue(new Promise(() => {}) as any);
    render(<ServerDetailPage />);
    expect(await screen.findByRole('status')).toHaveTextContent(/Loading/);
  });

  it('shows a message when the server cannot be loaded', async () => {
    mocked.get.mockRejectedValue(new Error('boom'));
    render(<ServerDetailPage />);
    await waitFor(() => expect(screen.getByText(/could not be loaded/i)).toBeInTheDocument());
  });

  it('breadcrumb links back to Servers', async () => {
    render(<ServerDetailPage />);
    await screen.findByRole('heading', { name: 'Prod' });
    expect(screen.getByRole('link', { name: 'Servers' }).getAttribute('href')).toBe('/servers/');
  });
});
