import { render, screen } from '@testing-library/react';
import RootLayout from '../RootLayout';
import { useInRootShell } from '../ShellContext';

const mockRouter = { pathname: '/projects' };
jest.mock('next/router', () => ({ useRouter: () => mockRouter }));

jest.mock('@/modules/AppShell/AppShell', () => ({
  __esModule: true,
  default: ({ children }: any) => <div data-testid="shell">{children}</div>,
}));
jest.mock('../AppHeader', () => ({ __esModule: true, default: () => <div data-testid="header" /> }));
jest.mock('../AppFooter', () => ({ __esModule: true, default: () => <div data-testid="footer" /> }));

function Probe() {
  return <span data-testid="probe">{useInRootShell() ? 'nested' : 'standalone'}</span>;
}

describe('RootLayout', () => {
  beforeEach(() => { mockRouter.pathname = '/projects'; });

  it('wraps signed-in pages with the shell, header and footer', () => {
    render(<RootLayout><p data-testid="page">page</p><Probe /></RootLayout>);

    expect(screen.getByTestId('shell')).toContainElement(screen.getByTestId('page'));
    expect(screen.getByTestId('header')).toBeInTheDocument();
    expect(screen.getByTestId('footer')).toBeInTheDocument();
    expect(screen.getByTestId('probe')).toHaveTextContent('nested');
  });

  it.each(['/login', '/signup', '/forgot-password', '/reset-password'])(
    'leaves the public page %s unwrapped', (path) => {
      mockRouter.pathname = path;
      render(<RootLayout><p data-testid="page">page</p></RootLayout>);

      expect(screen.queryByTestId('shell')).toBeNull();
      expect(screen.queryByTestId('header')).toBeNull();
      expect(screen.queryByTestId('footer')).toBeNull();
      expect(screen.getByTestId('page')).toBeInTheDocument();
    },
  );

  it('also wraps the temporary-password page', () => {
    mockRouter.pathname = '/change-password';
    render(<RootLayout><p>page</p></RootLayout>);
    expect(screen.getByTestId('shell')).toBeInTheDocument();
  });
});
