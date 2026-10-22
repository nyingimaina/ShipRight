import { render, screen } from '@testing-library/react';
import React from 'react';
import ServerStatsTab from '../ServerStatsTab';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn() }, sseUrl: (p: string) => p }));
jest.mock('jattac-web-poller', () => ({ __esModule: true, default: class { start() {} stop() {} } }));
jest.mock('@/pages/monitoring', () => ({
  ServerCard: ({ metrics }: any) => <div>{metrics ? 'card' : 'card-without-metrics'}</div>,
}));

describe('ServerStatsTab', () => {
  it('shows a busy indicator, with a slow-link hint, until the first metrics arrive', () => {
    render(<ServerStatsTab server={{ id: 's1', name: 'Prod', host: 'h' } as any} />);
    expect(screen.getByRole('status')).toHaveTextContent(/Connecting/);
    expect(screen.getByText(/slow connections/i)).toBeInTheDocument();
  });
});
