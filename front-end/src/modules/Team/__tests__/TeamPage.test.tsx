import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import TeamPage from '../TeamPage';
import styles from '../TeamPage.module.css';
import { api } from '@/shared/ApiService';
import { useAuthStore } from '@/shared/auth';

jest.mock('@/shared/ApiService', () => ({
  api: { get: jest.fn(), post: jest.fn(), put: jest.fn() },
}));

jest.mock('@/shared/auth', () => ({
  useAuthStore: jest.fn(),
}));

const mockUseAuthStore = useAuthStore as unknown as jest.Mock;

jest.mock('react-hot-toast', () => ({ toast: { success: jest.fn() } }));

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
    default: ({ children, type, disabled, onClick }: any) =>
      react.createElement('button', { type, disabled, onClick }, children),
  };
});

jest.mock('jattac.libs.web.overflow-menu', () => {
  const react = jest.requireActual<typeof import('react')>('react');
  return {
    __esModule: true,
    default: ({ items }: any) =>
      react.createElement(
        react.Fragment,
        null,
        items.map((item: any, index: number) =>
          react.createElement('button', { key: index, onClick: item.onClick }, item.content)),
      ),
  };
});

jest.mock('jattac.libs.web.responsive-table', () => {
  const react = jest.requireActual<typeof import('react')>('react');
  return {
    __esModule: true,
    default: ({ data, columnDefinitions }: any) =>
      react.createElement(
        'table',
        { 'data-testid': 'team-table' },
        data.map((row: any) =>
          react.createElement(
            'tr',
            { key: row.id },
            columnDefinitions.map((column: any) =>
              react.createElement('td', { key: column.columnId }, column.cellRenderer(row))),
          )),
      ),
  };
});

const getMock = api.get as jest.Mock;
const postMock = api.post as jest.Mock;
const putMock = api.put as jest.Mock;

const member = { id: 'm1', email: 'a@b.c', name: 'Ali', role: 'Operator', isActive: true, created: '2026-01-01' };

function mockAdmin() {
  mockUseAuthStore.mockImplementation((selector: any) => selector({ user: { id: 'u1', role: 'Admin' } }));
}

beforeEach(() => {
  jest.clearAllMocks();
  mockAdmin();
  getMock.mockResolvedValue({ items: [member], total: 1, page: 1, pageSize: 20 });
  postMock.mockResolvedValue(undefined);
  putMock.mockResolvedValue(undefined);
});

describe('TeamPage', () => {
  it('loads and lists the team for an Admin', async () => {
    render(<TeamPage />);

    expect(await screen.findByText('Ali')).toBeInTheDocument();
    expect(getMock).toHaveBeenCalledWith('/api/team?page=1&pageSize=20');
    expect(screen.getByText('1–1 of 1 people')).toBeInTheDocument();
  });

  it('keeps a very long name or email inside the wrapping cell so the layout does not break', async () => {
    const longName = 'A'.repeat(200);
    const longEmail = `${'b'.repeat(190)}@example.com`;
    getMock.mockResolvedValue({ items: [{ ...member, name: longName, email: longEmail }], total: 1, page: 1, pageSize: 20 });

    render(<TeamPage />);

    const nameCell = await screen.findByText(longName);
    const emailCell = screen.getByText(longEmail);
    expect(nameCell).toHaveClass(styles.cellText);
    expect(emailCell).toHaveClass(styles.cellText);
  });

  it('hides the management UI from non-Admin roles', () => {
    mockUseAuthStore.mockImplementation((selector: any) => selector({ user: { id: 'u2', role: 'Viewer' } }));
    render(<TeamPage />);

    expect(screen.getByText(/You do not have permission to manage the team/i)).toBeInTheDocument();
    expect(getMock).not.toHaveBeenCalled();
  });

  it('adds a teammate with the chosen role and refreshes the list', async () => {
    render(<TeamPage />);
    await screen.findByText('Ali');

    fireEvent.change(screen.getByPlaceholderText('teammate@example.com'), { target: { value: 'b@c.d' } });
    fireEvent.change(screen.getByPlaceholderText('Teammate name'), { target: { value: 'Bo' } });
    fireEvent.change(screen.getByPlaceholderText('At least 8 characters'), { target: { value: 'temporary-pass' } });
    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'Viewer' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add teammate' }));

    await waitFor(() => expect(postMock).toHaveBeenCalledWith('/api/team', {
      email: 'b@c.d',
      name: 'Bo',
      temporaryPassword: 'temporary-pass',
      role: 'Viewer',
    }));
    expect(getMock).toHaveBeenCalledWith('/api/team?page=1&pageSize=20');
  });

  it('surfaces validation errors from the server next to the fields', async () => {
    postMock.mockRejectedValue({
      message: 'Validation failed',
      isError: true,
      errors: { email: ['Enter an email address.'] },
    });
    render(<TeamPage />);
    await screen.findByText('Ali');

    fireEvent.change(screen.getByPlaceholderText('teammate@example.com'), { target: { value: 'bad' } });
    fireEvent.change(screen.getByPlaceholderText('Teammate name'), { target: { value: 'Bo' } });
    fireEvent.change(screen.getByPlaceholderText('At least 8 characters'), { target: { value: 'temporary-pass' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add teammate' }));

    expect(await screen.findByText('Enter an email address.')).toBeInTheDocument();
  });

  it('changes a role through the overflow menu and reloads', async () => {
    render(<TeamPage />);
    const makeAdmin = await screen.findByRole('button', { name: 'Make Admin' });

    fireEvent.click(makeAdmin);

    await waitFor(() => expect(putMock).toHaveBeenCalledWith('/api/team/m1/role', { role: 'Admin', expectedRole: 'Operator' }));
  });

  it('deactivates a teammate only after confirmation', async () => {
    render(<TeamPage />);
    const switchOff = await screen.findByRole('button', { name: 'Switch off' });

    window.confirm = jest.fn(() => true);
    fireEvent.click(switchOff);

    await waitFor(() => expect(postMock).toHaveBeenCalledWith('/api/team/m1/deactivate', {}));
  });

  it('keeps the list after a role conflict and shows the guidance', async () => {
    putMock.mockRejectedValue({ message: 'This person was changed by someone else. Refresh the list and try again.', isError: true });
    render(<TeamPage />);
    const makeAdmin = await screen.findByRole('button', { name: 'Make Admin' });

    fireEvent.click(makeAdmin);

    expect(await screen.findByText(/This person was changed by someone else/i)).toBeInTheDocument();
  });
});