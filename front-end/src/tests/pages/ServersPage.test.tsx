import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import React from 'react';
import ServersPage from '../../pages/servers';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn(), delete: jest.fn() },
  sseUrl: (p: string) => p,
}));

jest.mock('next/head', () => ({ __esModule: true, default: ({ children }: any) => <>{children}</> }));
jest.mock('next/link', () => ({ __esModule: true, default: ({ children }: any) => <a>{children}</a> }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { success: jest.fn(), error: jest.fn() } }));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => <button onClick={onClick} disabled={disabled}>{children}</button>,
}));
jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder }: any) => <input value={value} onChange={onChange} placeholder={placeholder} />,
}));
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
jest.mock('../../modules/FilePicker/FilePicker', () => ({ __esModule: true, default: () => <div /> }));

const mocked = api as jest.Mocked<typeof api>;

const SERVER = {
  id: 's1', name: 'Prod', host: '1.2.3.4', username: 'ec2-user', sshKeyPath: '/k.pem',
  remoteWorkingDir: '/srv/app', rebuildScript: 'custom-rebuild.sh', deployMode: 'EnvCompose',
};

beforeEach(() => {
  jest.clearAllMocks();
  mocked.get.mockImplementation(async (url: string) => (url === '/api/servers' ? [SERVER] : []) as any);
  mocked.put.mockResolvedValue({} as any);
});

const openEdit = async () => {
  fireEvent.click(await screen.findByText('Edit'));
  await screen.findByText('Update Server');
};

describe('Servers page form', () => {
  it('no longer shows Deploy Mode or Rebuild Script', async () => {
    render(<ServersPage />);
    await openEdit();
    expect(screen.queryByText('Deploy Mode')).toBeNull();
    expect(screen.queryByText('Rebuild Script')).toBeNull();
  });

  it('still shows the connection fields and the remote deploy section', async () => {
    render(<ServersPage />);
    await openEdit();
    expect(screen.getByText('SSH Key Path')).toBeTruthy();
    expect(screen.getByText('Remote Working Directory')).toBeTruthy();
    expect(screen.getByText('Remote deploy (EC2)')).toBeTruthy();
  });

  it('preserves the stored deploy mode and rebuild script when saving an existing server', async () => {
    render(<ServersPage />);
    await openEdit();

    fireEvent.click(screen.getByText('Update Server'));

    await waitFor(() => expect(mocked.put).toHaveBeenCalled());
    const [url, body] = mocked.put.mock.calls[0] as [string, any];
    expect(url).toBe('/api/servers/s1');
    expect(body.deployMode).toBe('EnvCompose');
    expect(body.rebuildScript).toBe('custom-rebuild.sh');
  });
});
