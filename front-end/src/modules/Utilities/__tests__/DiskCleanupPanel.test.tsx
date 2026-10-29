import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import DiskCleanupPanel from '../DiskCleanupPanel';
import { api } from '@/shared/ApiService';
import { ICleanupPreview, IDiskJob, IDiskJobStep } from '@/shared/types/IDiskCleanup';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

const mockApi = api as jest.Mocked<typeof api>;
const GB = 1024 ** 3;
const NOW = new Date().toISOString();

const preview = (over: Partial<ICleanupPreview> = {}): ICleanupPreview => ({
  images: [
    { reference: 'shop/api:1.0.0', id: 'a', size: '500MB', sizeBytes: 500e6, age: '2 days ago', owner: 'ShipRight', selectedByDefault: true },
    { reference: 'postgres:16', id: 'c', size: '400MB', sizeBytes: 400e6, age: '3 weeks ago', owner: 'Other', selectedByDefault: false },
  ],
  inUseCount: 2, danglingCount: 3, selectedBytes: 500e6, diskUsage: 'Images 5 1 3.2GB', error: null, ...over,
});

const wsl = { vhdxFiles: [{ path: 'C:\\x\\ext4.vhdx', sizeBytes: 167 * GB }], distros: [], error: null };

const step = (over: Partial<IDiskJobStep>): IDiskJobStep =>
  ({ id: 'images', label: 'Removing 1 image', status: 'Pending', estimatedSeconds: 10, ...over });

const job = (over: Partial<IDiskJob> = {}): IDiskJob =>
  ({ id: 'j1', status: 'Running', startedAt: NOW, steps: [step({})], blockers: [], ...over });

let latest: IDiskJob | null;
let blockers: unknown[];

function serve(p: ICleanupPreview = preview()) {
  latest = null;
  blockers = [];
  mockApi.get.mockImplementation(async (url: string) => {
    if (url.includes('docker-cleanup')) return p as never;
    if (url.endsWith('/disk-job/latest')) return latest as never;
    if (url.includes('/disk-job/')) return latest as never;
    if (url.endsWith('/wsl-disk/blockers')) return blockers as never;
    return wsl as never;
  });
}

const succeeded = job({
  status: 'Succeeded', finishedAt: NOW,
  steps: [step({ status: 'Done', startedAt: NOW, finishedAt: NOW }), step({ id: 'shrink', label: 'Shrinking your Windows disk', status: 'Done', startedAt: NOW, finishedAt: NOW, estimatedSeconds: 600 })],
  cleanup: { items: [{ reference: 'shop/api:1.0.0', removed: true }], before: '', after: '' },
  shrink: { succeeded: true, files: [{ path: 'C:\\x\\ext4.vhdx', beforeBytes: 167 * GB, afterBytes: 30 * GB }], messages: [] },
});

const POLL = 5;

describe('DiskCleanupPanel', () => {
  beforeEach(() => { jest.resetAllMocks(); serve(); });

  it("pre-ticks ShipRight's own images and leaves everything else unticked", async () => {
    render(<DiskCleanupPanel pollMs={POLL} />);

    expect(await screen.findByLabelText('shop/api:1.0.0')).toBeChecked();
    expect(screen.getByLabelText('postgres:16')).not.toBeChecked();
    expect(screen.getByText(/not made by ShipRight/i)).toBeInTheDocument();
    expect(screen.getByText(/never touches your data/i)).toBeInTheDocument();
  });

  it('shows how big the Windows virtual disk is', async () => {
    render(<DiskCleanupPanel pollMs={POLL} />);

    expect(await screen.findByText(/167 GB/)).toBeInTheDocument();
  });

  it('starts a job with exactly what is ticked, without the shrink', async () => {
    mockApi.post.mockResolvedValue(job() as never);
    render(<DiskCleanupPanel pollMs={POLL} />);
    fireEvent.click(await screen.findByLabelText('postgres:16'));

    fireEvent.click(screen.getByRole('button', { name: /^clean up now$/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/system/disk-job', {
      images: ['shop/api:1.0.0', 'postgres:16'], pruneDangling: true, pruneBuildCache: true, shrink: false,
    }));
  });

  it('asks before shrinking, and only then starts a job that includes the shrink', async () => {
    mockApi.post.mockResolvedValue(job() as never);
    render(<DiskCleanupPanel pollMs={POLL} />);
    await screen.findByLabelText('shop/api:1.0.0');

    fireEvent.click(screen.getByRole('button', { name: /clean up and shrink/i }));
    expect(await screen.findByText(/stops WSL/i)).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: /yes, clean up and shrink/i }));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/system/disk-job',
      expect.objectContaining({ shrink: true })));
  });

  it('can back out of the shrink confirmation', async () => {
    render(<DiskCleanupPanel pollMs={POLL} />);
    await screen.findByLabelText('shop/api:1.0.0');

    fireEvent.click(screen.getByRole('button', { name: /clean up and shrink/i }));
    fireEvent.click(await screen.findByRole('button', { name: /cancel/i }));

    expect(screen.queryByText(/stops WSL/i)).not.toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });

  it('shows progress while it works: a bar, the step count, elapsed time and an estimate of what is left', async () => {
    latest = job({
      steps: [
        step({ status: 'Done', startedAt: NOW, finishedAt: NOW, estimatedSeconds: 10 }),
        step({ id: 'shrink', label: 'Shrinking your Windows disk', status: 'Running', startedAt: NOW, estimatedSeconds: 600, detail: 'Windows will ask permission' }),
      ],
    });
    render(<DiskCleanupPanel pollMs={POLL} />);

    const bar = await screen.findByRole('progressbar', { name: /overall progress/i });
    expect(Number(bar.getAttribute('aria-valuenow'))).toBeGreaterThanOrEqual(0);
    expect(screen.getByText(/step 2 of 2/i)).toBeInTheDocument();
    expect(screen.getByText(/elapsed/i)).toBeInTheDocument();
    expect(screen.getByText(/left \(estimate\)/i)).toBeInTheDocument();
    expect(screen.getByText('Shrinking your Windows disk')).toBeInTheDocument();
    expect(screen.getByText(/Windows will ask permission/)).toBeInTheDocument();
  });

  it('picks a running job back up after the page is reloaded, without any click', async () => {
    latest = job();
    render(<DiskCleanupPanel pollMs={POLL} />);

    expect(await screen.findByRole('progressbar', { name: /overall progress/i })).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });

  it('keeps polling until the job is finished, then shows what was achieved', async () => {
    mockApi.post.mockResolvedValue(job() as never);
    render(<DiskCleanupPanel pollMs={POLL} />);
    fireEvent.click(await screen.findByRole('button', { name: /^clean up now$/i }));
    latest = succeeded;

    expect(await screen.findByText(/all done/i)).toBeInTheDocument();
    expect(screen.getByText(/167 GB → 30 GB/)).toBeInTheDocument();
  });

  it('offers action buttons, not advice, when a build is in the way', async () => {
    latest = job({
      status: 'Blocked', error: 'A build is running or waiting for you.',
      steps: [step({ status: 'Skipped' })],
      blockers: [{ id: 'b1', projectName: 'Shop', status: 'Paused', step: 'DockerBuild', startedAt: NOW, canAbort: true }],
    });
    render(<DiskCleanupPanel pollMs={POLL} />);

    expect(await screen.findByText(/Shop/)).toBeInTheDocument();
    expect(screen.getByText(/paused/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /abort this build and continue/i })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /open build history/i })).toHaveAttribute('href', '/history');
  });

  it('aborting the blocking build cancels it, waits for it to stop, and then carries on with the same job', async () => {
    mockApi.post.mockResolvedValue(job() as never);
    latest = job({
      status: 'Blocked',
      steps: [step({ status: 'Skipped' })],
      blockers: [{ id: 'b1', projectName: 'Shop', status: 'Paused', startedAt: NOW, canAbort: true }],
    });
    render(<DiskCleanupPanel pollMs={POLL} />);

    fireEvent.click(await screen.findByRole('button', { name: /abort this build and continue/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/builds/b1/cancel', {}));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/system/disk-job', expect.any(Object)));
    const order = mockApi.post.mock.calls.map(c => c[0]);
    expect(order.indexOf('/api/builds/b1/cancel')).toBeLessThan(order.indexOf('/api/system/disk-job'));
  });

  const twoBlockers = (): IDiskJob => job({
    status: 'Blocked',
    steps: [step({ status: 'Skipped' })],
    blockers: [
      { id: 'b1', projectName: 'Shop', status: 'Paused', startedAt: NOW, canAbort: true },
      { id: 'b2', projectName: 'Blog', status: 'Running', startedAt: NOW, canAbort: true },
      { id: 'b3', projectName: 'Docs', status: 'Pending', startedAt: NOW, canAbort: false },
    ],
  });

  it('lets you pick several blocking builds at once, with a select-all toggle', async () => {
    latest = twoBlockers();
    render(<DiskCleanupPanel pollMs={POLL} />);

    const all = await screen.findByLabelText(/select all \(2\)/i);
    const bulk = screen.getByRole('button', { name: /abort selected builds/i });
    expect(all).not.toBeChecked();
    expect(bulk).toBeDisabled();
    expect(screen.queryByLabelText(/select docs/i)).not.toBeInTheDocument();

    fireEvent.click(all);
    expect(screen.getByLabelText(/select shop/i)).toBeChecked();
    expect(screen.getByLabelText(/select blog/i)).toBeChecked();
    expect(screen.getByRole('button', { name: /abort selected builds.*\(2\)/i })).toBeEnabled();

    fireEvent.click(all);
    expect(screen.getByLabelText(/select shop/i)).not.toBeChecked();
    expect(screen.getByRole('button', { name: /abort selected builds/i })).toBeDisabled();
  });

  it('select-all follows the individual boxes', async () => {
    latest = twoBlockers();
    render(<DiskCleanupPanel pollMs={POLL} />);

    fireEvent.click(await screen.findByLabelText(/select shop/i));
    expect(screen.getByLabelText(/select all/i)).not.toBeChecked();
    fireEvent.click(screen.getByLabelText(/select blog/i));
    expect(screen.getByLabelText(/select all/i)).toBeChecked();
  });

  it('aborts every selected build, then carries on with the job', async () => {
    mockApi.post.mockResolvedValue(job() as never);
    latest = twoBlockers();
    render(<DiskCleanupPanel pollMs={POLL} />);

    fireEvent.click(await screen.findByLabelText(/select all/i));
    fireEvent.click(screen.getByRole('button', { name: /abort selected builds/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/system/disk-job', expect.any(Object)));
    const urls = mockApi.post.mock.calls.map(c => c[0]);
    expect(urls).toEqual(expect.arrayContaining(['/api/builds/b1/cancel', '/api/builds/b2/cancel']));
    expect(urls.indexOf('/api/system/disk-job')).toBeGreaterThan(urls.indexOf('/api/builds/b2/cancel'));
  });

  it('offers to force-clean a build that cannot be cancelled because nothing is running behind it', async () => {
    mockApi.post.mockImplementation((async (url: string) => {
      if (url.endsWith('/b1/cancel')) throw { message: 'No active pipeline found for this build.' };
      return job();
    }) as never);
    latest = twoBlockers();
    render(<DiskCleanupPanel pollMs={POLL} />);

    fireEvent.click(await screen.findAllByRole('button', { name: /abort this build and continue/i }).then(b => b[0]));

    expect(await screen.findByText(/nothing is running behind/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /force clean and continue/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /leave them/i })).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalledWith('/api/system/disk-job', expect.anything());
  });

  it('force-cleaning frees the stuck builds and then carries on with the job', async () => {
    mockApi.post.mockImplementation((async (url: string) => {
      if (url.endsWith('/b1/cancel')) throw { message: 'No active pipeline found for this build.' };
      return job();
    }) as never);
    latest = twoBlockers();
    render(<DiskCleanupPanel pollMs={POLL} />);
    fireEvent.click((await screen.findAllByRole('button', { name: /abort this build and continue/i }))[0]);

    fireEvent.click(await screen.findByRole('button', { name: /force clean and continue/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/builds/b1/force-abort', {}));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/system/disk-job', expect.any(Object)));
  });

  it('a bulk abort cancels the real ones and offers force-clean only for the stuck ones', async () => {
    mockApi.post.mockImplementation((async (url: string) => {
      if (url.endsWith('/b1/cancel')) throw { message: 'No active pipeline found for this build.' };
      return job();
    }) as never);
    latest = twoBlockers();
    render(<DiskCleanupPanel pollMs={POLL} />);

    fireEvent.click(await screen.findByLabelText(/select all/i));
    fireEvent.click(screen.getByRole('button', { name: /abort selected builds/i }));

    expect(await screen.findByRole('button', { name: /force clean and continue/i })).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenCalledWith('/api/builds/b2/cancel', {});
    expect(screen.getAllByText(/Shop/).length).toBeGreaterThan(0);
  });

  it('leaving stuck builds alone dismisses the offer and forces nothing', async () => {
    mockApi.post.mockImplementation((async (url: string) => {
      if (url.endsWith('/cancel')) throw { message: 'No active pipeline found for this build.' };
      return job();
    }) as never);
    latest = twoBlockers();
    render(<DiskCleanupPanel pollMs={POLL} />);
    fireEvent.click((await screen.findAllByRole('button', { name: /abort this build and continue/i }))[0]);

    fireEvent.click(await screen.findByRole('button', { name: /leave them/i }));

    expect(screen.queryByRole('button', { name: /force clean and continue/i })).not.toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalledWith(expect.stringContaining('force-abort'), expect.anything());
  });

  it('does not pretend a build that is only waiting to start can be aborted', async () => {
    latest = job({
      status: 'Blocked', steps: [step({ status: 'Skipped' })],
      blockers: [{ id: 'b2', projectName: 'Blog', status: 'Pending', startedAt: NOW, canAbort: false }],
    });
    render(<DiskCleanupPanel pollMs={POLL} />);

    expect(await screen.findByText(/Blog/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /abort this build/i })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /check again/i })).toBeInTheDocument();
  });

  it('shows what went wrong with a Try again button when a job fails', async () => {
    mockApi.post.mockResolvedValue(job() as never);
    latest = job({ status: 'Failed', error: 'Build cache: Failed: boom', steps: [step({ status: 'Failed', startedAt: NOW, finishedAt: NOW })] });
    render(<DiskCleanupPanel pollMs={POLL} />);

    expect(await screen.findByText(/Build cache: Failed: boom/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /try again/i }));
    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/system/disk-job', expect.any(Object)));
  });

  it('says so plainly when Docker cannot be reached, and will not start a job', async () => {
    serve(preview({ images: [], error: 'Docker could not be reached, so there is nothing to clean up yet.' }));
    render(<DiskCleanupPanel pollMs={POLL} />);

    expect(await screen.findByText(/Docker could not be reached/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^clean up now$/i })).toBeDisabled();
  });

  it('lists nothing when there is nothing unused, but the buttons still work for the cache', async () => {
    serve(preview({ images: [] }));
    render(<DiskCleanupPanel pollMs={POLL} />);

    expect(await screen.findByText(/No unused images/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^clean up now$/i })).toBeEnabled();
  });
});
