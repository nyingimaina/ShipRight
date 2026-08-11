import { api, setAccessToken } from '../ApiService';
import { useAuthStore } from '../auth';

const mockFetch = jest.fn();
global.fetch = mockFetch as any;

const user = { id: 'u1', email: 'a@b.com', name: 'A', companyId: 'c1', isAdmin: true };

function jsonResponse(status: number, body: unknown) {
  return { ok: status >= 200 && status < 300, status, json: async () => body };
}

describe('ApiService', () => {
  beforeEach(() => {
    mockFetch.mockReset();
    setAccessToken(null);
    useAuthStore.setState({ accessToken: null, refreshToken: null, user: null, isAuthenticated: false });
  });

  it('sends the Bearer token when set', async () => {
    setAccessToken('tok1');
    mockFetch.mockResolvedValue(jsonResponse(200, { ok: true }));

    await api.get('/api/projects');

    expect(mockFetch).toHaveBeenCalledWith('/api/projects', expect.objectContaining({
      headers: expect.objectContaining({ Authorization: 'Bearer tok1' }),
    }));
  });

  it('refreshes the session and retries once on a 401', async () => {
    setAccessToken('expired');
    useAuthStore.setState({ accessToken: 'expired', refreshToken: 'rt1', user, isAuthenticated: true });

    mockFetch
      .mockImplementationOnce(async () => jsonResponse(401, { message: 'expired', isError: true }))
      .mockImplementationOnce(async (url: string) =>
        url.endsWith('/api/auth/refresh')
          ? jsonResponse(200, { accessToken: 'tok2', refreshToken: 'rt2' })
          : jsonResponse(404, {}))
      .mockResolvedValueOnce(jsonResponse(200, { ok: true }));

    const data = await api.get('/api/projects');

    expect(data).toEqual({ ok: true });
    expect(mockFetch).toHaveBeenCalledTimes(3);
    expect(mockFetch).toHaveBeenLastCalledWith('/api/projects', expect.objectContaining({
      headers: expect.objectContaining({ Authorization: 'Bearer tok2' }),
    }));
    expect(useAuthStore.getState().accessToken).toBe('tok2');
    expect(useAuthStore.getState().refreshToken).toBe('rt2');
  });

  it('clears auth and surfaces the 401 when refresh fails', async () => {
    useAuthStore.setState({ accessToken: 'expired', refreshToken: 'bad', user, isAuthenticated: true });
    mockFetch.mockResolvedValue(jsonResponse(401, { message: 'expired', isError: true }));

    await expect(api.get('/api/projects')).rejects.toEqual({ message: 'expired', isError: true });
    expect(useAuthStore.getState().isAuthenticated).toBe(false);
  });

  it('shares a single refresh across concurrent 401s', async () => {
    useAuthStore.setState({ accessToken: 'expired', refreshToken: 'rt1', user, isAuthenticated: true });
    mockFetch.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/api/auth/refresh')) {
        return jsonResponse(200, { accessToken: 'tok2', refreshToken: 'rt2' });
      }
      if ((init?.headers as Record<string, string>)?.Authorization === 'Bearer tok2') {
        return jsonResponse(200, { ok: true });
      }
      return jsonResponse(401, { message: 'expired', isError: true });
    });

    const [a, b] = await Promise.all([api.get('/api/a'), api.get('/api/b')]);

    expect(a).toEqual({ ok: true });
    expect(b).toEqual({ ok: true });
    const refreshCalls = mockFetch.mock.calls.filter(([url]) => (url as string).endsWith('/api/auth/refresh'));
    expect(refreshCalls).toHaveLength(1);
  });

  it('refreshes and retries raw text requests on a 401', async () => {
    setAccessToken('expired');
    useAuthStore.setState({ accessToken: 'expired', refreshToken: 'rt1', user, isAuthenticated: true });

    mockFetch
      .mockImplementationOnce(async () => jsonResponse(401, { message: 'expired', isError: true }))
      .mockImplementationOnce(async (url: string) =>
        url.endsWith('/api/auth/refresh')
          ? jsonResponse(200, { accessToken: 'tok2', refreshToken: 'rt2' })
          : jsonResponse(404, {}))
      .mockResolvedValueOnce({ ok: true, status: 200, text: async () => 'hello' });

    const content = await api.getRaw('/api/fs/read?path=x.sql');

    expect(content).toBe('hello');
    expect(mockFetch).toHaveBeenLastCalledWith('/api/fs/read?path=x.sql', expect.objectContaining({
      headers: expect.objectContaining({ Authorization: 'Bearer tok2' }),
    }));
  });
});
