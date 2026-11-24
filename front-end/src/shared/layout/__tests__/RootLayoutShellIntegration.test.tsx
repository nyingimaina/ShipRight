import { render, screen } from '@testing-library/react';
import RootLayout from '../RootLayout';

const mockRouter = { pathname: '/servers' };
jest.mock('next/router', () => ({ useRouter: () => mockRouter }));

jest.mock('jattac.libs.web.zest-sidekick-menu', () => ({
  __esModule: true,
  default: () => <nav data-testid="sidekick-menu">menu</nav>,
}));
jest.mock('@/shared/theme/ThemeToggle', () => ({ __esModule: true, default: () => <span>theme</span> }));
jest.mock('@/shared/auth', () => ({
  authApi: { logout: jest.fn() },
  useAuthStore: (sel: (s: unknown) => unknown) => sel({ clearAuth: jest.fn(), user: { role: 'Admin' } }),
}));
jest.mock('../AppHeader', () => ({ __esModule: true, default: () => <div data-testid="header" /> }));
jest.mock('../AppFooter', () => ({ __esModule: true, default: () => <div data-testid="footer" /> }));

beforeEach(() => {
  Object.defineProperty(global, 'fetch', {
    value: jest.fn().mockResolvedValue({ ok: true, json: async () => ({ webVersion: '4.10.25' }) }),
    writable: true,
  });
});

describe('RootLayout with the real AppShell', () => {
  it('still renders the sidekick hamburger menu around signed-in pages', () => {
    render(
      <RootLayout>
        <p data-testid="page">page</p>
      </RootLayout>,
    );

    expect(screen.getByTestId('sidekick-menu')).toBeInTheDocument();
    expect(screen.getByTestId('page')).toBeInTheDocument();
  });
});
