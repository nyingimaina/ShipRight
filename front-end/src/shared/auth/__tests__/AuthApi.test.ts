import { authApi, googleStartUrl } from '../AuthApi';

const textMock = jest.fn();
const fetchMock = jest.fn();

function jsonResponse(status: number, body: unknown) {
  return { ok: status >= 200 && status < 300, status, text: async () => JSON.stringify(body) };
}

beforeEach(() => {
  jest.clearAllMocks();
  Object.defineProperty(global, 'fetch', { value: fetchMock, writable: true });
});

describe('authApi', () => {
  it('returns the parsed session body on success and sends the identity cookie', async () => {
    const body = {
      user: { id: 'u1', email: 'a@b.c', name: 'A', companyId: 'c1', role: 'Admin', mustChangePassword: false },
    };
    fetchMock.mockResolvedValue(jsonResponse(200, body));

    await expect(authApi.login('a@b.c', 'secret')).resolves.toEqual(body);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/auth/login');
    expect(init).toMatchObject({ method: 'POST', credentials: 'include' });
    expect(JSON.parse(init.body)).toEqual({ email: 'a@b.c', password: 'secret' });
  });

  it('throws a readable ApiError when the server returns an empty 404 body', async () => {
    textMock.mockResolvedValue('');
    fetchMock.mockResolvedValue({ ok: false, status: 404, text: textMock });

    const err: unknown = await authApi.me().catch((e) => e);

    expect(err).not.toBeInstanceOf(SyntaxError);
    expect(err).toMatchObject({ isError: true });
    expect((err as { message: string }).message).toContain('404');
  });

  it('throws the error object returned by the server, including field errors', async () => {
    textMock.mockResolvedValue(JSON.stringify({
      message: 'Validation failed',
      isError: true,
      errors: { email: ['Enter an email address.'] },
    }));
    fetchMock.mockResolvedValue({ ok: false, status: 400, text: textMock });

    await expect(authApi.register('', 'short', 'A', 'Acme'))
      .rejects.toEqual({
        message: 'Validation failed',
        isError: true,
        errors: { email: ['Enter an email address.'] },
      });
  });

  it('resolves providers and the google start url', async () => {
    fetchMock.mockResolvedValue(jsonResponse(200, { google: true }));
    await expect(authApi.providers()).resolves.toEqual({ google: true });
    expect(googleStartUrl()).toBe('/api/auth/google/start');
  });

  it('calls change-password with both passwords and returns the refreshed session', async () => {
    const body = { user: { id: 'u1', email: 'a@b.c', name: 'A', companyId: 'c1', role: 'Operator', mustChangePassword: false } };
    fetchMock.mockResolvedValue(jsonResponse(200, body));

    await expect(authApi.changePassword('old-pass', 'new-pass-123')).resolves.toEqual(body);
    const [, init] = fetchMock.mock.calls[0];
    expect(JSON.parse(init.body)).toEqual({ currentPassword: 'old-pass', newPassword: 'new-pass-123' });
  });
});