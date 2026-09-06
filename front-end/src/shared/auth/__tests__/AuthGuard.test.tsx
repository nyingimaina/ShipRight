import { render, screen, waitFor } from '@testing-library/react';
import { useAuthStore } from '../AuthStore';
import AuthGuard from '../AuthGuard';

const mockReplace = jest.fn();
let mockPathname = '/projects';

jest.mock('next/router', () => ({
  useRouter: () => ({ replace: mockReplace, pathname: mockPathname }),
}));

const mockUseServerMode = jest.fn();
jest.mock('@/shared/serverInfo', () => ({
  useServerMode: () => mockUseServerMode(),
  loadServerMode: jest.fn(),
  resetServerModeCache: jest.fn(),
}));

function Child() {
  return <div data-testid="child">protected content</div>;
}

function renderPage() {
  return render(<AuthGuard><Child /></AuthGuard>);
}

beforeEach(() => {
  mockReplace.mockClear();
  mockPathname = '/projects';
  mockUseServerMode.mockReturnValue('cloud');
  localStorage.clear();
  useAuthStore.getState().clearAuth();
});

describe('AuthGuard', () => {
  it('redirects unauthenticated users away from protected pages', async () => {
    renderPage();
    await waitFor(() => expect(mockReplace).toHaveBeenCalledWith('/login'));
    expect(screen.queryByTestId('child')).not.toBeInTheDocument();
  });

  it('renders protected content for authenticated users', async () => {
    useAuthStore.getState().setAuth('access', 'refresh', {
      id: 'u1', email: 'a@b.c', name: 'A', companyId: 'c1', isAdmin: false,
    });
    renderPage();
    expect(await screen.findByTestId('child')).toBeInTheDocument();
    expect(mockReplace).not.toHaveBeenCalled();
  });

  it('renders public pages without requiring authentication', async () => {
    mockPathname = '/login';
    renderPage();
    expect(await screen.findByTestId('child')).toBeInTheDocument();
    expect(mockReplace).not.toHaveBeenCalled();
  });

  it('renders protected content without auth when running in desktop mode', async () => {
    mockUseServerMode.mockReturnValue('desktop');
    renderPage();
    expect(await screen.findByTestId('child')).toBeInTheDocument();
    expect(mockReplace).not.toHaveBeenCalled();
  });

  it('does not redirect to login on public pages in desktop mode', async () => {
    mockUseServerMode.mockReturnValue('desktop');
    mockPathname = '/login';
    renderPage();
    expect(await screen.findByTestId('child')).toBeInTheDocument();
    expect(mockReplace).not.toHaveBeenCalled();
  });
});
