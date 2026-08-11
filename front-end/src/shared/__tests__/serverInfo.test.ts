import { loadServerMode, resetServerModeCache } from '../serverInfo';

const jsonMock = jest.fn();
const fetchMock = jest.fn();

beforeEach(() => {
  resetServerModeCache();
  jest.clearAllMocks();
  Object.defineProperty(global, 'fetch', { value: fetchMock, writable: true });
});

describe('serverInfo', () => {
  it('returns cloud mode when health payload reports cloud', async () => {
    jsonMock.mockResolvedValue({ mode: 'cloud' });
    fetchMock.mockResolvedValue({ ok: true, json: jsonMock });

    await expect(loadServerMode()).resolves.toBe('cloud');
  });

  it('returns desktop mode when health payload reports desktop', async () => {
    jsonMock.mockResolvedValue({ mode: 'desktop' });
    fetchMock.mockResolvedValue({ ok: true, json: jsonMock });

    await expect(loadServerMode()).resolves.toBe('desktop');
  });

  it('returns desktop mode when the payload has no mode field', async () => {
    jsonMock.mockResolvedValue({ serverVersion: '3.6.5' });
    fetchMock.mockResolvedValue({ ok: true, json: jsonMock });

    await expect(loadServerMode()).resolves.toBe('desktop');
  });

  it('falls back to desktop mode when the health request fails', async () => {
    fetchMock.mockRejectedValue(new Error('network down'));

    await expect(loadServerMode()).resolves.toBe('desktop');
  });

  it('caches the result so subsequent calls reuse the same response', async () => {
    jsonMock.mockResolvedValue({ mode: 'cloud' });
    fetchMock.mockResolvedValue({ ok: true, json: jsonMock });

    await loadServerMode();
    await loadServerMode();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
