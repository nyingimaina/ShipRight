import { act, renderHook } from '@testing-library/react';
import { useSystemStats } from '../useSystemStats';
import { api } from '@/shared/ApiService';

let options: any;
jest.mock('jattac-web-poller', () => ({
  __esModule: true,
  default: jest.fn().mockImplementation((o: any) => {
    options = o;
    return { start: jest.fn().mockImplementation(() => Promise.resolve(o.func())), stop: jest.fn().mockResolvedValue(undefined) };
  }),
}));
jest.mock('@/shared/ApiService', () => ({ api: { get: jest.fn() } }));

const get = api.get as jest.Mock;
const stats = {
  diskUsedPercent: 72, ramUsedPercent: 48, processCpuPercent: 6, processCount: 11,
  available: true, capturedAtUtc: '2026-01-01T00:00:00Z',
};

describe('useSystemStats', () => {
  beforeEach(() => { get.mockReset(); options = undefined; });

  it('polls the cheap stats endpoint and exposes the latest reading', async () => {
    get.mockResolvedValue(stats);
    const { result } = renderHook(() => useSystemStats(10_000));

    expect(get).toHaveBeenCalledWith('/api/system/stats');
    expect(options.pollIntervalMilliseconds).toBe(10_000);

    await act(async () => { options.onResult(stats); });
    expect(result.current.stats).toEqual(stats);
  });

  it('keeps the last good reading when a poll fails', async () => {
    get.mockResolvedValue(stats);
    const { result } = renderHook(() => useSystemStats());
    await act(async () => { options.onResult(stats); });

    await act(async () => { options.onError(new Error('offline')); });
    expect(result.current.stats).toEqual(stats);
  });
});
