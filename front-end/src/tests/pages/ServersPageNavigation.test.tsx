import { render, screen, fireEvent } from '@testing-library/react';
import React from 'react';
import ServersPage from '../../pages/servers';
import { api } from '@/shared/ApiService';

const push = jest.fn();
jest.mock('next/router', () => ({ __esModule: true, default: { push: (...a: any[]) => push(...a) } }));
jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn(), delete: jest.fn() },
  sseUrl: (p: string) => p,
}));
jest.mock('next/head', () => ({ __esModule: true, default: ({ children }: any) => <>{children}</> }));
jest.mock('next/link', () => ({ __esModule: true, default: ({ children, href }: any) => <a href={href}>{children}</a> }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { success: jest.fn(), error: jest.fn() } }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick }: any) => <button onClick={onClick}>{children}</button>,
}));
jest.mock('jattac.libs.web.zest-textbox', () => ({ __esModule: true, default: () => <input /> }));
jest.mock('jattac.libs.web.overflow-menu', () => ({
  __esModule: true,
  default: ({ items }: any) => (
    <div>{items.map((i: any) => <button key={i.content} onClick={i.onClick}>{i.content}</button>)}</div>
  ),
}));
jest.mock('jattac.libs.web.zest-responsive-layout', () => ({
  ZestResponsiveLayout: ({ children, sidePane }: any) => (
    <div>{children}{sidePane?.visible && <aside>{sidePane.content}</aside>}</div>
  ),
}));
jest.mock('../../modules/AppShell/AppShell', () => ({ __esModule: true, default: ({ children }: any) => <div>{children}</div> }));
jest.mock('../../modules/ProjectConfig/SshKeySection', () => ({ __esModule: true, default: () => <div /> }));
jest.mock('../../modules/FilePicker/BrowsePathField', () => ({ __esModule: true, default: () => <div /> }));

const mocked = api as jest.Mocked<typeof api>;
const SERVER = { id: 's1', name: 'Prod', host: '1.2.3.4', username: 'u', sshKeyPath: '/k.pem', remoteWorkingDir: '/srv' };

beforeEach(() => {
  jest.clearAllMocks();
  mocked.get.mockImplementation(async (url: string) => (url === '/api/servers' ? [SERVER] : []) as any);
});

describe('Servers page → detail navigation', () => {
  it('the server name links to its detail page', async () => {
    render(<ServersPage />);
    const link = await screen.findByRole('link', { name: 'Prod' });
    expect(link.getAttribute('href')).toBe('/servers/detail/?id=s1');
  });

  it('clicking the card body opens the detail page', async () => {
    render(<ServersPage />);
    fireEvent.click(await screen.findByText('u@1.2.3.4'));
    expect(push).toHaveBeenCalledWith('/servers/detail/?id=s1');
  });

  it('the overflow menu has Open, and Edit still opens the pane without navigating', async () => {
    render(<ServersPage />);
    fireEvent.click(await screen.findByText('Edit'));
    expect(await screen.findByText('Update Server')).toBeInTheDocument();
    expect(push).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Open'));
    expect(push).toHaveBeenCalledWith('/servers/detail/?id=s1');
  });
});
