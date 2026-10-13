import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import LoginPage from '@/pages/login';
import { useAuthStore, authApi } from '@/shared/auth';

jest.mock('next/head', () => ({ __esModule: true, default: ({ children }: any) => <>{children}</> }));

const mockReplace = jest.fn();
jest.mock('next/router', () => ({ useRouter: () => ({ replace: mockReplace, pathname: '/login' }) }));

jest.mock('react-hot-toast', () => ({ toast: { success: jest.fn(), error: jest.fn() } }));

jest.mock('jattac.libs.web.zest-textbox', () => {
  const react = jest.requireActual<typeof import('react')>('react');
  return {
    __esModule: true,
    default: ({ id, value, onChange, placeholder, type }: any) =>
      react.createElement('input', { id, value, onChange, placeholder, type }),
  };
});

jest.mock('jattac.libs.web.zest-button', () => {
  const react = jest.requireActual<typeof import('react')>('react');
  return {
    __esModule: true,
    default: ({ children, type, disabled, onClick, zest }: any) =>
      react.createElement(
        'button',
        { type, disabled, onClick, 'data-semantic': zest?.semanticType },
        children,
      ),
  };
});

jest.mock('@/shared/auth', () => {
  const actual = jest.requireActual('@/shared/auth');
  return {
    ...actual,
    authApi: {
      login: jest.fn(),
      signup: jest.fn(),
      providers: jest.fn(),
    },
  };
});

const mockUseServerMode = jest.fn();
jest.mock('@/shared/serverInfo', () => ({
  useServerMode: () => mockUseServerMode(),
  loadServerMode: jest.fn(),
  resetServerModeCache: jest.fn(),
}));

const loginMock = authApi.login as jest.Mock;
const providersMock = authApi.providers as jest.Mock;

const adminSession = {
  user: { id: 'u1', email: 'a@b.c', name: 'A', companyId: 'c1', role: 'Admin', mustChangePassword: false },
};

beforeEach(() => {
  mockReplace.mockClear();
  loginMock.mockReset();
  providersMock.mockReset();
  providersMock.mockResolvedValue({ google: false });
  mockUseServerMode.mockReset();
  mockUseServerMode.mockReturnValue('cloud');
  localStorage.clear();
  useAuthStore.getState().clearAuth();
});

describe('LoginPage', () => {
  it('renders email and password textboxes and a submit button with semantic type', () => {
    render(<LoginPage />);
    expect(screen.getByPlaceholderText('you@example.com')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('••••••••')).toBeInTheDocument();
    const button = screen.getByRole('button', { name: /sign in/i });
    expect(button).toHaveAttribute('type', 'submit');
    expect(button).toHaveAttribute('data-semantic', 'submit');
  });

  it('calls login, stores the session and redirects on success', async () => {
    loginMock.mockResolvedValue(adminSession);
    render(<LoginPage />);
    fireEvent.change(screen.getByPlaceholderText('you@example.com'), { target: { value: 'a@b.c' } });
    fireEvent.change(screen.getByPlaceholderText('••••••••'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(loginMock).toHaveBeenCalledWith('a@b.c', 'secret'));
    await waitFor(() => expect(useAuthStore.getState().isAuthenticated).toBe(true));
    expect(useAuthStore.getState().user?.role).toBe('Admin');
    expect(mockReplace).toHaveBeenCalledWith('/projects');
  });

  it('sends a person with a temporary password to change-password', async () => {
    loginMock.mockResolvedValue({
      user: { ...adminSession.user, mustChangePassword: true },
    });
    render(<LoginPage />);
    fireEvent.change(screen.getByPlaceholderText('you@example.com'), { target: { value: 'a@b.c' } });
    fireEvent.change(screen.getByPlaceholderText('••••••••'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(mockReplace).toHaveBeenCalledWith('/change-password'));
  });

  it('shows an error message when login fails', async () => {
    loginMock.mockRejectedValue({ message: 'Invalid credentials', isError: true });
    render(<LoginPage />);
    fireEvent.change(screen.getByPlaceholderText('you@example.com'), { target: { value: 'a@b.c' } });
    fireEvent.change(screen.getByPlaceholderText('••••••••'), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByText('Invalid credentials')).toBeInTheDocument();
    expect(useAuthStore.getState().isAuthenticated).toBe(false);
  });

  it('shows the Google button only when the server has Google configured', async () => {
    providersMock.mockResolvedValue({ google: true });
    render(<LoginPage />);
    expect(await screen.findByTestId('google-button')).toHaveAttribute('href', '/api/auth/google/start');
  });

  it('keeps email sign-in available and explains when Google is not configured', async () => {
    providersMock.mockResolvedValue({ google: false });
    render(<LoginPage />);
    expect(await screen.findByText(/Google sign-in is not set up/i)).toBeInTheDocument();
    expect(screen.queryByTestId('google-button')).not.toBeInTheDocument();
    expect(screen.getByPlaceholderText('you@example.com')).toBeInTheDocument();
  });

  it('skips login entirely and redirects to projects in desktop mode', () => {
    mockUseServerMode.mockReturnValue('desktop');
    render(<LoginPage />);

    expect(mockReplace).toHaveBeenCalledWith('/projects');
    expect(screen.queryByPlaceholderText('you@example.com')).not.toBeInTheDocument();
    expect(loginMock).not.toHaveBeenCalled();
  });
});