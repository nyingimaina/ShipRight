import type { Role } from './AuthStore';

export interface CompanyChoice {
  id: string;
  name: string;
  role: Role;
  isActive: boolean;
  current: boolean;
}

export interface SessionUser {
  id: string;
  email: string;
  name: string;
  companyId: string;
  companyName?: string;
  role: Role;
  mustChangePassword: boolean;
  /** Every company this person may enter; older servers omit it. */
  companies?: CompanyChoice[];
  /** The person's chosen IANA zone; older servers omit it. */
  timeZone?: string;
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

  /** Moves this browser to another company the person belongs to. */
  switchCompany: (companyId: string) =>
    authRequest<Session>('POST', '/api/auth/active-company', { companyId }),

  /** Creates a company and lands the person in it as Admin. */
  createCompany: (name: string) =>
    authRequest<Session>('POST', '/api/companies', { name }),

  renameCompany: (companyId: string, name: string) =>
    authRequest<{ id: string; name: string }>('PUT', `/api/companies/${companyId}`, { name }),

  /** Personal preferences used by every screen. */
  updatePreferences: (preferences: { timeZone?: string }) =>
    authRequest<Session>('PUT', '/api/auth/preferences', preferences),
};