import type { Role } from './AuthStore';

export interface SessionUser {
  id: string;
  email: string;
  name: string;
  companyId: string;
  role: Role;
  mustChangePassword: boolean;
}

export interface Session {
  user: SessionUser;
}

export interface ApiError {
  message: string;
  isError: boolean;
  errors?: Record<string, string[]>;
}

export interface ProvidersInfo {
  google: boolean;
}

const BASE = process.env.NEXT_PUBLIC_API_URL ?? '';

async function authRequest<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  if (body) headers['Content-Type'] = 'application/json';

  // The identity cookie is httpOnly, so the browser just sends it along with every call.
  const res = await fetch(`${BASE}${path}`, {
    method,
    headers: Object.keys(headers).length > 0 ? headers : undefined,
    body: body ? JSON.stringify(body) : undefined,
    credentials: 'include',
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

/** URL that starts the redirect to Google; the browser navigates to it as a whole page. */
export function googleStartUrl(): string {
  return `${BASE}/api/auth/google/start`;
}

export const authApi = {
  login: (email: string, password: string) =>
    authRequest<Session>('POST', '/api/auth/login', { email, password }),

  register: (email: string, password: string, name: string, companyName: string) =>
    authRequest<Session>('POST', '/api/auth/register', { email, password, name, companyName }),

  me: () => authRequest<Session>('GET', '/api/auth/me'),

  logout: () => authRequest<{ message: string }>('POST', '/api/auth/logout'),

  providers: () => authRequest<ProvidersInfo>('GET', '/api/auth/providers'),

  forgotPassword: (email: string) =>
    authRequest<{ message: string }>('POST', '/api/auth/forgot-password', { email }),

  resetPassword: (token: string, newPassword: string) =>
    authRequest<{ message: string }>('POST', '/api/auth/reset-password', { token, newPassword }),

  changePassword: (currentPassword: string, newPassword: string) =>
    authRequest<Session>('POST', '/api/auth/change-password', { currentPassword, newPassword }),
};