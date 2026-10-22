import { useAuthStore } from '@/shared/auth';

const BASE = process.env.NEXT_PUBLIC_API_URL ?? '';

function clearSession() {
  // The identity cookie belongs to the server; dropping our mirror lets AuthGuard send the person
  // back to the sign-in screen on their next render.
  useAuthStore.getState().clearAuth();
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  if (body) headers['Content-Type'] = 'application/json';

  const res = await fetch(`${BASE}${path}`, {
    method,
    headers,
    body: body ? JSON.stringify(body) : undefined,
    credentials: 'include',
  });

  if (res.status === 401) {
    clearSession();
    throw { isError: true, message: 'Sign in required.' };
  }

  if (res.status === 204) return undefined as T;

  const data = await res.json().catch(() => ({ isError: true, message: `HTTP ${res.status}` }));
  if (!res.ok) throw data;
  return data as T;
}

async function getRaw(path: string): Promise<string> {
  const res = await fetch(`${BASE}${path}`, { credentials: 'include' });

  if (res.status === 401) {
    clearSession();
    throw { isError: true, message: 'Sign in required.' };
  }

  if (!res.ok) {
    const data = await res.json().catch(() => ({ message: `HTTP ${res.status}` }));
    throw data;
  }
  return res.text();
}

/** POST a raw file body (used for server file uploads — avoids multipart size limits). */
async function upload<T>(path: string, file: File): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/octet-stream' },
    body: file,
    credentials: 'include',
  });

  if (res.status === 401) {
    clearSession();
    throw { isError: true, message: 'Sign in required.' };
  }

  const data = await res.json().catch(() => ({ isError: true, message: `HTTP ${res.status}` }));
  if (!res.ok) throw data;
  return data as T;
}

export const api = {
  upload,
  get:    <T>(path: string)               => request<T>('GET',    path),
  post:   <T>(path: string, body: unknown) => request<T>('POST',   path, body),
  put:    <T>(path: string, body: unknown) => request<T>('PUT',    path, body),
  delete: <T>(path: string)               => request<T>('DELETE', path),
  getRaw,
};

/** Resolve an API path to a fully-qualified URL for use with EventSource. */
export function sseUrl(path: string): string {
  return `${BASE}${path}`;
}