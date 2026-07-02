import { render, screen, waitFor } from '@testing-library/react';
import MonitoringPage from '../monitoring';

jest.mock('next/head', () => ({ __esModule: true, default: ({ children }: any) => <>{children}</> }));
jest.mock('next/router', () => ({ useRouter: () => ({ push: jest.fn(), pathname: '/monitoring' }) }));
jest.mock('@/modules/AppShell/AppShell', () => ({
  __esModule: true,
  default: ({ children }: any) => <div data-testid="app-shell">{children}</div>,
}));
jest.mock('jattac-web-poller', () => ({
  __esModule: true,
  default: jest.fn().mockImplementation(({ func, onResult }) => ({
    start: () => func().then((r: any) => onResult(r)),
    stop: () => Promise.resolve(),
  })),
}));

const mockServer = { id: 'srv1', name: 'Production', host: '1.2.3.4', username: 'ubuntu', sshKeyPath: '/key.pem' };

const mockMetrics = {
  reachable: true,
  cpuPercent: 32,
  memUsedMb: 2048,
  memTotalMb: 4096,
  swapUsedMb: 0,
  swapTotalMb: 2048,
  load1m: 0.4,
  load5m: 0.3,
  load15m: 0.2,
  uptimeSeconds: 86400 * 4 + 3600 * 2,
  disks: [{ mount: '/', usedGb: 30, totalGb: 50 }],
  containers: [
    { name: 'myapp', image: 'nginx:alpine', status: 'Healthy', cpuPercent: 8.5, memUsedMb: 256, memLimitMb: 1024 },
    { name: 'mydb', image: 'mariadb:10.11', status: 'Stopped', cpuPercent: 0, memUsedMb: 0, memLimitMb: 0 },
  ],
};

global.fetch = jest.fn((url: string) => {
  if (url.includes('/api/servers') && !url.includes('/metrics')) {
    return Promise.resolve({ ok: true, json: () => Promise.resolve([mockServer]) } as Response);
  }
  if (url.includes('/metrics')) {
    return Promise.resolve({ ok: true, json: () => Promise.resolve(mockMetrics) } as Response);
  }
  return Promise.resolve({ ok: true, json: () => Promise.resolve({}) } as Response);
}) as jest.Mock;

describe('MonitoringPage', () => {
  beforeEach(() => jest.clearAllMocks());

  it('renders page heading', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('Monitoring')).toBeInTheDocument();
  });

  it('renders server name with metrics when API responds', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('Production')).toBeInTheDocument();
  });

  it('shows Processor load bar for healthy server', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('Processor load')).toBeInTheDocument();
  });

  it('shows Memory in use label', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('Memory in use')).toBeInTheDocument();
  });

  it('shows container rows with status', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('myapp')).toBeInTheDocument();
    expect(await screen.findByText('Healthy')).toBeInTheDocument();
  });

  it('shows Stopped badge for stopped containers', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('Stopped')).toBeInTheDocument();
  });
});
