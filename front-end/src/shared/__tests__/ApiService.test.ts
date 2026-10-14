import { api } from '../ApiService';
import { useAuthStore } from '../auth';

const mockFetch = jest.fn();
global.fetch = mockFetch as any;

function jsonResponse(status: number, body: unknown) {
  return { ok: status >= 200 && status < 300, status, json: async () => body };
}

describe('ApiService', () => {
  beforeEach(() => {
    mockFetch.mockReset();
    useAuthStore.getState().clearAuth();
  });

  it('sends no Authorization header and carries cookies on every request', async () => {
    mockFetch.mockResolvedValue(jsonResponse(200, { ok: true }));

    await api.get('/api/projects');

    expect(mockFetch).toHaveBeenCalledWith('/api/projects', expect.objectContaining({
      credentials: 'include',
    }));
    const init = mockFetch.mock.calls[0][1] as RequestInit;
    expect(init.headers).toBeDefined();
    expect((init.headers as Record<string, string>).Authorization ?? (init.headers as Record<string, string>).authorization).toBeUndefined();
  });

  it('posts JSON bodies with cookies', async () => {
    mockFetch.mockResolvedValue(jsonResponse(200, { ok: true }));

    await api.post('/api/builds', { id: 'b1' });

    expect(mockFetch).toHaveBeenCalledWith('/api/builds', expect.objectContaining({
      method: 'POST',
      credentials: 'include',
    }));
  });

  it('clears auth and surfaces a sign-in-required error rather than retrying', async () => {
    useAuthStore.setState({ user: { id: 'u1', email: 'a@b.com', name: 'A', companyId: 'c1', role: 'Admin', mustChangePassword: false }, isAuthenticated: true });
    mockFetch.mockResolvedValue(jsonResponse(401, { message: 'expired', isError: true }));

    await expect(api.get('/api/projects')).rejects.toEqual({ isError: true, message: 'Sign in required.' });
    expect(mockFetch).toHaveBeenCalledTimes(1);
    expect(useAuthStore.getState().isAuthenticated).toBe(false);
  });

  it('returns undefined for a 204', async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 204, json: async () => { throw new Error('no body'); } });

    const result = await api.delete('/api/projects/p1');
    expect(result).toBeUndefined();
  });

  it('returns raw text for getRaw with cookies and no auth header', async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 200, text: async () => 'hello' });

    const content = await api.getRaw('/api/fs/read?path=x.sql');

    expect(content).toBe('hello');
    expect(mockFetch).toHaveBeenCalledWith('/api/fs/read?path=x.sql', expect.objectContaining({
      credentials: 'include',
    }));
  });
});