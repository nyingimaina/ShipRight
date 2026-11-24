import { render, screen } from '@testing-library/react';
import AppHeader from '../AppHeader';
import { useSystemStats } from '../useSystemStats';

let user: any = { name: 'Ada Lovelace', companyName: 'Analytical Engines', role: 'Operator' };
jest.mock('@/shared/auth', () => ({
  useAuthStore: (selector: (s: any) => unknown) => selector({ user }),
}));
jest.mock('../useSystemStats', () => ({ useSystemStats: jest.fn() }));

const statsMock = useSystemStats as jest.Mock;
const stats = (over: object = {}) => ({
  diskUsedPercent: 62, ramUsedPercent: 41, processCpuPercent: 7, processCount: 9,
  available: true, capturedAtUtc: '2026-01-01T00:00:00Z', ...over,
});

describe('AppHeader', () => {
  beforeEach(() => {
    user = { name: 'Ada Lovelace', companyName: 'Analytical Engines', role: 'Operator' };
    statsMock.mockReturnValue({ stats: stats() });
  });

  it('shows who is signed in, their company and their role, with live host percentages', () => {
    render(<AppHeader />);

    const header = screen.getByRole('banner');
    expect(header).toHaveTextContent('Ada Lovelace');
    expect(header).toHaveTextContent('Analytical Engines');
    expect(header).toHaveTextContent('Operator');
    expect(header).toHaveTextContent('Disk 62%');
    expect(header).toHaveTextContent('RAM 41%');
    expect(header).toHaveTextContent('Process 7%');
    expect(header).toHaveTextContent('9 processes');
  });

  it('warns when any capacity measure is close to its limit', () => {
    statsMock.mockReturnValue({ stats: stats({ diskUsedPercent: 91 }) });
    render(<AppHeader />);
    expect(screen.getByRole('status')).toHaveTextContent(/near capacity/i);
  });

  it('stays quiet at healthy levels and while the first reading is loading', () => {
    const { rerender } = render(<AppHeader />);
    expect(screen.queryByRole('status')).toBeNull();

    statsMock.mockReturnValue({ stats: null });
    rerender(<AppHeader />);
    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByRole('banner')).toHaveTextContent('—');
  });
});
