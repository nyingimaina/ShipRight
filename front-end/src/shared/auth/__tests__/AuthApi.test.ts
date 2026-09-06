import { authApi } from '../AuthApi';

const textMock = jest.fn();
const fetchMock = jest.fn();

beforeEach(() => {
  jest.clearAllMocks();
  Object.defineProperty(global, 'fetch', { value: fetchMock, writable: true });
});

describe('authApi', () => {
  it('returns the parsed body on success', async () => {
    const body = {
      accessToken: 'at',
      refreshToken: 'rt',
      user: { id: 'u1', email: 'a@b.c', name: 'A', companyId: 'c1', isAdmin: false },
    };
    textMock.mockResolvedValue(JSON.stringify(body));
    fetchMock.mockResolvedValue({ ok: true, status: 200, text: textMock });

    await expect(authApi.login('a@b.c', 'secret')).resolves.toEqual(body);
  });

  it('throws a readable ApiError when the server returns an empty 404 body', async () => {
    textMock.mockResolvedValue('');
    fetchMock.mockResolvedValue({ ok: false, status: 404, text: textMock });

    const err: unknown = await authApi.login('a@b.c', 'secret').catch((e) => e);

    expect(err).not.toBeInstanceOf(SyntaxError);
    expect(err).toMatchObject({ isError: true });
    expect((err as { message: string }).message).toContain('404');
  });

  it('throws the error object returned by the server', async () => {
    textMock.mockResolvedValue(JSON.stringify({ message: 'Invalid credentials', isError: true }));
    fetchMock.mockResolvedValue({ ok: false, status: 401, text: textMock });

    await expect(authApi.login('a@b.c', 'wrong'))
      .rejects.toEqual({ message: 'Invalid credentials', isError: true });
  });
});
