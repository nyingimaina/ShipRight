import { render, screen, waitFor } from '@testing-library/react';
import AppShell from '../AppShell';
import { resetServerVersionCache } from '@/shared/serverInfo';

jest.mock('next/router', () => ({ useRouter: () => ({ push: jest.fn(), pathname: '/' }) }));
jest.mock('jattac.libs.web.zest-sidekick-menu', () => ({
  __esModule: true,
  default: ({ headerContent, footerContent }: { headerContent: React.ReactNode; footerContent: React.ReactNode }) => (
    <nav><header>{headerContent}</header><footer>{footerContent}</footer></nav>
  ),
}));
jest.mock('@/shared/theme/ThemeToggle', () => ({ __esModule: true, default: () => <span>theme</span> }));
jest.mock('@/shared/auth', () => ({
  authApi: { logout: jest.fn() },
  useAuthStore: (sel: (s: unknown) => unknown) => sel({ clearAuth: jest.fn(), user: { role: 'Admin' } }),
}));

const originalFetch = global.fetch;

const health = (body: unknown, ok = true) =>
  (global.fetch = jest.fn().mockResolvedValue({ ok, json: async () => body }) as unknown as typeof fetch);

beforeEach(() => resetServerVersionCache());
afterEach(() => { global.fetch = originalFetch; });

describe('AppShell version', () => {
  it('shows the version of the running ShipRight beside its name, at the top of the toolbar', async () => {
    health({ status: 'ok', serverVersion: '4.10.4', mode: 'desktop' });
    render(<AppShell><p>page</p></AppShell>);
    const header = screen.getByRole('banner');
    expect(await screen.findByText('v4.10.4')).toBeInTheDocument();
    expect(header).toContainElement(screen.getByText('v4.10.4'));
    expect(header).toHaveTextContent('ShipRight');
    expect(screen.getByText('page')).toBeInTheDocument();
  });

  it('does not show a stale number from the front-end package when the server cannot say', async () => {
    health({}, false);
    render(<AppShell><p>page</p></AppShell>);
    await waitFor(() => expect(global.fetch).toHaveBeenCalled());
    expect(screen.queryByText(/^v\d/)).not.toBeInTheDocument();
  });

  it('shows nothing until the server has answered', () => {
    global.fetch = jest.fn(() => new Promise(() => undefined)) as unknown as typeof fetch;
    render(<AppShell><p>page</p></AppShell>);
    expect(screen.queryByText(/^v\d/)).not.toBeInTheDocument();
  });

  it('asks the server only once however many times it is shown', async () => {
    health({ serverVersion: '4.10.4' });
    const first = render(<AppShell><p>a</p></AppShell>);
    await screen.findByText('v4.10.4');
    first.unmount();
    render(<AppShell><p>b</p></AppShell>);
    await screen.findByText('v4.10.4');
    expect(global.fetch).toHaveBeenCalledTimes(1);
  });
});
