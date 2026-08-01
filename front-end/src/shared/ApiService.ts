const BASE = process.env.NEXT_PUBLIC_API_URL ?? '';
let _accessToken: string | null = null;

export function setAccessToken(token: string | null) {
  _accessToken = token;
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  if (_accessToken) headers['Authorization'] = `Bearer ${_accessToken}`;
  if (body) headers['Content-Type'] = 'application/json';

  const res = await fetch(`${BASE}${path}`, {
    method,
    headers,
    body: body ? JSON.stringify(body) : undefined,
  });

  if (res.status === 204) return undefined as T;

  const data = await res.json().catch(() => ({ isError: true, message: `HTTP ${res.status}` }));
  if (!res.ok) throw data;
  return data as T;
}

async function getRaw(path: string): Promise<string> {
  const headers: Record<string, string> = {};
  if (_accessToken) headers['Authorization'] = `Bearer ${_accessToken}`;

  const res = await fetch(`${BASE}${path}`, { headers });
  if (!res.ok) {
    const data = await res.json().catch(() => ({ message: `HTTP ${res.status}` }));
    throw data;
  }
  return res.text();
}

export const api = {
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
