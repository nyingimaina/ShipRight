import { render, screen } from '@testing-library/react';
import RootLayout from '../RootLayout';
import { useInRootShell } from '../ShellContext';

const mockRouter = { pathname: '/projects' };
jest.mock('next/router', () => ({ useRouter: () => mockRouter }));

jest.mock('@/modules/AppShell/AppShell', () => {
  const { useInRootShell } = require('../ShellContext');
  // The root shell must sit outside the context; only the pages inside it are marked as nested.
  function MockShell({ children }: any) {
    return (
      <div data-testid="shell" data-context={useInRootShell() ? 'nested' : 'root'}>
        {children}
      </div>
    );
  }
  return { __esModule: true, default: MockShell };
});
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
    expect(screen.getByTestId('shell')).toHaveAttribute('data-context', 'root');
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
