import { render, screen, waitFor } from '@testing-library/react';
import AuthGuard from '@/shared/auth/AuthGuard';
import { useAuthStore } from '@/shared/auth';
import { authApi } from '@/shared/auth/AuthApi';

jest.mock('@/shared/auth/AuthApi', () => ({
  authApi: {
    me: jest.fn(),
  },
}));

const mockRouter = { pathname: '/projects', replace: jest.fn() };
jest.mock('next/router', () => ({ useRouter: () => mockRouter }));

let mockMode = 'cloud';
jest.mock('@/shared/serverInfo', () => ({
  useServerMode: () => mockMode,
}));

const meMock = authApi.me as jest.Mock;

const sessionUser = { id: 'u1', email: 'a@b.c', name: 'A', companyId: 'c1', role: 'Admin', mustChangePassword: false };

function renderGuard(path: string) {
  mockRouter.pathname = path;
  return render(<AuthGuard><div data-testid="child">content</div></AuthGuard>);
}

beforeEach(() => {
  mockMode = 'cloud';
  mockRouter.replace.mockClear();
  meMock.mockReset();
  useAuthStore.getState().clearAuth();
});

describe('AuthGuard', () => {
  it('asks the server who is signed in and shows the page to an authenticated person', async () => {
    meMock.mockResolvedValue({ user: sessionUser });
    renderGuard('/projects');

    expect(await screen.findByTestId('child')).toBeInTheDocument();
    expect(meMock).toHaveBeenCalledTimes(1);
  });

  it('redirects to /login when the identity cookie is rejected on a protected page', async () => {
    meMock.mockRejectedValue({ message: 'Sign in required.', isError: true });
    renderGuard('/projects');

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/login'));
    expect(screen.queryByTestId('child')).not.toBeInTheDocument();
  });

  it('renders a public page without needing a session', async () => {
    meMock.mockRejectedValue({ message: 'Sign in required.', isError: true });
    renderGuard('/login');

    expect(await screen.findByTestId('child')).toBeInTheDocument();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('sends an authenticated person away from /login to /projects', async () => {
    meMock.mockResolvedValue({ user: sessionUser });
    renderGuard('/login');

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/projects'));
  });

  it('forces a person with a temporary password to change-password', async () => {
    meMock.mockResolvedValue({ user: { ...sessionUser, mustChangePassword: true } });
    renderGuard('/projects');

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/change-password'));
  });

  it('skips the identity check and renders children in desktop mode', () => {
    mockMode = 'desktop';
    renderGuard('/projects');

    expect(screen.getByTestId('child')).toBeInTheDocument();
    expect(meMock).not.toHaveBeenCalled();
  });
});