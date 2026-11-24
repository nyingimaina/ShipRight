import {
  loadServerMode, loadServerVersion, resetServerModeCache, resetServerVersionCache,
} from '../serverInfo';

const jsonMock = jest.fn();
const fetchMock = jest.fn();

beforeEach(() => {
  resetServerModeCache();
  resetServerVersionCache();
  jest.clearAllMocks();
  Object.defineProperty(global, 'fetch', { value: fetchMock, writable: true });
});

describe('serverInfo', () => {
  it('returns cloud mode when health payload reports cloud', async () => {
    jsonMock.mockResolvedValue({ mode: 'cloud' });
    fetchMock.mockResolvedValue({ ok: true, json: jsonMock });

    await expect(loadServerMode()).resolves.toBe('cloud');
  });

  it('returns cloud mode for the casing the server actually sends', async () => {
    // The back-end serializes the AppMode.Cloud enum as "Cloud", not "cloud". Treating that as desktop skips the
    // sign-in guard entirely on a hosted deployment.
    jsonMock.mockResolvedValue({ mode: 'Cloud' });
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

  describe('loadServerVersion', () => {
    it('reports the web version the server is actually serving, not its assembly version', async () => {
      jsonMock.mockResolvedValue({ serverVersion: '1.0.0', webVersion: '4.10.24', mode: 'Cloud' });
      fetchMock.mockResolvedValue({ ok: true, json: jsonMock });

      await expect(loadServerVersion()).resolves.toBe('4.10.24');
    });

    it('falls back to the assembly version for older servers that only report serverVersion', async () => {
      jsonMock.mockResolvedValue({ serverVersion: '4.9.0' });
      fetchMock.mockResolvedValue({ ok: true, json: jsonMock });

      await expect(loadServerVersion()).resolves.toBe('4.9.0');
    });

    it('returns null when the health request fails', async () => {
      fetchMock.mockRejectedValue(new Error('network down'));

      await expect(loadServerVersion()).resolves.toBeNull();
    });
  });
});
