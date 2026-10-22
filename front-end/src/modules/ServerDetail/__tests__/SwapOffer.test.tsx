import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import React from 'react';
import SwapOffer from '../SwapOffer';
import { api } from '@/shared/ApiService';

jest.mock('@/shared/ApiService', () => ({ api: { post: jest.fn() } }));
jest.mock('react-hot-toast', () => ({ __esModule: true, default: { success: jest.fn(), error: jest.fn() } }));

const mocked = api as jest.Mocked<typeof api>;
const base = { serverId: 's1', reachable: true, swapTotalMb: 0, memTotalMb: 908, onCreated: jest.fn() };

beforeEach(() => { jest.clearAllMocks(); });

describe('SwapOffer', () => {
  it('offers to create swap when the server has none', () => {
    render(<SwapOffer {...base} />);
    expect(screen.getByText(/no swap/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create 2 GB swap' })).toBeInTheDocument();
  });

  it('says why: the server has little memory', () => {
    render(<SwapOffer {...base} />);
    expect(screen.getByText(/908 MB/)).toBeInTheDocument();
  });

  it('is hidden when the server already has swap', () => {
    const { container } = render(<SwapOffer {...base} swapTotalMb={2048} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('is hidden when the server cannot be reached', () => {
    const { container } = render(<SwapOffer {...base} reachable={false} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('creates the swap, tells the person, and reports the new size', async () => {
    mocked.post.mockResolvedValue({ success: true, output: 'Swap enabled.' } as any);
    render(<SwapOffer {...base} />);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Create 2 GB swap' })); });
    expect(mocked.post).toHaveBeenCalledWith('/api/servers/s1/swap', { sizeGb: 2 });
    await waitFor(() => expect(base.onCreated).toHaveBeenCalledWith(2));
  });

  it('shows what went wrong when the server refuses', async () => {
    mocked.post.mockResolvedValue({ success: false, output: 'Not enough free disk space for a 2G swap file.' } as any);
    render(<SwapOffer {...base} />);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Create 2 GB swap' })); });
    expect(await screen.findByText(/Not enough free disk space/)).toBeInTheDocument();
    expect(base.onCreated).not.toHaveBeenCalled();
  });

  it('shows a failed request too', async () => {
    mocked.post.mockRejectedValue(new Error('SSH timed out'));
    render(<SwapOffer {...base} />);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Create 2 GB swap' })); });
    expect(await screen.findByText(/SSH timed out/)).toBeInTheDocument();
  });

  it('is busy while working and ignores rapid clicks', async () => {
    let resolve!: (v: any) => void;
    mocked.post.mockReturnValue(new Promise(r => { resolve = r; }) as any);
    render(<SwapOffer {...base} />);
    const btn = screen.getByRole('button', { name: 'Create 2 GB swap' });
    fireEvent.click(btn); fireEvent.click(btn); fireEvent.click(btn);
    expect(mocked.post).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('status')).toHaveTextContent(/Creating/);
    expect(screen.getByRole('button', { name: /Create 2 GB swap/ })).toBeDisabled();
    await act(async () => { resolve({ success: true, output: '' }); });
  });
});
