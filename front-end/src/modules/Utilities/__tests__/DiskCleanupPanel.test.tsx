import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import DiskCleanupPanel from '../DiskCleanupPanel';
import { api } from '@/shared/ApiService';
import { ICleanupPreview } from '@/shared/types/IDiskCleanup';

jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn(), post: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { error: jest.fn(), success: jest.fn() } }));

jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled }: any) => (
    <button type="button" onClick={onClick} disabled={disabled}>{children}</button>
  ),
}));

const mockApi = api as jest.Mocked<typeof api>;

const preview = (over: Partial<ICleanupPreview> = {}): ICleanupPreview => ({
  images: [
    { reference: 'shop/api:1.0.0', id: 'a', size: '500MB', sizeBytes: 500e6, age: '2 days ago', owner: 'ShipRight', selectedByDefault: true },
    { reference: 'postgres:16', id: 'c', size: '400MB', sizeBytes: 400e6, age: '3 weeks ago', owner: 'Other', selectedByDefault: false },
  ],
  inUseCount: 2, danglingCount: 3, selectedBytes: 500e6, diskUsage: 'Images 5 1 3.2GB', error: null, ...over,
});

const wsl = { vhdxFiles: [{ path: 'C:\\x\\ext4.vhdx', sizeBytes: 167 * 1024 ** 3 }], distros: [], error: null };

function serve(p: ICleanupPreview = preview()) {
  mockApi.get.mockImplementation(async (url: string) => (url.includes('docker-cleanup') ? p : wsl) as never);
}

const cleanResult = { items: [{ reference: 'shop/api:1.0.0', removed: true }], danglingOutput: 'Total reclaimed space: 40MB', buildCacheOutput: null, before: 'b', after: 'a' };

describe('DiskCleanupPanel', () => {
  beforeEach(() => { jest.resetAllMocks(); serve(); });

  it("pre-ticks ShipRight's own images and leaves everything else unticked", async () => {
    render(<DiskCleanupPanel />);

    expect(await screen.findByLabelText('shop/api:1.0.0')).toBeChecked();
    expect(screen.getByLabelText('postgres:16')).not.toBeChecked();
  });

  it('explains that other images are offered but not ticked, and that data is never touched', async () => {
    render(<DiskCleanupPanel />);
    await screen.findByLabelText('postgres:16');

    expect(screen.getByText(/not made by ShipRight/i)).toBeInTheDocument();
    expect(screen.getByText(/never touches your data/i)).toBeInTheDocument();
  });

  it('shows how big the Windows virtual disk is', async () => {
    render(<DiskCleanupPanel />);

    expect(await screen.findByText(/167 GB/)).toBeInTheDocument();
  });

  it('cleans up exactly what is ticked, plus the leftovers options', async () => {
    mockApi.post.mockResolvedValue(cleanResult as never);
    render(<DiskCleanupPanel />);
    fireEvent.click(await screen.findByLabelText('postgres:16'));

    fireEvent.click(screen.getByRole('button', { name: /^clean up now$/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/system/docker-cleanup/run', {
      images: ['shop/api:1.0.0', 'postgres:16'], pruneDangling: true, pruneBuildCache: true,
    }));
    expect(await screen.findByText(/Total reclaimed space: 40MB/)).toBeInTheDocument();
    expect(mockApi.post).toHaveBeenCalledTimes(1);
  });

  it('can leave out the unnamed leftovers and the build cache', async () => {
    mockApi.post.mockResolvedValue(cleanResult as never);
    render(<DiskCleanupPanel />);
    await screen.findByLabelText('shop/api:1.0.0');

    fireEvent.click(screen.getByLabelText(/leftover/i));
    fireEvent.click(screen.getByLabelText(/build cache/i));
    fireEvent.click(screen.getByRole('button', { name: /^clean up now$/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/system/docker-cleanup/run',
      { images: ['shop/api:1.0.0'], pruneDangling: false, pruneBuildCache: false }));
  });

  it('asks before shrinking the Windows disk, and does nothing if the person backs out', async () => {
    render(<DiskCleanupPanel />);
    await screen.findByLabelText('shop/api:1.0.0');

    fireEvent.click(screen.getByRole('button', { name: /clean up and shrink/i }));

    expect(await screen.findByText(/stops WSL/i)).toBeInTheDocument();
    expect(screen.getByText(/Windows will ask permission/i)).toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
    expect(screen.queryByText(/stops WSL/i)).not.toBeInTheDocument();
    expect(mockApi.post).not.toHaveBeenCalled();
  });

  it('after confirming, cleans first and only then shrinks', async () => {
    mockApi.post.mockImplementation(async (url: string) => (url.endsWith('/shrink')
      ? { succeeded: true, files: [{ path: 'C:\\x\\ext4.vhdx', beforeBytes: 167 * 1024 ** 3, afterBytes: 30 * 1024 ** 3 }], messages: [] }
      : cleanResult) as never);
    render(<DiskCleanupPanel />);
    await screen.findByLabelText('shop/api:1.0.0');

    fireEvent.click(screen.getByRole('button', { name: /clean up and shrink/i }));
    fireEvent.click(await screen.findByRole('button', { name: /yes, clean up and shrink/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledTimes(2));
    expect(mockApi.post.mock.calls.map(c => c[0])).toEqual(['/api/system/docker-cleanup/run', '/api/system/wsl-disk/shrink']);
    expect(await screen.findByText(/167 GB → 30 GB/)).toBeInTheDocument();
  });

  it('keeps showing what was cleaned when the shrink step is refused', async () => {
    mockApi.post.mockImplementation(async (url: string) => {
      if (url.endsWith('/shrink')) throw { error: 'diskpart needs administrator permission', messages: ['Answer Yes when Windows asks.'] };
      return cleanResult as never;
    });
    render(<DiskCleanupPanel />);
    await screen.findByLabelText('shop/api:1.0.0');

    fireEvent.click(screen.getByRole('button', { name: /clean up and shrink/i }));
    fireEvent.click(await screen.findByRole('button', { name: /yes, clean up and shrink/i }));

    expect(await screen.findByText(/diskpart needs administrator permission/)).toBeInTheDocument();
    expect(screen.getByText(/Total reclaimed space: 40MB/)).toBeInTheDocument();
  });

  it('says so plainly when Docker cannot be reached', async () => {
    serve(preview({ images: [], error: 'Docker could not be reached, so there is nothing to clean up yet.' }));
    render(<DiskCleanupPanel />);

    expect(await screen.findByText(/Docker could not be reached/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^clean up now$/i })).toBeDisabled();
  });

  it('lists nothing when there is nothing unused, and keeps the buttons usable for the cache', async () => {
    serve(preview({ images: [] }));
    render(<DiskCleanupPanel />);

    expect(await screen.findByText(/No unused images/i)).toBeInTheDocument();
    expect(screen.queryByLabelText('shop/api:1.0.0')).toBeNull();
    expect(screen.getByRole('button', { name: /^clean up now$/i })).toBeEnabled();
  });
});
