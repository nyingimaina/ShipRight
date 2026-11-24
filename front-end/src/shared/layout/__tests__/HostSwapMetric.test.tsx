import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import toast from 'react-hot-toast';
import { api } from '@/shared/ApiService';
import HostSwapMetric from '../HostSwapMetric';

jest.mock('@/shared/ApiService', () => ({ api: { post: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { success: jest.fn(), error: jest.fn() } }));
jest.mock('jattac.libs.web.zest-button', () => ({
  __esModule: true,
  default: ({ children, onClick, disabled, zest }: any) => (
    <button type="button" data-semantic={zest?.semanticType}
      onClick={(e: any) => { void Promise.resolve(onClick(e)).catch(() => undefined); }} disabled={disabled}>
      {children}
    </button>
  ),
}));

const post = (api as any).post as jest.Mock;

const swap = (over: object = {}) => ({
  swapUsedBytes: 512, swapTotalBytes: 2048, swapUsedPercent: 25, available: true, configured: true, ...over,
});

describe('HostSwapMetric', () => {
  beforeEach(() => jest.clearAllMocks());

  it('shows the host swap used percentage when swap already exists', () => {
    render(<HostSwapMetric swap={swap()} canCreate onChanged={jest.fn()} />);

    expect(screen.getByText('Swap 25%')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /add/i })).toBeNull();
  });

  it('offers an Admin one confirmation step before creating swap on the host', () => {
    render(<HostSwapMetric swap={swap({ swapUsedBytes: 0, swapTotalBytes: 0, swapUsedPercent: 0 })} canCreate onChanged={jest.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: /Swap 0%/ }));
    expect(screen.getByText(/Create 2 GB swap on the host\?/)).toBeInTheDocument();
    const create = screen.getByRole('button', { name: 'Create swap' });
    expect(create).toHaveAttribute('data-semantic', 'save');
    expect(post).not.toHaveBeenCalled();
  });

  it('posts the host action, refreshes the reading and confirms success', async () => {
    post.mockResolvedValue({ success: true, output: 'Swap enabled.' });
    const onChanged = jest.fn();
    render(<HostSwapMetric swap={swap({ swapUsedBytes: 0, swapTotalBytes: 0, swapUsedPercent: 0 })} canCreate onChanged={onChanged} />);

    fireEvent.click(screen.getByRole('button', { name: /Swap 0%/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Create swap' }));

    await waitFor(() => expect(post).toHaveBeenCalledWith('/api/system/host-swap', { sizeGb: 2 }));
    expect(onChanged).toHaveBeenCalled();
    expect((toast as any).success).toHaveBeenCalledWith('Swap enabled on the host.');
    await waitFor(() => expect(screen.queryByText(/Create 2 GB swap on the host\?/)).toBeNull());
  });

  it('shows the server failure as an alert and keeps the confirmation open', async () => {
    post.mockRejectedValue(new Error('Host swap is not configured.'));
    render(<HostSwapMetric swap={swap({ swapUsedBytes: 0, swapTotalBytes: 0, swapUsedPercent: 0 })} canCreate onChanged={jest.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: /Swap 0%/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Create swap' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Host swap is not configured.');
    expect(screen.getByText(/Create 2 GB swap on the host\?/)).toBeInTheDocument();
  });

  it('does not offer host changes to a non-Admin or when the host is not configured', () => {
    const { rerender } = render(<HostSwapMetric swap={swap({ swapTotalBytes: 0 })} canCreate={false} onChanged={jest.fn()} />);
    expect(screen.queryByRole('button', { name: /Swap 0%/ })).toBeNull();

    rerender(<HostSwapMetric swap={swap({ swapTotalBytes: 0, configured: false })} canCreate onChanged={jest.fn()} />);
    expect(screen.queryByRole('button', { name: /Swap 0%/ })).toBeNull();
  });
});
