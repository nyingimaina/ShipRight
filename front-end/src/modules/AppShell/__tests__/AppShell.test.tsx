import { readFileSync } from 'fs';
import path from 'path';
import { render, screen, waitFor } from '@testing-library/react';
import AppShell from '../AppShell';
import styles from '../Styles/AppShell.module.css';
import { resetServerVersionCache } from '@/shared/serverInfo';

jest.mock('next/router', () => ({ useRouter: () => ({ push: jest.fn(), pathname: '/' }) }));
jest.mock('jattac.libs.web.zest-sidekick-menu', () => ({
  __esModule: true,
  default: ({ headerContent, footerContent, items = [], ...props }: any) => (
    <nav
      data-search={String(props.searchEnabled)}
      data-swipe={String(props.swipeEnabled)}
      data-surface={props.zest?.visualOptions?.surface}
      data-motion={props.zest?.motionOptions?.intensity}
    >
      {items.map((item: any) => <span key={item.id}>{item.label}</span>)}
      <header>{headerContent}</header>
      <footer>{footerContent}</footer>
    </nav>
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

  it('uses mobile-first page padding and a readable desktop width', () => {
    health({ serverVersion: '4.10.4' });
    const css = readFileSync(path.join(__dirname, '..', 'Styles', 'AppShell.module.css'), 'utf8');
    const content = css.match(/\.content\s*\{([^}]*)\}/)?.[1] ?? '';

    expect(content).toMatch(/padding:\s*calc\(72px \+ env\(safe-area-inset-top, 0px\)\)/);
    expect(content).toMatch(/max-width:\s*1320px/);

    const { container } = render(<AppShell><p>page</p></AppShell>);
    expect(container.querySelector('main')).toHaveClass(styles.content);
    expect(container.querySelector('main > div')).toHaveClass(styles.page);
  });

  it('uses the new Sidekick surface, playful motion and mobile swipe gesture', async () => {
    health({ serverVersion: '4.10.4' });
    render(<AppShell><p>page</p></AppShell>);

    const nav = await screen.findByRole('navigation');
    expect(nav).toHaveAttribute('data-search', 'false');
    expect(nav).toHaveAttribute('data-swipe', 'true');
    expect(nav).toHaveAttribute('data-surface', 'translucent');
    expect(nav).toHaveAttribute('data-motion', 'playful');
  });

  it('hides the Windows-only Utilities entry when the server runs in cloud mode', async () => {
    health({ mode: 'Cloud', serverVersion: '4.11.0' });
    render(<AppShell><p>page</p></AppShell>);

    await waitFor(() => expect(screen.queryByText('Utilities')).toBeNull());
    expect(screen.getByText('Backups')).toBeInTheDocument();
  });

  it('keeps Utilities in desktop mode', async () => {
    health({ mode: 'Desktop', serverVersion: '4.11.0' });
    render(<AppShell><p>page</p></AppShell>);

    expect(await screen.findByText('Utilities')).toBeInTheDocument();
  });
});
