import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import ProjectSpaceCard from '../ProjectSpaceCard';
import { api } from '@/shared/ApiService';
import { IProjectSpaceReport } from '@/shared/types/IProjectSpace';

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

const report = (over: Partial<IProjectSpaceReport> = {}): IProjectSpaceReport => ({
  projectId: 'p1',
  images: [
    { reference: 'shop/api:1.0.1', id: 'a', size: '500MB', sizeBytes: 500e6, age: '2 days ago', inUse: false, origin: 'Successful build', buildVersion: '1.0.1' },
    { reference: 'shop/api:1.0.2', id: 'b', size: '400MB', sizeBytes: 400e6, age: '1 day ago', inUse: false, origin: 'Failed build', buildVersion: '1.0.2' },
    { reference: 'shop/api:1.0.0', id: 'c', size: '300MB', sizeBytes: 300e6, age: '9 days ago', inUse: true, origin: 'Successful build', buildVersion: '1.0.0' },
  ],
  snapshots: [{ buildId: 'old1', path: '/home/dev/.shipright/build/p1/old1', sizeBytes: 2 * GB, active: false, origin: 'Failed build' }],
  imagesBytes: 1.2e9, snapshotsBytes: 2 * GB, cacheBytes: 5 * GB,
  totalBytes: 1.2e9 + 7 * GB, reclaimableBytes: 0.9e9 + 2 * GB,
  services: [{ name: 'Api', keepCount: 2, neverClean: false }],
  deletesAfterPush: false, error: null, ...over,
});

describe('ProjectSpaceCard', () => {
  beforeEach(() => jest.resetAllMocks());

  it('says how much space earlier builds are holding, and how much can be freed right now', async () => {
    mockApi.get.mockResolvedValue(report() as never);
    render(<ProjectSpaceCard projectId="p1" />);

    expect(await screen.findByText(/earlier builds are holding/i)).toBeInTheDocument();
    expect(screen.getByText(/8\.1 GB/)).toBeInTheDocument();          // total held
    expect(screen.getByRole('button', { name: /free up 2\.8 GB now/i })).toBeEnabled();
  });

  it('shows each image with whether a successful or a failed build made it, and whether it is in use', async () => {
    mockApi.get.mockResolvedValue(report() as never);
    render(<ProjectSpaceCard projectId="p1" />);

    await screen.findByText(/earlier builds are holding/i);
    fireEvent.click(screen.getByText(/see what is using the space/i));

    expect(screen.getByText('shop/api:1.0.2')).toBeInTheDocument();
    expect(screen.getAllByText('Failed build').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Successful build').length).toBeGreaterThan(0);
    expect(screen.getByText(/in use/i)).toBeInTheDocument();
  });

  it('explains the build cache is kept on purpose', async () => {
    mockApi.get.mockResolvedValue(report() as never);
    render(<ProjectSpaceCard projectId="p1" />);

    expect(await screen.findByText(/makes the next build faster/i)).toBeInTheDocument();
  });

  it('frees the space in one click and says how much came back', async () => {
    mockApi.get.mockResolvedValue(report() as never);
    mockApi.post.mockResolvedValue({
      items: [{ name: 'shop/api:1.0.1', kind: 'Image', removed: true }],
      freedBytes: 2.8e9,
      report: report({ images: [], snapshots: [], reclaimableBytes: 0, totalBytes: 5 * GB, imagesBytes: 0, snapshotsBytes: 0 }),
    } as never);
    render(<ProjectSpaceCard projectId="p1" />);

    fireEvent.click(await screen.findByRole('button', { name: /free up 2\.8 GB now/i }));

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/projects/p1/space/clean', {}));
    expect(await screen.findByText(/freed 2\.6 GB/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /nothing to free/i })).toBeDisabled();
    expect(toast.success).toHaveBeenCalled();
  });

  it('has nothing to offer when there is nothing to free', async () => {
    mockApi.get.mockResolvedValue(report({ images: [], snapshots: [], reclaimableBytes: 0, imagesBytes: 0, snapshotsBytes: 0, totalBytes: 0 }) as never);
    render(<ProjectSpaceCard projectId="p1" />);

    expect(await screen.findByRole('button', { name: /nothing to free/i })).toBeDisabled();
  });

  it('reflects whether images are deleted after they are pushed, and switches it for the whole project', async () => {
    mockApi.get.mockResolvedValue(report() as never);
    mockApi.post.mockResolvedValue({ deleteAfterPush: true } as never);
    render(<ProjectSpaceCard projectId="p1" />);

    const toggle = await screen.findByLabelText(/delete images from this computer as soon as they are pushed/i);
    expect(toggle).not.toBeChecked();
    fireEvent.click(toggle);

    await waitFor(() => expect(mockApi.post).toHaveBeenCalledWith('/api/projects/p1/local-cleanup-policy', { deleteAfterPush: true }));
    await waitFor(() => expect(toggle).toBeChecked());
  });

  it('leaves the switch as it was if saving fails', async () => {
    mockApi.get.mockResolvedValue(report() as never);
    mockApi.post.mockRejectedValue({ message: 'nope' });
    render(<ProjectSpaceCard projectId="p1" />);

    const toggle = await screen.findByLabelText(/delete images from this computer as soon as they are pushed/i);
    fireEvent.click(toggle);

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('nope'));
    expect(toggle).not.toBeChecked();
  });

  it('says so plainly when Docker cannot be reached', async () => {
    mockApi.get.mockResolvedValue(report({ images: [], snapshots: [], error: 'Docker could not be reached, so the space used by images cannot be measured.' }) as never);
    render(<ProjectSpaceCard projectId="p1" />);

    expect(await screen.findByText(/Docker could not be reached/)).toBeInTheDocument();
  });

  it('says so when the report cannot be loaded, and can try again', async () => {
    mockApi.get.mockRejectedValueOnce(new Error('down')).mockResolvedValue(report() as never);
    render(<ProjectSpaceCard projectId="p1" />);

    expect(await screen.findByText(/could not measure/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /refresh/i }));

    expect(await screen.findByText(/earlier builds are holding/i)).toBeInTheDocument();
  });
});
