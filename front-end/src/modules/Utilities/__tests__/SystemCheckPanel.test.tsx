import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import SystemCheckPanel from '../SystemCheckPanel';
import { api } from '@/shared/ApiService';
import { ISystemCheckItem, ISystemCheckReport } from '@/shared/types/IDiskCleanup';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

const mockApi = api as jest.Mocked<typeof api>;

const item = (over: Partial<ISystemCheckItem>): ISystemCheckItem =>
  ({ id: 'wsl', title: 'WSL', status: 'Ok', detail: 'WSL is installed.', ...over });

const report = (items: ISystemCheckItem[]): ISystemCheckReport =>
  ({ items, healthy: items.every(i => i.status === 'Ok' || i.status === 'Skipped') });

const broken = report([
  item({ id: 'wsl' }),
  item({ id: 'docker', title: 'Docker installed', status: 'Problem', detail: 'Docker is not installed.', fixId: 'install-docker', fixLabel: 'Install Docker' }),
  item({ id: 'daemon', title: 'Docker running and usable', status: 'Skipped', detail: 'Checked once Docker is installed.' }),
]);

describe('SystemCheckPanel', () => {
  beforeEach(() => jest.resetAllMocks());

  it('says everything is fine when it is', async () => {
    mockApi.get.mockResolvedValue(report([item({}), item({ id: 'git', title: 'git' })]) as never);
    render(<SystemCheckPanel />);

    expect(await screen.findByText(/everything looks good/i)).toBeInTheDocument();
  });

  it('counts what needs attention and shows each check in plain words', async () => {
    mockApi.get.mockResolvedValue(broken as never);
    render(<SystemCheckPanel />);

    expect(await screen.findByText(/1 thing needs attention/i)).toBeInTheDocument();
    expect(screen.getByText('Docker is not installed.')).toBeInTheDocument();
    expect(screen.getByText('Checked once Docker is installed.')).toBeInTheDocument();
  });

  it('offers a repair button where ShipRight can fix it, and shows the new state afterwards', async () => {
    mockApi.get.mockResolvedValue(broken as never);
    mockApi.post.mockResolvedValue({
      succeeded: true, message: 'Install Docker: done.', output: '',
      report: report([item({}), item({ id: 'docker', title: 'Docker installed', detail: 'Docker is installed.' })]),
    } as never);
    render(<SystemCheckPanel />);

    fireEvent.click(await screen.findByRole('button', { name: 'Install Docker' }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/system/check/fix', { fixId: 'install-docker' }));
    expect(await screen.findByText(/everything looks good/i)).toBeInTheDocument();
    expect(toast.success).toHaveBeenCalled();
  });

  it('shows what happened when a repair fails', async () => {
    mockApi.get.mockResolvedValue(broken as never);
    mockApi.post.mockResolvedValue({
      succeeded: false, message: 'Install Docker did not work (exit code 100).', output: 'Could not resolve host', report: broken,
    } as never);
    render(<SystemCheckPanel />);

    fireEvent.click(await screen.findByRole('button', { name: 'Install Docker' }));

    expect(await screen.findByText(/did not work/)).toBeInTheDocument();
    expect(screen.getByText(/Could not resolve host/)).toBeInTheDocument();
  });

  it('warns that installing can take a while, while it works', async () => {
    mockApi.get.mockResolvedValue(broken as never);
    mockApi.post.mockReturnValue(new Promise(() => {}) as never);
    render(<SystemCheckPanel />);

    fireEvent.click(await screen.findByRole('button', { name: 'Install Docker' }));

    expect(await screen.findByText(/can take several minutes/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Install Docker' })).toBeDisabled();
  });

  it('gives a copy button for steps that only the person can do', async () => {
    const writeText = jest.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    mockApi.get.mockResolvedValue(report([
      item({ status: 'Problem', detail: 'WSL is not installed.', manualSteps: 'Open PowerShell as administrator. Run:  wsl --install --no-distribution' }),
    ]) as never);
    render(<SystemCheckPanel />);

    expect(await screen.findByText(/wsl --install --no-distribution/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /copy the steps/i }));

    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('wsl --install --no-distribution'));
  });

  it('turns the low-disk advice into a button that goes to the clean-up', async () => {
    const scrollIntoView = jest.fn();
    const target = document.createElement('div');
    target.id = 'disk-cleanup';
    target.scrollIntoView = scrollIntoView;
    document.body.appendChild(target);
    mockApi.get.mockResolvedValue(report([
      item({ id: 'disk', title: 'Free space', status: 'Problem', detail: '2.6 GB free.', manualSteps: 'Open Utilities and clean up.' }),
    ]) as never);
    render(<SystemCheckPanel />);

    fireEvent.click(await screen.findByRole('button', { name: /go to disk clean-up/i }));

    expect(scrollIntoView).toHaveBeenCalled();
    expect(screen.queryByText('Open Utilities and clean up.')).not.toBeInTheDocument();
    target.remove();
  });

  it('checks again on request', async () => {
    mockApi.get.mockResolvedValue(broken as never);
    render(<SystemCheckPanel />);
    await screen.findByText(/1 thing needs attention/i);

    fireEvent.click(screen.getByRole('button', { name: /check again/i }));

    await waitFor(() => expect(mockApi.get).toHaveBeenCalledTimes(2));
  });

  it('says so when the check itself cannot run', async () => {
    mockApi.get.mockRejectedValue(new Error('down'));
    render(<SystemCheckPanel />);

    expect(await screen.findByText(/could not run the check/i)).toBeInTheDocument();
  });
});
