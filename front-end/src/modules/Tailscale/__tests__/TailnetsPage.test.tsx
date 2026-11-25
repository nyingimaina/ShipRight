import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import TailnetsPage from '@/pages/tailnets';
import { api } from '@/shared/ApiService';

jest.mock('next/head', () => ({ __esModule: true, default: ({ children }: any) => <>{children}</> }));
jest.mock('@/modules/AppShell/AppShell', () => ({ __esModule: true, default: ({ children }: any) => <>{children}</> }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { success: jest.fn(), error: jest.fn() } }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));
jest.mock('jattac.libs.web.zest-textbox', () => ({
  __esModule: true,
  default: ({ value, onChange, placeholder, type, id }: any) => (
    <input id={id} type={type ?? 'text'} value={value} onChange={onChange} placeholder={placeholder} />
  ),
}));
jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn(), delete: jest.fn() } }));

const get = (api as any).get as jest.Mock;
const post = (api as any).post as jest.Mock;

beforeEach(() => {
  jest.clearAllMocks();
  get.mockResolvedValue([{
    id: 't1', name: 'Acme', tailnet: 'example.com', tags: 'tag:shipright',
    hasAuthKey: true, hasOAuthClient: false, online: true, status: 'online', socksPort: 43123,
    modifiedAt: '2026-01-01T00:00:00Z',
  }]);
  post.mockResolvedValue({});
});

describe('TailnetsPage', () => {
  it('lists a tenant network without exposing its secret', async () => {
    render(<TailnetsPage />);

    expect(await screen.findByText('Acme')).toBeInTheDocument();
    expect(screen.getByText(/SOCKS5 127.0.0.1:43123/)).toBeInTheDocument();
    expect(screen.getAllByText(/Auth key/).length).toBeGreaterThan(0);
    expect(screen.queryByText(/tskey-auth-secret/)).toBeNull();
  });

  it('saves a new network and explains the Google limitation', async () => {
    render(<TailnetsPage />);
    await screen.findByText('Acme');

    fireEvent.change(screen.getByPlaceholderText('Acme production'), { target: { value: 'Beta' } });
    fireEvent.change(screen.getByPlaceholderText('tskey-auth-…'), { target: { value: 'tskey-auth-secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save network' }));

    await waitFor(() => expect(post).toHaveBeenCalledWith('/api/tailnets', expect.objectContaining({
      name: 'Beta',
      authKey: 'tskey-auth-secret',
    })));
    expect(toast.success).toHaveBeenCalledWith('Tailscale network saved.');
    expect(screen.getByText(/Google sign-in itself is handled by Tailscale/i)).toBeInTheDocument();
  });

  it('gives copy-pastable ACL snippets instead of sending people to search', async () => {
    Object.assign(navigator, { clipboard: { writeText: jest.fn().mockResolvedValue(undefined) } });

    render(<TailnetsPage />);
    await screen.findByText('Acme');

    expect(screen.getByText('Copy the Tailscale rules')).toBeInTheDocument();
    expect(screen.getAllByText(/tcp:22/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/tag:shipright/).length).toBeGreaterThan(0);

    fireEvent.click(screen.getAllByRole('button', { name: 'Copy' })[0]);

    expect(navigator.clipboard.writeText).toHaveBeenCalledWith(expect.stringContaining('tag:shipright'));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Tag owner copied.'));
  });

  it('explains where each value comes from and links straight into the Tailscale admin console', async () => {
    render(<TailnetsPage />);
    await screen.findByText('Acme');

    fireEvent.click(screen.getByRole('button', { name: /Help: New to Tailscale/i }));

    expect(screen.getAllByText(/Generate auth key/i).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/tagOwners/i).length).toBeGreaterThan(0);

    const links = screen.getAllByRole('link').map(link => ({
      href: link.getAttribute('href'),
      target: link.getAttribute('target'),
    }));

    for (const href of [
      'https://console.tailscale.com/admin/settings/keys',
      'https://console.tailscale.com/admin/settings/trust-credentials',
      'https://console.tailscale.com/admin/acls',
    ]) {
      const match = links.find(link => link.href === href);
      expect(match).toBeDefined();
      expect(match?.target).toBe('_blank');
    }
  });
});
