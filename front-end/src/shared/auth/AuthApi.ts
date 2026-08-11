export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  user: {
    id: string;
    email: string;
    name: string;
    companyId: string;
    isAdmin: boolean;
  };
}

export interface ApiError {
  message: string;
  isError: boolean;
}

const BASE = process.env.NEXT_PUBLIC_API_URL ?? '';

async function authRequest<T>(
  method: string,
  path: string,
  body?: unknown,
  refreshToken?: string,
): Promise<T> {
  const headers: Record<string, string> = {};
  if (body) headers['Content-Type'] = 'application/json';
  if (refreshToken) headers['X-Refresh-Token'] = refreshToken;

  const res = await fetch(`${BASE}${path}`, {
    method,
    headers: Object.keys(headers).length > 0 ? headers : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });

  const text = await res.text();
  let data: unknown = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = null;
    }
  }

  if (!res.ok) {
    throw (data as ApiError | null) ?? {
      message: `Request failed with status ${res.status}`,
      isError: true,
    };
  }
  return data as T;
}

export const authApi = {
  login: (email: string, password: string) =>
    authRequest<AuthResponse>('POST', '/api/auth/login', { email, password }),

  signup: (email: string, password: string, name: string, companyName: string) =>
    authRequest<AuthResponse>('POST', '/api/auth/signup', { email, password, name, companyName }),

  refresh: (refreshToken: string) =>
    authRequest<{ accessToken: string; refreshToken: string }>(
      'POST', '/api/auth/refresh', undefined, refreshToken,
    ),

  forgotPassword: (email: string) =>
    authRequest<{ message: string; resetToken?: string }>(
      'POST', '/api/auth/forgot-password', { email },
    ),

  resetPassword: (token: string, newPassword: string) =>
    authRequest<{ message: string }>('POST', '/api/auth/reset-password', { token, newPassword }),
};
