import { render, screen } from '@testing-library/react';
import MonitoringPage from '@/pages/monitoring';

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

const future = new Date(Date.now() + 45 * 86400000).toISOString();
const past90  = new Date(Date.now() - 90 * 86400000).toISOString();
const past3   = new Date(Date.now() - 3  * 86400000).toISOString();

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
  certs: [
    { domain: 'example.com', issuedUtc: past90, expiresUtc: future },
    { domain: 'api.example.com', issuedUtc: past3, expiresUtc: past3 },
  ],
  oomEvents: [
    { processName: 'java', pid: 1234, memoryMb: 512, occurredAt: 'Jun 14 at 10:23' },
  ],
  zombies: [
    { pid: 5678, processName: 'worker', parentName: 'nginx' },
  ],
  failedServices: ['nginx.service'],
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

  it('shows SSL certificate domain', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('example.com')).toBeInTheDocument();
  });

  it('shows expired SSL cert badge', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('api.example.com')).toBeInTheDocument();
  });

  it('shows failed service name', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('nginx.service')).toBeInTheDocument();
  });

  it('shows OOM event process name', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('java')).toBeInTheDocument();
  });

  it('shows zombie process name', async () => {
    render(<MonitoringPage />);
    expect(await screen.findByText('worker')).toBeInTheDocument();
  });
});
